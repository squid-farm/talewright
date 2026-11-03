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
                scene = new SceneState(start, line, indent, length,
                    content[SceneVocabulary.SceneHeader.Length..].Trim().ToString(), lineBuffer, partBuffer, clauseBuffer,
                    cancellationToken);
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

    private static int SkipSpaces(string text, int pos)
    {
        while (pos < text.Length && char.IsWhiteSpace(text[pos]))
            pos++;
        return pos;
    }

    private static int Lead(ReadOnlySpan<char> text) => text.Length - text.TrimStart().Length;

    private sealed class SceneState
    {
        private readonly int _headerIndent;
        private readonly int _headerLength;
        private readonly string _id;
        private readonly List<TaleLinePart> _parts;
        private readonly List<TaleClause> _clauses;
        private readonly CancellationToken _cancellationToken;
        private readonly List<TaleSyntaxDiagnostic> _diagnostics = [];
        // Open {if} blocks: header-relative line and column, and whether {else} has been seen.
        private readonly List<(int Line, int Column, bool InElse)> _blocks = [];
        private int _suppressedDepth;
        private (int Line, int Column) _suppressedAt;
        private bool _hasHub;
        private bool _hasHubLimit;
        // The skill check whose outcome lines are being attached: its line and column, and the outcomes seen so far.
        private (int Line, int Column, bool InsideBlock, bool Success, bool Failure)? _check;

        /// <summary>Starts a scene, reusing the previous scene's buffers.</summary>
        public SceneState(int start, int line, int headerIndent, int headerLength, string id,
            List<TaleLineSyntax> lines, List<TaleLinePart> parts, List<TaleClause> clauses,
            CancellationToken cancellationToken)
        {
            Start = start;
            Line = line;
            _headerIndent = headerIndent;
            _headerLength = headerLength;
            _id = id;
            Lines = lines;
            _parts = parts;
            _clauses = clauses;
            _cancellationToken = cancellationToken;
            lines.Clear();
            parts.Clear();
            clauses.Clear();
        }

        public int Start { get; }
        public int Line { get; }
        public bool HeaderDone;
        public List<TaleLineSyntax> Lines { get; }

        /// <summary>Adds the line's parts and block state, reporting structural errors.</summary>
        public TaleLineSyntax Describe(TaleLineSyntax line, ReadOnlySpan<char> content)
        {
            if (_check is not null && TryAttach(ref line, content))
                return line;

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
                case TaleLineKind.Choice:
                {
                    var first = _clauses.Count;
                    return WithClauses(line, first, Choice(line, content[1..].ToString()));
                }
                case TaleLineKind.SkillCheckBlock:
                {
                    var first = _clauses.Count;
                    return WithClauses(line, first, BlockCheck(line, content));
                }
                case TaleLineKind.SuccessOutcome or TaleLineKind.FailureOutcome:
                    Error(line, CompilerDiagnosticCodes.MalformedSkillCheckOutcome,
                        $"{SceneVocabulary.SuccessOutcome}/{SceneVocabulary.FailureOutcome} lines must directly follow " +
                        $"a {SceneVocabulary.SkillCheck} choice or be inside a '{SceneVocabulary.SkillCheckBlock}' block.");
                    return line with { IsInvalid = true };
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

        // ---- choices -------------------------------------------------------------------------------------------------

        /// <summary>Adds a choice's clauses and returns whether the choice is dropped.</summary>
        /// <param name="line">The choice line.</param>
        /// <param name="rest">The line after the choice marker.</param>
        private bool Choice(TaleLineSyntax line, string rest)
        {
            var indent = line.Indent;
            var pos = SkipSpaces(rest, 0);
            var hasLabel = false;
            if (pos < rest.Length && rest[pos] == SceneVocabulary.OpenBracket)
            {
                var labelScan = DelimiterScanner.Scan(rest, DelimiterScanMode.ChoiceLabel, pos, _cancellationToken);
                if (!labelScan.TryGetClose(pos, out var close))
                {
                    UnclosedLabel(line, rest, pos, labelScan);
                    return true;
                }

                Clause(new(TaleClauseKind.Label, indent + 1 + pos, indent + 2 + pos, close - pos - 1));
                hasLabel = true;
                pos = SkipSpaces(rest, close + 1);
            }

            var hasDivert = false;
            var hasRequires = false;
            var hasEffects = false;
            var clauseScan = DelimiterScanner.Scan(rest, DelimiterScanMode.CodeGroup, pos, _cancellationToken);
            while (pos < rest.Length)
            {
                var column = indent + 1 + pos;
                var remaining = rest.AsSpan(pos);
                if (remaining.StartsWith(SceneVocabulary.ChoiceRequires, StringComparison.Ordinal))
                {
                    if (hasRequires)
                        Error(line, CompilerDiagnosticCodes.MalformedChoice,
                            $"Duplicate '{SceneVocabulary.ChoiceRequires}{ExpressionVocabulary.CloseParen}' condition. " +
                            "A choice accepts only one.", column);
                    var valueStart = pos + SceneVocabulary.ChoiceRequires.Length;
                    if (!clauseScan.TryGetClose(pos, out var end))
                    {
                        Clause(new(TaleClauseKind.UnclosedRequires, column, indent + 1 + valueStart,
                            rest.Length - valueStart));
                        Error(line, CompilerDiagnosticCodes.MalformedChoice,
                            $"'{SceneVocabulary.ChoiceRequires}' is missing its closing " +
                            $"'{ExpressionVocabulary.CloseParen}'.", column);
                        return true;
                    }

                    Clause(new(TaleClauseKind.Requires, column, indent + 1 + valueStart, end - valueStart));
                    hasRequires = true;
                    pos = SkipSpaces(rest, end + 1);
                }
                else if (remaining[0] == SceneVocabulary.InlineOpen)
                {
                    if (hasEffects)
                        Error(line, CompilerDiagnosticCodes.MalformedChoice,
                            "Duplicate effect block. A choice accepts only one.", column);
                    if (!clauseScan.TryGetClose(pos, out var end))
                    {
                        Clause(new(TaleClauseKind.UnclosedEffects, column, column + 1, rest.Length - pos - 1));
                        Error(line, CompilerDiagnosticCodes.MalformedChoice,
                            $"Effect block is missing its closing '{SceneVocabulary.InlineClose}'.", column);
                        return true;
                    }

                    Clause(new(TaleClauseKind.Effects, column, column + 1, end - pos - 1));
                    hasEffects = true;
                    pos = SkipSpaces(rest, end + 1);
                }
                else if (remaining.StartsWith(SceneVocabulary.Divert, StringComparison.Ordinal))
                {
                    hasDivert = true;
                    var targetEnd = SkipSpaces(rest, pos + SceneVocabulary.Divert.Length);
                    var checkpoint = _cancellationToken.CanBeCanceled ? new CancellationCheckpoint(_cancellationToken) : null;
                    while (targetEnd < rest.Length && !char.IsWhiteSpace(rest[targetEnd]) &&
                           rest[targetEnd] is not (SceneVocabulary.InlineOpen or ExpressionVocabulary.OpenParen) &&
                           !rest.AsSpan(targetEnd).StartsWith(SceneVocabulary.Divert, StringComparison.Ordinal))
                    {
                        checkpoint?.Check();
                        targetEnd++;
                    }

                    var targetStart = pos + SceneVocabulary.Divert.Length;
                    DivertClause(line, column, column + SceneVocabulary.Divert.Length,
                        rest.AsSpan(targetStart, targetEnd - targetStart));
                    var trailing = SkipSpaces(rest, targetEnd);
                    if (trailing < rest.Length)
                        TrailingChoiceText(line, rest, trailing, indent + 1, afterCheck: false, insideBlock: false);
                    pos = rest.Length;
                }
                else if (remaining.StartsWith(SceneVocabulary.SkillCheck, StringComparison.Ordinal))
                {
                    if (!hasLabel)
                    {
                        Error(line, CompilerDiagnosticCodes.MalformedChoice,
                            $"Textless choices need '{SceneVocabulary.Divert} target' or '{SceneVocabulary.Divert} " +
                            $"{SceneVocabulary.End}'; skill checks require a '{SceneVocabulary.OpenBracket}label" +
                            $"{SceneVocabulary.CloseBracket}'.");
                        return true;
                    }

                    var specOffset = pos + SceneVocabulary.SkillCheck.Length;
                    SkillCheck(line, column, rest[specOffset..], clauseScan, specOffset, insideBlock: false);
                    _check = (line.Line, column, false, false, false);
                    pos = rest.Length;
                }
                else if (remaining[0] == ExpressionVocabulary.OpenParen &&
                         ChoiceSyntax.TryReadSpacedRequires(rest, pos, _cancellationToken, out var expressionStart))
                {
                    var expressionEnd = clauseScan.TryGetClose(pos, out var end) ? end : rest.Length;
                    Clause(new(TaleClauseKind.MisspelledRequires, column, indent + 1 + expressionStart,
                        expressionEnd - expressionStart));
                    var spelling = ChoiceSyntax.Excerpt(rest.AsSpan(pos, expressionStart - pos));
                    Error(line, CompilerDiagnosticCodes.MalformedChoice,
                        $"Choice conditions use the exact marker " +
                        $"'{SceneVocabulary.ChoiceRequires}{ExpressionVocabulary.CloseParen}'. " +
                        $"'{spelling}' is not recognized.", column);
                    return true;
                }
                else
                {
                    var help = !hasLabel
                        ? $"Use '{SceneVocabulary.ChoiceMarker} {SceneVocabulary.OpenBracket}label" +
                          $"{SceneVocabulary.CloseBracket} ...' or a textless '{SceneVocabulary.ChoiceMarker} ... " +
                          $"{SceneVocabulary.Divert} target'."
                        : $"Expected {SceneVocabulary.ChoiceRequires} …{ExpressionVocabulary.CloseParen}, " +
                          $"{SceneVocabulary.InlineOpen} effects " +
                          $"{SceneVocabulary.InlineClose}, {SceneVocabulary.Divert} target or {SceneVocabulary.SkillCheck}.";
                    var excerpt = ChoiceSyntax.Excerpt(remaining);
                    var bareTarget = IdentifierRules.IsValid(remaining)
                        ? $" Insert '{SceneVocabulary.Divert}' before the target."
                        : "";
                    Error(line, CompilerDiagnosticCodes.MalformedChoice,
                        $"Unexpected '{excerpt}' in choice.{bareTarget} {help}", column);
                    return true;
                }
            }

            if (!hasLabel && !hasDivert)
            {
                Error(line, CompilerDiagnosticCodes.MalformedChoice,
                    $"Textless choices need '{SceneVocabulary.Divert} target' or '{SceneVocabulary.Divert} " +
                    $"{SceneVocabulary.End}'.");
                return true;
            }

            return false;
        }

        private void UnclosedLabel(TaleLineSyntax line, string rest, int open, DelimiterScanResult labelScan)
        {
            var unclosedBrace = -1;
            var checkpoint = _cancellationToken.CanBeCanceled ? new CancellationCheckpoint(_cancellationToken) : null;
            foreach (var issue in labelScan.Issues)
            {
                checkpoint?.Check();
                if (issue.Kind == DelimiterKind.Brace &&
                    issue.Issue is (DelimiterIssueKind.UnclosedOpener or DelimiterIssueKind.MismatchedOpener) &&
                    (unclosedBrace < 0 || issue.Offset < unclosedBrace))
                    unclosedBrace = issue.Offset;
            }

            var unclosedImage = unclosedBrace >= 0 && ImageSyntax.IsCall(rest.AsSpan(unclosedBrace + 1));
            var hint = ChoiceSyntax.FindMissingBracketHint(rest, open, labelScan, _cancellationToken);
            // Clauses after the label are not reader text, so the partial label stops before them.
            var end = unclosedImage ? Math.Min(hint.LabelEnd, unclosedBrace) : hint.LabelEnd;
            var column = line.Indent + 1;
            Clause(new(TaleClauseKind.UnclosedLabel, column + open, column + open + 1, end - open - 1));
            if (unclosedBrace >= 0)
            {
                Clause(new(unclosedImage ? TaleClauseKind.UnclosedImage : TaleClauseKind.UnclosedBrace,
                    column + unclosedBrace, column + unclosedBrace + 1, rest.Length - unclosedBrace - 1));
            }

            var message = $"Choice label is missing its closing '{SceneVocabulary.CloseBracket}'.";
            if (hint.Marker is not null)
                message += $" Place '{SceneVocabulary.CloseBracket}' before '{hint.Marker}'.";
            Error(line, CompilerDiagnosticCodes.MalformedChoice, message, column + open);
        }

        private void DivertClause(TaleLineSyntax line, int start, int column, ReadOnlySpan<char> text)
        {
            var target = text.Trim();
            var valueStart = column + Lead(text);
            var error = DivertTargetError(target);
            Clause(new(TaleClauseKind.Divert, start, valueStart, target.Length) { IsInvalid = error is not null });
            if (error is { } found)
                Error(line, found.Code, found.Message, valueStart);
        }

        private void TrailingChoiceText(TaleLineSyntax line, string text, int pos, int column, bool afterCheck,
            bool insideBlock)
        {
            var remaining = text.AsSpan(pos);
            if (remaining[0] == ExpressionVocabulary.OpenParen &&
                ChoiceSyntax.TryReadSpacedRequires(text, pos, _cancellationToken, out _))
            {
                var terminal = afterCheck ? "the skill check" : "the divert";
                Error(line, CompilerDiagnosticCodes.MalformedChoice,
                    $"Choice conditions use the exact marker '{SceneVocabulary.ChoiceRequires}" +
                    $"{ExpressionVocabulary.CloseParen}' and cannot follow {terminal}.", column + pos);
                return;
            }

            var message = remaining[0] switch
            {
                SceneVocabulary.InlineOpen when insideBlock =>
                    $"Effect block is misplaced after the skill check. Put effects on a " +
                    $"'{SceneVocabulary.SuccessOutcome}' or '{SceneVocabulary.FailureOutcome}' line.",
                SceneVocabulary.InlineOpen when afterCheck =>
                    $"Effect block is misplaced after the skill check. Put '{SceneVocabulary.InlineOpen} effects " +
                    $"{SceneVocabulary.InlineClose}' before '{SceneVocabulary.SkillCheck}'.",
                SceneVocabulary.InlineOpen =>
                    $"Effect block is misplaced after the divert. Put '{SceneVocabulary.InlineOpen} effects " +
                    $"{SceneVocabulary.InlineClose}' before '{SceneVocabulary.Divert}'.",
                _ when remaining.StartsWith(SceneVocabulary.ChoiceRequires, StringComparison.Ordinal) && insideBlock =>
                    $"'{SceneVocabulary.ChoiceRequires}{ExpressionVocabulary.CloseParen}' is misplaced after the " +
                    "block skill check.",
                _ when remaining.StartsWith(SceneVocabulary.ChoiceRequires, StringComparison.Ordinal) && afterCheck =>
                    $"'{SceneVocabulary.ChoiceRequires}{ExpressionVocabulary.CloseParen}' is misplaced after the " +
                    $"skill check. Put the condition before '{SceneVocabulary.SkillCheck}'.",
                _ when remaining.StartsWith(SceneVocabulary.ChoiceRequires, StringComparison.Ordinal) =>
                    $"'{SceneVocabulary.ChoiceRequires}{ExpressionVocabulary.CloseParen}' is misplaced after the " +
                    $"divert. Put the condition before '{SceneVocabulary.Divert}'.",
                _ when remaining.StartsWith(SceneVocabulary.Divert, StringComparison.Ordinal) && afterCheck =>
                    $"Divert is misplaced after the skill check. Outcomes belong on " +
                    $"'{SceneVocabulary.SuccessOutcome}' and '{SceneVocabulary.FailureOutcome}' lines.",
                _ => $"Unexpected '{ChoiceSyntax.Excerpt(remaining)}' after the " +
                     $"{(afterCheck ? "skill check" : "divert")}.",
            };
            Error(line, CompilerDiagnosticCodes.MalformedChoice, message, column + pos);
        }

        // ---- skill checks ----------------------------------------------------------------------------------------------

        /// <summary>Adds a <c>::: skill-check</c> block's clauses and returns whether the block is invalid.</summary>
        private bool BlockCheck(TaleLineSyntax line, ReadOnlySpan<char> content)
        {
            var spec = content[SceneVocabulary.SkillCheckFence.Length..].TrimStart();
            if (!spec.StartsWith(SceneVocabulary.SkillCheck, StringComparison.Ordinal))
            {
                Error(line, CompilerDiagnosticCodes.InvalidBlock,
                    $"Only '{SceneVocabulary.SkillCheckBlock}' blocks are supported.");
                return true;
            }

            var keyword = line.Indent + content.Length - spec.Length;
            SkillCheck(line, keyword, spec[SceneVocabulary.SkillCheck.Length..].ToString(), null, 0, insideBlock: true);
            _check = (line.Line, line.Indent, true, false, false);
            return false;
        }

        /// <summary>
        /// Adds the clauses of a <c>[actor] stat dc expr</c> spec that starts after the <c>skill-check</c> keyword at
        /// <paramref name="keyword"/>.
        /// </summary>
        private void SkillCheck(TaleLineSyntax line, int keyword, string text, DelimiterScanResult? scan, int scanOffset,
            bool insideBlock)
        {
            var column = keyword + SceneVocabulary.SkillCheck.Length;
            var separator = text.IndexOf(SceneVocabulary.DifficultySeparator, StringComparison.Ordinal);
            if (separator >= 0)
            {
                scan ??= DelimiterScanner.Scan(text, DelimiterScanMode.CodeGroup, cancellationToken: _cancellationToken);
                var trailing = ChoiceSyntax.FindTrailingClause(text,
                    separator + SceneVocabulary.DifficultySeparator.Length, scan, scanOffset, _cancellationToken);
                if (trailing >= 0)
                {
                    TrailingChoiceText(line, text, trailing, column, afterCheck: true, insideBlock);
                    text = text[..trailing].TrimEnd();
                }
            }

            // Words are separated by spaces only; dc is the first word spelled exactly "dc".
            var words = 0;
            var difficultyWord = -1;
            var checkIndex = _clauses.Count;
            Clause(default);
            for (var pos = 0; pos < text.Length;)
            {
                if (text[pos] == ' ')
                {
                    pos++;
                    continue;
                }

                var end = text.IndexOf(' ', pos);
                if (end < 0)
                    end = text.Length;
                if (difficultyWord < 0)
                {
                    if (text.AsSpan(pos, end - pos).SequenceEqual(SceneVocabulary.Difficulty))
                        difficultyWord = words;
                    else if (words < 2)
                        Clause(new(TaleClauseKind.CheckWord, column + pos, column + pos, end - pos));
                }

                words++;
                pos = end;
            }

            separator = text.IndexOf(SceneVocabulary.DifficultySeparator, StringComparison.Ordinal);
            if (separator >= 0)
            {
                var value = separator + SceneVocabulary.DifficultySeparator.Length;
                Clause(new(TaleClauseKind.Difficulty, column + separator + 1, column + value, text.Length - value));
            }
            else if (text.EndsWith(SceneVocabulary.DifficultyPrefix, StringComparison.Ordinal))
            {
                Clause(new(TaleClauseKind.Difficulty, column + text.Length - SceneVocabulary.Difficulty.Length,
                    column + text.Length, 0));
            }

            var malformed = difficultyWord is < 1 or > 2 || difficultyWord == words - 1;
            _clauses[checkIndex] = new(TaleClauseKind.SkillCheck, keyword, column, text.Length)
            {
                IsInvalid = malformed,
                DifficultyWord = difficultyWord,
            };
            if (malformed)
            {
                Error(line, CompilerDiagnosticCodes.MalformedSkillCheck,
                    $"Skill checks look like '{SceneVocabulary.SkillCheck} {SceneVocabulary.OpenBracket}actor" +
                    $"{SceneVocabulary.CloseBracket} skill{SceneVocabulary.DifficultySeparator}12'.", column);
            }
        }

        /// <summary>Attaches the line to the open skill check if it continues it; otherwise closes the check.</summary>
        private bool TryAttach(ref TaleLineSyntax line, ReadOnlySpan<char> content)
        {
            var check = _check!.Value;
            if (line.Kind == TaleLineKind.Comment || (check.InsideBlock && line.Kind == TaleLineKind.Blank))
            {
                line = line with { IsAttached = true };
                return true;
            }

            if (check.InsideBlock && line is { Kind: TaleLineKind.SkillCheckBlock, IsBareFence: true })
            {
                line = line with { IsAttached = true };
                FinishCheck(closed: true);
                return true;
            }

            if (line.Kind is not (TaleLineKind.SuccessOutcome or TaleLineKind.FailureOutcome))
            {
                FinishCheck(closed: false);
                return false;
            }

            _check = line.Kind == TaleLineKind.SuccessOutcome ? check with { Success = true } : check with { Failure = true };
            var first = _clauses.Count;
            line = WithClauses(line, first, Outcome(line, content)) with { IsAttached = true };
            return true;
        }

        private void FinishCheck(bool closed)
        {
            var check = _check!.Value;
            _check = null;
            if (check.InsideBlock && !closed)
            {
                _diagnostics.Add(new(CompilerDiagnosticCodes.InvalidBlock,
                    $"'{SceneVocabulary.SkillCheckBlock}' block is never closed with " +
                    $"'{SceneVocabulary.SkillCheckFence}'.", check.Line, check.Column));
            }

            if (!check.Success || !check.Failure)
            {
                _diagnostics.Add(new(CompilerDiagnosticCodes.MalformedSkillCheckOutcome,
                    $"A skill check needs both a '{SceneVocabulary.SuccessOutcome}' and a " +
                    $"'{SceneVocabulary.FailureOutcome}' line.", check.Line, check.Column));
            }
        }

        /// <summary>Adds an outcome line's effects and divert clauses and returns whether the outcome is invalid.</summary>
        private bool Outcome(TaleLineSyntax line, ReadOnlySpan<char> content)
        {
            var prefix = line.Kind == TaleLineKind.SuccessOutcome
                ? SceneVocabulary.SuccessOutcome
                : SceneVocabulary.FailureOutcome;
            var text = content[prefix.Length..];
            var column = line.Indent + prefix.Length;
            var pos = text.Length - text.TrimStart().Length;
            if (pos < text.Length && text[pos] == SceneVocabulary.InlineOpen)
            {
                var scan = DelimiterScanner.Scan(text.ToString(), DelimiterScanMode.CodeGroup, pos, _cancellationToken);
                if (!scan.TryGetClose(pos, out var end))
                {
                    Clause(new(TaleClauseKind.UnclosedEffects, column + pos, column + pos + 1, text.Length - pos - 1));
                    Error(line, CompilerDiagnosticCodes.MalformedSkillCheckOutcome,
                        $"Outcome effect block is missing its closing '{SceneVocabulary.InlineClose}'.", column + pos);
                    return true;
                }

                Clause(new(TaleClauseKind.Effects, column + pos, column + pos + 1, end - pos - 1));
                pos = end + 1 + Lead(text[(end + 1)..]);
            }

            if (pos == text.Length)
                return false;
            if (!text[pos..].StartsWith(SceneVocabulary.Divert, StringComparison.Ordinal))
            {
                Error(line, CompilerDiagnosticCodes.MalformedSkillCheckOutcome,
                    $"Outcomes look like '{SceneVocabulary.SuccessOutcome} {SceneVocabulary.InlineOpen} effects " +
                    $"{SceneVocabulary.InlineClose} {SceneVocabulary.Divert} target'.", column + pos);
                return true;
            }

            DivertClause(line, column + pos, column + pos + SceneVocabulary.Divert.Length,
                text[(pos + SceneVocabulary.Divert.Length)..]);
            return false;
        }

        private void Clause(TaleClause clause) => _clauses.Add(clause);

        /// <summary>Points the line at the clauses added from <paramref name="first"/>.</summary>
        private TaleLineSyntax WithClauses(TaleLineSyntax line, int first, bool invalid) =>
            line with
            {
                PartIndex = _clauses.Count > first ? first + 1 : 0,
                ClauseCount = _clauses.Count - first,
                IsInvalid = invalid,
            };

        private TaleLineSyntax WithPart(TaleLineSyntax line, TaleLinePart part)
        {
            _parts.Add(part);
            return line with { PartIndex = _parts.Count };
        }

        private void Error(TaleLineSyntax line, string code, string message, int? column = null) =>
            _diagnostics.Add(new(code, message, line.Line, column ?? line.Indent));

        public TaleSceneSyntax Build(int endLine)
        {
            if (_check is not null)
                FinishCheck(closed: false);
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
                _parts.ToArray(), _clauses.ToArray(), endLine - Line, diagnostics.ToArray());
        }
    }
}
