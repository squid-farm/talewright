using Talewright.Core.Identity;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Talemark.Parsing;

/// <summary>
/// Splits a <c>.tale</c> file into scenes and classifies each line without consulting story symbols. The binder
/// (<see cref="TaleBinder"/>) turns this syntax into the story model.
/// </summary>
internal static partial class TaleSyntaxParser
{
    internal const int MaximumBlockConditionDepth = 64;
    private const string LineComment = "//";

    public static TaleFileSyntax Parse(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var preamble = new List<TaleLineSyntax>();
        var fileDiagnostics = new List<TaleSyntaxDiagnostic>();
        var scenes = new List<TaleSceneSyntax>();
        SceneState? scene = null;
        var lineBuffer = new List<TaleLineSyntax>();
        var partBuffer = new List<TaleLinePart>();
        var clauseBuffer = new List<TaleClause>();
        var start = 0;
        var line = 0;
        while (true)
        {
            if ((line & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var length = LineLength(text, start, out var next, out var hasNext);
            var raw = text.AsSpan(start, length);
            var content = raw.Trim();
            var indent = raw.Length - raw.TrimStart().Length;

            if (content.StartsWith(SceneVocabulary.SceneHeader, StringComparison.Ordinal))
            {
                if (scene is not null)
                    scenes.Add(scene.Build(line));
                var header = content[SceneVocabulary.SceneHeader.Length..];
                var idColumn = indent + SceneVocabulary.SceneHeader.Length + header.Length - header.TrimStart().Length;
                scene = new SceneState(start, line, indent, idColumn, length, header.Trim().ToString(), lineBuffer,
                    partBuffer, clauseBuffer, cancellationToken);
            }
            else if (scene is null)
            {
                var kind = content.StartsWith(LineComment, StringComparison.Ordinal) ? TaleLineKind.Comment
                    : content.Length == 0 ? TaleLineKind.Blank
                    : TaleLineKind.Prose;
                if (kind == TaleLineKind.Prose)
                {
                    fileDiagnostics.Add(new(CompilerDiagnosticCodes.TextOutsideSceneOrNoFiles,
                        $"Text before the first scene. Start a scene with '{SceneVocabulary.SceneHeader} scene.id'.",
                        line, indent));
                }

                preamble.Add(new TaleLineSyntax(kind, start, line, length, indent, content.Length));
            }
            else
            {
                var kind = Classify(content, cancellationToken, ref scene.HeaderDone, out var key, out var bareFence);
                scene.Lines.Add(scene.Describe(new TaleLineSyntax(kind, start - scene.Start, line - scene.Line, length,
                    indent, content.Length) { MetadataKey = key, IsBareFence = bareFence }, content));
            }

            line++;
            if (!hasNext)
                break;
            start = next;
        }

        if (scene is not null)
            scenes.Add(scene.Build(line));
        return new TaleFileSyntax(text, preamble.ToArray(), scenes.ToArray(), fileDiagnostics.ToArray(), line);
    }

    private static TaleLineKind Classify(ReadOnlySpan<char> line, CancellationToken cancellationToken, ref bool headerDone,
        out SceneMetadataKey? metadataKey, out bool bareFence)
    {
        metadataKey = null;
        bareFence = false;
        if (line.StartsWith(LineComment, StringComparison.Ordinal))
            return TaleLineKind.Comment;
        if (line.Length == 0)
            return TaleLineKind.Blank;

        if (line[0] == SceneVocabulary.MetadataMarker && !headerDone)
        {
            metadataKey = MetadataKey(line, trimKeyword: true);
            headerDone = metadataKey is null && !SceneVocabulary.IsClearMetadata(line);
            return TaleLineKind.Metadata;
        }

        headerDone = true;
        if (TryClassifyBlockDirective(line, cancellationToken, out var directive))
            return directive;

        switch (line)
        {
            case [SceneVocabulary.SpeechMarker, ..]:
                return TaleLineKind.Speech;
            case [SceneVocabulary.EffectsMarker, ..]:
                return TaleLineKind.Effects;
            case [SceneVocabulary.ChoiceMarker]:
            case [SceneVocabulary.ChoiceMarker, ' ' or '\t' or SceneVocabulary.OpenBracket, ..]:
                // A choice marker followed by a label opener or divert marker is a choice; adjacent markers are emphasis.
                return TaleLineKind.Choice;
            case [ExpressionVocabulary.Minus, ExpressionVocabulary.Greater, ..]:
                return TaleLineKind.Divert;
        }

        if (line.StartsWith(SceneVocabulary.SkillCheckFence, StringComparison.Ordinal))
        {
            bareFence = line.Length == SceneVocabulary.SkillCheckFence.Length;
            return TaleLineKind.SkillCheckBlock;
        }

        if (line.StartsWith(SceneVocabulary.SuccessOutcome, StringComparison.Ordinal))
            return TaleLineKind.SuccessOutcome;
        if (line.StartsWith(SceneVocabulary.FailureOutcome, StringComparison.Ordinal))
            return TaleLineKind.FailureOutcome;
        if (line[0] == SceneVocabulary.MetadataMarker && MetadataKey(line, trimKeyword: false) is not null)
            return TaleLineKind.MisplacedMetadata;
        return TaleLineKind.Prose;
    }

    /// <remarks>
    /// The keyword ends at the first space. Header metadata trims it (so <c>@once\t x</c> is a directive) while the
    /// misplaced-metadata check does not, matching the binder's historical behavior.
    /// </remarks>
    private static SceneMetadataKey? MetadataKey(ReadOnlySpan<char> line, bool trimKeyword)
    {
        var keyword = line[1..];
        var space = keyword.IndexOf(' ');
        if (space >= 0)
            keyword = keyword[..space];
        if (trimKeyword)
            keyword = keyword.Trim();
        return SceneVocabulary.TryGetMetadataKey(keyword, out var key) ? key : null;
    }

    private static bool TryClassifyBlockDirective(ReadOnlySpan<char> line, CancellationToken cancellationToken,
        out TaleLineKind kind)
    {
        kind = default;
        if (line[0] != SceneVocabulary.InlineOpen)
            return false;

        var text = line.ToString();
        var scan = DelimiterScanner.Scan(text, DelimiterScanMode.ReaderText, cancellationToken: cancellationToken);
        if (!scan.TryGetClose(0, out var close) || close != text.Length - 1)
            return false;

        var inner = line[1..^1].Trim();
        if (inner.SequenceEqual(SceneVocabulary.Else))
            kind = TaleLineKind.BlockElse;
        else if (inner.SequenceEqual(SceneVocabulary.EndIf))
            kind = TaleLineKind.BlockEndIf;
        else if (inner.StartsWith(SceneVocabulary.IfPrefix, StringComparison.Ordinal))
            kind = TaleLineKind.BlockIf;
        else
            return false;
        return true;
    }

    private static int LineLength(string text, int start, out int nextStart, out bool hasNext)
    {
        var remainder = text.AsSpan(start);
        var lineBreak = remainder.IndexOfAny("\r\n\u0085\u2028\u2029");
        var end = lineBreak < 0 ? text.Length : start + lineBreak;
        if (lineBreak < 0)
        {
            nextStart = end;
            hasNext = false;
            return end - start;
        }

        nextStart = end + 1;
        if (text[end] == '\r' && nextStart < text.Length && text[nextStart] == '\n')
            nextStart++;
        hasNext = true;
        return end - start;
    }

    /// <summary>The structural error for a divert target, or null when it is <c>END</c> or a valid scene id.</summary>
    internal static (string Code, string Message)? DivertTargetError(ReadOnlySpan<char> id)
    {
        if (id.SequenceEqual(SceneVocabulary.End))
            return null;
        if (id.Length == 0)
        {
            return (CompilerDiagnosticCodes.MissingDivertTarget,
                $"Divert needs a target: '{SceneVocabulary.Divert} scene.id' or '{SceneVocabulary.Divert} " +
                $"{SceneVocabulary.End}'.");
        }

        if (!IdentifierRules.IsValid(id))
        {
            return (CompilerDiagnosticCodes.InvalidSceneId,
                $"'{id}' is not a valid scene id. Use an ASCII letter or '_' followed by letters, digits, '_' and " +
                "single '-' or '.' separators.");
        }

        return null;
    }

    private static int Lead(ReadOnlySpan<char> text) => text.Length - text.TrimStart().Length;
}
