using Talewright.Core.Identity;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Talemark.Parsing;

/// <summary>
/// Splits a <c>.tale</c> file into scenes and classifies each line without consulting story symbols. The binder
/// (<see cref="TaleParser"/>) turns this syntax into the story model.
/// </summary>
internal static class TaleSyntaxParser
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
                scene = new SceneState(start, line, indent, length,
                    content[SceneVocabulary.SceneHeader.Length..].Trim().ToString(), lineBuffer, partBuffer);
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
            headerDone = metadataKey is null;
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

    /// <summary>Reads an <c>@hub</c> value: a tag, <c>all(tag, tag)</c> or <c>any(tag, tag)</c> with distinct nonempty tags.</summary>
    internal static bool TryReadHub(string value, out string[] tags, out bool all)
    {
        SceneHubMode? hubMode = SceneVocabulary.TryGetHubMode(value, out var parsedHubMode) ? parsedHubMode : null;
        var hubPrefix = hubMode is { } mode ? SceneVocabulary.HubModePrefix(mode) : string.Empty;
        tags = hubMode is not null && value.EndsWith(ExpressionVocabulary.CloseParen)
            ? value[hubPrefix.Length..^1].Split(ExpressionVocabulary.ArgumentSeparator, StringSplitOptions.TrimEntries)
            : [value];
        all = hubMode == SceneHubMode.All;
        return !tags.Any(t => string.IsNullOrWhiteSpace(t) ||
                              t.Any(c => char.IsWhiteSpace(c) || c is ExpressionVocabulary.OpenParen or
                                  ExpressionVocabulary.CloseParen or ExpressionVocabulary.ArgumentSeparator)) &&
               tags.Distinct(StringComparer.Ordinal).Count() == tags.Length;
    }

    internal static string[] ReadTags(string value) => value.Split(ExpressionVocabulary.ArgumentSeparator,
        StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static int Lead(ReadOnlySpan<char> text) => text.Length - text.TrimStart().Length;

    private sealed class SceneState
    {
        private readonly int _headerIndent;
        private readonly int _headerLength;
        private readonly string _id;
        private readonly List<TaleLinePart> _parts;
        private readonly List<TaleSyntaxDiagnostic> _diagnostics = [];
        // Open {if} blocks: header-relative line and column, and whether {else} has been seen.
        private readonly List<(int Line, int Column, bool InElse)> _blocks = [];
        private int _suppressedDepth;
        private (int Line, int Column) _suppressedAt;
        private bool _hasHub;
        private bool _hasHubLimit;

        /// <summary>Starts a scene, reusing the previous scene's buffers.</summary>
        public SceneState(int start, int line, int headerIndent, int headerLength, string id,
            List<TaleLineSyntax> lines, List<TaleLinePart> parts)
        {
            Start = start;
            Line = line;
            _headerIndent = headerIndent;
            _headerLength = headerLength;
            _id = id;
            Lines = lines;
            _parts = parts;
            lines.Clear();
            parts.Clear();
        }

        public int Start { get; }
        public int Line { get; }
        public bool HeaderDone;
        public List<TaleLineSyntax> Lines { get; }

        /// <summary>Adds the line's parts and block state, reporting structural errors.</summary>
        public TaleLineSyntax Describe(TaleLineSyntax line, ReadOnlySpan<char> content)
        {
            if (_suppressedDepth > 0)
            {
                if (line.Kind == TaleLineKind.BlockIf)
                    _suppressedDepth++;
                else if (line.Kind == TaleLineKind.BlockEndIf)
                    _suppressedDepth--;
                return line with { IsSuppressed = true };
            }

            switch (line.Kind)
            {
                case TaleLineKind.Metadata:
                    return Metadata(line, content);
                case TaleLineKind.BlockIf or TaleLineKind.BlockElse or TaleLineKind.BlockEndIf:
                    return Block(line, content);
                case TaleLineKind.Speech:
                    return Speech(line, content);
                case TaleLineKind.Effects:
                    var effects = content[1..];
                    return WithPart(line, new(0, 0, line.Indent + 1 + Lead(effects), effects.Trim().Length));
                case TaleLineKind.Divert:
                    return Divert(line, content);
                case TaleLineKind.MisplacedMetadata:
                    Error(line, CompilerDiagnosticCodes.InvalidSceneMetadata,
                        $"Metadata ({SceneVocabulary.MetadataMarker}{SceneVocabulary.Title}, " +
                        $"{SceneVocabulary.MetadataMarker}{SceneVocabulary.Tags}, …) must come directly after the " +
                        $"'{SceneVocabulary.SceneHeader}' line.");
                    return line with { IsInvalid = true };
                default:
                    return line;
            }
        }

        private TaleLineSyntax Metadata(TaleLineSyntax line, ReadOnlySpan<char> content)
        {
            var rest = content[1..];
            var space = rest.IndexOf(' ');
            var value = space < 0 ? ReadOnlySpan<char>.Empty : rest[(space + 1)..];
            line = WithPart(line, new(line.Indent + 1, space < 0 ? rest.Length : space,
                space < 0 ? line.Indent + content.Length : line.Indent + space + 2 + Lead(value), value.Trim().Length));
            if (line.MetadataKey is not { } key)
                return line;

            if (key is SceneMetadataKey.Hub or SceneMetadataKey.HubLimit)
            {
                ref var seen = ref key == SceneMetadataKey.Hub ? ref _hasHub : ref _hasHubLimit;
                if (seen)
                {
                    Error(line, CompilerDiagnosticCodes.InvalidSceneMetadata,
                        $"Duplicate @{SceneVocabulary.Spelling(key)} directive.");
                    return line with { IsInvalid = true };
                }

                seen = true;
            }

            value = value.Trim();
            switch (key)
            {
                case SceneMetadataKey.Priority when !int.TryParse(value, out _):
                    Error(line, CompilerDiagnosticCodes.NumberTooLarge,
                        $"{SceneVocabulary.MetadataMarker}{SceneVocabulary.Priority} needs a whole number.",
                        _parts[^1].MetadataValueColumn(line));
                    return line with { IsInvalid = true };
                case SceneMetadataKey.Hub when !TryReadHub(value.ToString(), out _, out _):
                    Error(line, CompilerDiagnosticCodes.InvalidSceneMetadata,
                        $"{SceneVocabulary.MetadataMarker}{SceneVocabulary.Hub} needs a tag or " +
                        $"{SceneVocabulary.HubAllPrefix}tag{ExpressionVocabulary.ArgumentSeparator} tag" +
                        $"{ExpressionVocabulary.CloseParen}/{SceneVocabulary.HubAnyPrefix}tag" +
                        $"{ExpressionVocabulary.ArgumentSeparator} tag{ExpressionVocabulary.CloseParen} " +
                        "with distinct nonempty tags.");
                    return line with { IsInvalid = true };
                case SceneMetadataKey.HubLimit when !int.TryParse(value, out var limit) || limit <= 0:
                    Error(line, CompilerDiagnosticCodes.InvalidSceneMetadata,
                        $"{SceneVocabulary.MetadataMarker}{SceneVocabulary.HubLimit} needs a positive whole number.");
                    return line with { IsInvalid = true };
                default:
                    return line;
            }
        }

        private TaleLineSyntax Block(TaleLineSyntax line, ReadOnlySpan<char> content)
        {
            var keyword = line.Kind switch
            {
                TaleLineKind.BlockIf => SceneVocabulary.If,
                TaleLineKind.BlockElse => SceneVocabulary.Else,
                _ => SceneVocabulary.EndIf,
            };
            var head = line.Indent + content.IndexOf(keyword, StringComparison.Ordinal);
            if (line.Kind != TaleLineKind.BlockIf)
                return Balance(WithPart(line, new(head, keyword.Length, 0, 0)));
            if (_blocks.Count == MaximumBlockConditionDepth)
            {
                Error(line, CompilerDiagnosticCodes.NestingLimitExceeded,
                    $"Conditions are nested more than {MaximumBlockConditionDepth} levels deep.");
                _suppressedDepth = 1;
                _suppressedAt = (line.Line, line.Indent);
                return WithPart(line, new(head, keyword.Length, 0, 0)) with { IsInvalid = true };
            }

            _blocks.Add((line.Line, line.Indent, false));
            var inner = content[1..^1];
            return WithPart(line, new(head, keyword.Length,
                line.Indent + 1 + Lead(inner) + SceneVocabulary.IfPrefix.Length,
                inner.Trim().Length - SceneVocabulary.IfPrefix.Length));
        }

        private TaleLineSyntax Balance(TaleLineSyntax line)
        {
            switch (line.Kind)
            {
                case TaleLineKind.BlockElse when _blocks.Count == 0 || _blocks[^1].InElse:
                    Error(line, CompilerDiagnosticCodes.UnbalancedCondition,
                        $"{SceneVocabulary.InlineOpen}{SceneVocabulary.Else}{SceneVocabulary.InlineClose} without a " +
                        $"matching {SceneVocabulary.InlineOpen}{SceneVocabulary.If}{SceneVocabulary.InlineClose}.");
                    return line with { IsInvalid = true };
                case TaleLineKind.BlockElse:
                    _blocks[^1] = _blocks[^1] with { InElse = true };
                    return line;
                case TaleLineKind.BlockEndIf when _blocks.Count == 0:
                    Error(line, CompilerDiagnosticCodes.UnbalancedCondition,
                        $"{SceneVocabulary.InlineOpen}{SceneVocabulary.EndIf}{SceneVocabulary.InlineClose} without a " +
                        $"matching {SceneVocabulary.InlineOpen}{SceneVocabulary.If}{SceneVocabulary.InlineClose}.");
                    return line with { IsInvalid = true };
                default:
                    _blocks.RemoveAt(_blocks.Count - 1);
                    return line;
            }
        }

        private TaleLineSyntax Speech(TaleLineSyntax line, ReadOnlySpan<char> content)
        {
            var body = content[1..];
            var colon = body.IndexOf(ExpressionVocabulary.Separator);
            var speaker = (colon < 0 ? body : body[..colon]).Trim();
            var headStart = line.Indent + 1 + Lead(body);
            if (colon <= 0 || !IdentifierRules.IsValid(speaker))
            {
                line = WithPart(line, new(headStart, speaker.Length, 0, 0));
                Error(line, CompilerDiagnosticCodes.MalformedDialogue,
                    $"Speech lines look like '{SceneVocabulary.SpeechMarker} who{ExpressionVocabulary.Separator} " +
                    "text', where who is a character id.");
                return line with { IsInvalid = true };
            }

            var text = body[(colon + 1)..];
            return WithPart(line, new(headStart, speaker.Length, line.Indent + colon + 2 + Lead(text), text.Trim().Length));
        }

        private TaleLineSyntax Divert(TaleLineSyntax line, ReadOnlySpan<char> content)
        {
            var text = content[SceneVocabulary.Divert.Length..];
            var target = text.Trim();
            var valueStart = line.Indent + SceneVocabulary.Divert.Length + Lead(text);
            line = WithPart(line, new(0, 0, valueStart, target.Length));
            if (DivertTargetError(target) is not { } error)
                return line;
            Error(line, error.Code, error.Message, valueStart);
            return line with { IsInvalid = true };
        }

        private TaleLineSyntax WithPart(TaleLineSyntax line, TaleLinePart part)
        {
            _parts.Add(part);
            return line with { PartIndex = _parts.Count };
        }

        private void Error(TaleLineSyntax line, string code, string message, int? column = null) =>
            _diagnostics.Add(new(code, message, line.Line, column ?? line.Indent));

        public TaleSceneSyntax Build(int endLine)
        {
            const string unclosed = "block is never closed with ";
            var diagnostics = new List<TaleSyntaxDiagnostic>(_diagnostics.Count + 1);
            if (!IdentifierRules.IsValid(_id) || _id == SceneVocabulary.End)
            {
                diagnostics.Add(new(CompilerDiagnosticCodes.InvalidSceneId,
                    $"'{_id}' is not a valid scene id. Use an ASCII letter or '_' followed by letters, digits, '_' " +
                    "and single '-' or '.' separators (e.g. temple.entrance).", 0, _headerIndent));
            }

            diagnostics.AddRange(_diagnostics);
            var message = $"{SceneVocabulary.InlineOpen}{SceneVocabulary.If}{SceneVocabulary.InlineClose} {unclosed}" +
                          $"{SceneVocabulary.InlineOpen}{SceneVocabulary.EndIf}{SceneVocabulary.InlineClose}.";
            if (_suppressedDepth > 0)
                diagnostics.Add(new(CompilerDiagnosticCodes.UnbalancedCondition, message, _suppressedAt.Line,
                    _suppressedAt.Column));
            for (var index = _blocks.Count - 1; index >= 0; index--)
                diagnostics.Add(new(CompilerDiagnosticCodes.UnbalancedCondition, message, _blocks[index].Line,
                    _blocks[index].Column));
            return new TaleSceneSyntax(Start, Line, _headerIndent, _headerLength, _id, Lines.ToArray(),
                _parts.ToArray(), endLine - Line, diagnostics.ToArray());
        }
    }
}
