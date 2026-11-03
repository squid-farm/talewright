using Talewright.Core.Identity;
using Talewright.Narrative.Templates;
using Talewright.Rules.Checks;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Model;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Talemark.Parsing;

/// <summary>A compiled scene plus where it was declared (for validator diagnostics).</summary>
internal sealed record ParsedScene(SceneDefinition Definition, SourceLocation Location)
{
    public required int EndLine { get; init; }
    public bool HasExplicitPriority { get; init; }
    public bool DroppedMalformedChoice { get; init; }
}

/// <summary>
/// Line-oriented parser for <c>.tale</c> files. Each line is classified by its first characters (<c>===</c>,
/// <c>@</c>, <c>&gt;</c>, <c>~</c>, <c>*</c>, <c>-&gt;</c>, <c>:::</c>, <c>{if}</c>…); everything else is prose,
/// grouped into paragraphs by blank lines and handed to <see cref="InlineParser"/>.
/// </summary>
/// <remarks>
/// Block-level conditions retain their nested steps so the runtime decides each branch once.
/// </remarks>
internal sealed partial class TaleParser
{
    private const int MaximumBlockConditionDepth = 64;

    private readonly string _file;
    private readonly string _text;
    private readonly StorySymbols _symbols;
    private readonly DiagnosticBag _diagnostics;
    private readonly Action<string, SourceLocation>? _discoverEffects;
    private readonly Action<SceneId, SourceLocation>? _discoverScene;
    private readonly CancellationToken _cancellationToken;
    private int _linesSinceCancellationCheck;
    private readonly List<ParsedScene> _scenes = [];
    private SceneBuilder? _scene;
    private int _index;
    private int _lineStart;
    private int _lineLength;
    private int _nextLineStart;
    private int _nextLineLength;
    private int _afterNextLineStart;
    private bool _hasCurrentLine = true;
    private bool _hasNextLine;
    private bool _hasAfterNextLine;

    private TaleParser(string file, string text, StorySymbols symbols, DiagnosticBag diagnostics,
        Action<string, SourceLocation>? discoverEffects = null, Action<SceneId, SourceLocation>? discoverScene = null,
        CancellationToken cancellationToken = default)
    {
        _file = file;
        _text = text;
        _lineLength = GetLineLength(0, out _nextLineStart, out _hasNextLine);
        if (_hasNextLine)
        {
            _nextLineLength = GetLineLength(_nextLineStart, out _afterNextLineStart, out _hasAfterNextLine);
        }
        _symbols = symbols;
        _diagnostics = diagnostics;
        _discoverEffects = discoverEffects;
        _discoverScene = discoverScene;
        _cancellationToken = cancellationToken;
    }

    public static List<ParsedScene> Parse(string file, string text, StorySymbols symbols, DiagnosticBag diagnostics,
        CancellationToken cancellationToken = default)
    {
        var parser = new TaleParser(file, text, symbols, diagnostics, cancellationToken: cancellationToken);
        parser.Run();
        return parser._scenes;
    }

    public static void Discover(string file, string text, StorySymbols symbols,
        Action<string, SourceLocation> effects, Action<SceneId, SourceLocation> scene,
        CancellationToken cancellationToken = default) =>
        new TaleParser(file, text, symbols, new DiagnosticBag(), effects, scene, cancellationToken).Run();

    private SourceLocation At(int column) => new(_file, _index + 1, column + 1);
    private SourceRange SpanFrom(SourceLocation start) => new(start, At(_lineLength));
    private static string StepId(SourceLocation start) => $"step:{start.Line}:{start.Column}";

    private void Run()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        while (_hasCurrentLine)
        {
            var raw = _text.AsSpan(_lineStart, _lineLength);
            var trimmed = raw.Trim();
            var indent = raw.Length - raw.TrimStart().Length;

            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                AdvanceLine();
                continue;
            }

            if (trimmed.StartsWith(SceneVocabulary.SceneHeader, StringComparison.Ordinal))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                _symbols.Analysis?.Tokens.Add(new(SceneVocabulary.SceneHeader,
                    AnalysisCollector.Range(At(indent), SceneVocabulary.SceneHeader))
                    { Kind = AuthoringTokenKind.SceneHeader });
                FinishScene();
                StartScene(trimmed[SceneVocabulary.SceneHeader.Length..].Trim().ToString(), At(indent));
                AdvanceLine();
                continue;
            }

            if (_scene is null)
            {
                if (trimmed.Length > 0)
                {
                    _diagnostics.Error(CompilerDiagnosticCodes.TextOutsideSceneOrNoFiles,
                        $"Text before the first scene. Start a scene with '{SceneVocabulary.SceneHeader} scene.id'.", At(indent));
                }

                AdvanceLine();
                continue;
            }

            ParseSceneLine(trimmed.ToString(), indent);
            AdvanceLine();
        }

        FinishScene();
    }

    private int GetLineLength(int start, out int nextStart, out bool hasNext)
    {
        var remainder = _text.AsSpan(start);
        var lineBreak = remainder.IndexOfAny("\r\n\u0085\u2028\u2029");
        var end = lineBreak < 0 ? _text.Length : start + lineBreak;
        if (lineBreak < 0)
        {
            nextStart = end;
            hasNext = false;
            return end - start;
        }

        nextStart = end + 1;
        if (_text[end] == '\r' && nextStart < _text.Length && _text[nextStart] == '\n')
        {
            nextStart++;
        }

        hasNext = true;
        return end - start;
    }

    private void AdvanceLine()
    {
        if ((_linesSinceCancellationCheck++ & 63) == 0)
            _cancellationToken.ThrowIfCancellationRequested();
        _index++;
        if (!_hasNextLine)
        {
            _hasCurrentLine = false;
            return;
        }

        _lineStart = _nextLineStart;
        _lineLength = _nextLineLength;
        _nextLineStart = _afterNextLineStart;
        _hasNextLine = _hasAfterNextLine;
        _nextLineLength = _hasNextLine
            ? GetLineLength(_nextLineStart, out _afterNextLineStart, out _hasAfterNextLine)
            : 0;
    }

    private void StartScene(string id, SourceLocation location)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        _discoverScene?.Invoke(new SceneId(id), location);
        _cancellationToken.ThrowIfCancellationRequested();
        _scene = new SceneBuilder(new SceneId(id), location);
        if (!IdentifierRules.IsValid(id) || id == SceneVocabulary.End)
        {
            _diagnostics.Error(CompilerDiagnosticCodes.InvalidSceneId, $"'{id}' is not a valid scene id. Use an ASCII letter or '_' followed by letters, digits, '_' and single '-' or '.' separators (e.g. temple.entrance).", location);
        }
    }

    private void FinishScene()
    {
        if (_scene is null)
        {
            return;
        }

        FlushParagraph();
        if (_scene.SuppressedConditionDepth > 0)
        {
            _diagnostics.Error(CompilerDiagnosticCodes.UnbalancedCondition,
                $"{SceneVocabulary.InlineOpen}{SceneVocabulary.If}{SceneVocabulary.InlineClose} block is never closed with " +
                $"{SceneVocabulary.InlineOpen}{SceneVocabulary.EndIf}{SceneVocabulary.InlineClose}.",
                _scene.SuppressedConditionLocation);
            _scene.SuppressedConditionDepth = 0;
        }
        while (_scene.Frames.Count > 0)
        {
            _diagnostics.Error(CompilerDiagnosticCodes.UnbalancedCondition,
                $"{SceneVocabulary.InlineOpen}{SceneVocabulary.If}{SceneVocabulary.InlineClose} block is never closed with " +
                $"{SceneVocabulary.InlineOpen}{SceneVocabulary.EndIf}{SceneVocabulary.InlineClose}.",
                _scene.Frames.Peek().Location);
            CloseFrame();
        }

        _scenes.Add(new ParsedScene(_scene.Build(), _scene.Location)
        {
            EndLine = _index,
            HasExplicitPriority = _scene.HasExplicitPriority,
            DroppedMalformedChoice = _scene.DroppedMalformedChoice,
        });
        _scene = null;
    }

    private void ParseSceneLine(string line, int indent)
    {
        var scene = _scene!;
        if (scene.SuppressedConditionDepth > 0)
        {
            SkipSuppressedBlockLine(line);
            return;
        }

        if (line.Length == 0)
        {
            FlushParagraph();
            return;
        }

        if (line[0] == SceneVocabulary.MetadataMarker && !scene.HeaderDone && TryParseMetadata(line, indent))
        {
            return;
        }

        scene.HeaderDone = true;

        if (IsBlockDirective(line, _cancellationToken, out var directive))
        {
            FlushParagraph();
            ParseBlockDirective(directive, indent, line);
            return;
        }

        switch (line)
        {
            case [SceneVocabulary.SpeechMarker, ..]:
                _symbols.Analysis?.Tokens.Add(new(SceneVocabulary.SpeechMarker.ToString(),
                    AnalysisCollector.Range(At(indent), SceneVocabulary.SpeechMarker.ToString()))
                    { Kind = AuthoringTokenKind.Speech });
                FlushParagraph();
                ParseSpeech(line, indent);
                return;
            case [SceneVocabulary.EffectsMarker, ..]:
                _symbols.Analysis?.Tokens.Add(new(SceneVocabulary.EffectsMarker.ToString(),
                    AnalysisCollector.Range(At(indent), SceneVocabulary.EffectsMarker.ToString()))
                    { Kind = AuthoringTokenKind.Effects });
                FlushParagraph();
                ParseEntryEffects(line[1..], indent + 1);
                return;
            case [SceneVocabulary.ChoiceMarker]:
            case [SceneVocabulary.ChoiceMarker, ' ' or '\t' or SceneVocabulary.OpenBracket, ..]:
                _symbols.Analysis?.Tokens.Add(new(SceneVocabulary.ChoiceMarker.ToString(),
                    AnalysisCollector.Range(At(indent), SceneVocabulary.ChoiceMarker.ToString()))
                    { Kind = AuthoringTokenKind.Choice });
                // A choice marker followed by a label opener or divert marker is a choice; adjacent markers are emphasis.
                FlushParagraph();
                ParseChoice(line, indent);
                return;
            case [ExpressionVocabulary.Minus, ExpressionVocabulary.Greater, ..]:
                _symbols.Analysis?.Tokens.Add(new(SceneVocabulary.Divert,
                    AnalysisCollector.Range(At(indent), SceneVocabulary.Divert))
                    { Kind = AuthoringTokenKind.Divert });
                FlushParagraph();
                var target = ParseDivertTarget(line[SceneVocabulary.Divert.Length..],
                    indent + SceneVocabulary.Divert.Length);
                if (target is not null)
                {
                    var start = At(indent);
                    scene.CurrentSteps.Add(new DivertStep(StepId(start), SpanFrom(start), target));
                }

                return;
        }

        if (line.StartsWith(SceneVocabulary.SkillCheckFence, StringComparison.Ordinal))
        {
            _symbols.Analysis?.Tokens.Add(new(SceneVocabulary.SkillCheckBlock,
                AnalysisCollector.Range(At(indent), SceneVocabulary.SkillCheckFence))
                { Kind = AuthoringTokenKind.SkillCheckBlock });
            FlushParagraph();
            ParseBlockCheck(line, indent);
            return;
        }

        if (line.StartsWith(SceneVocabulary.SuccessOutcome, StringComparison.Ordinal) ||
            line.StartsWith(SceneVocabulary.FailureOutcome, StringComparison.Ordinal))
        {
            _diagnostics.Error(CompilerDiagnosticCodes.MalformedSkillCheckOutcome,
                $"{SceneVocabulary.SuccessOutcome}/{SceneVocabulary.FailureOutcome} lines must directly follow a " +
                $"{SceneVocabulary.SkillCheck} choice or be inside a '{SceneVocabulary.SkillCheckBlock}' block.",
                At(indent));
            return;
        }

        if (line[0] == SceneVocabulary.MetadataMarker && IsMetadataKey(line))
        {
            _diagnostics.Error(CompilerDiagnosticCodes.InvalidSceneMetadata,
                $"Metadata ({SceneVocabulary.MetadataMarker}{SceneVocabulary.Title}, " +
                $"{SceneVocabulary.MetadataMarker}{SceneVocabulary.Tags}, …) must come directly after the " +
                $"'{SceneVocabulary.SceneHeader}' line.", At(indent));
            return;
        }

        scene.Paragraph.AppendLine(line, At(indent));
    }

    // ---- metadata --------------------------------------------------------------------------------------------------

    private static bool IsMetadataKey(string line)
    {
        var key = line[1..].Split(' ', 2)[0];
        return SceneVocabulary.TryGetMetadataKey(key, out _);
    }

    private bool TryParseMetadata(string line, int indent)
    {
        var keyword = line[1..].Split(' ', 2)[0];
        _symbols.Analysis?.Reference(keyword, At(indent + 1), AuthoringSymbolKind.Directive,
            SceneVocabulary.MetadataSpellings);
        var parts = line[1..].Split(' ', 2, StringSplitOptions.TrimEntries);
        if (!SceneVocabulary.TryGetMetadataKey(parts[0], out var key))
            return false;
        var value = parts.Length > 1 ? parts[1] : string.Empty;
        var valueColumn = indent + line.IndexOf(' ', StringComparison.Ordinal) + 1;
        var scene = _scene!;
        if (key is SceneMetadataKey.Hub or SceneMetadataKey.HubLimit && !scene.HubDirectives.Add(key))
        {
            _diagnostics.Error(CompilerDiagnosticCodes.InvalidSceneMetadata,
                $"Duplicate @{SceneVocabulary.Spelling(key)} directive.", At(indent));
            return true;
        }
        switch (key)
        {
            case SceneMetadataKey.Title:
                scene.Title = value;
                return true;
            case SceneMetadataKey.Requires:
                var expressionColumn = line.IndexOf(' ', StringComparison.Ordinal) < 0
                    ? indent + line.Length
                    : valueColumn + line[(valueColumn - indent)..].Length - line[(valueColumn - indent)..].TrimStart().Length;
                scene.Requires = ExpressionParser.ParseCondition(value, At(valueColumn), _symbols, _diagnostics,
                    At(expressionColumn));
                return true;
            case SceneMetadataKey.Tags:
                foreach (var tag in value.Split(ExpressionVocabulary.ArgumentSeparator,
                             StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    scene.Tags.Add(tag);
                }

                return true;
            case SceneMetadataKey.Priority:
                if (int.TryParse(value, out var priority))
                {
                    scene.Priority = priority;
                    scene.HasExplicitPriority = true;
                }
                else
                {
                    _diagnostics.Error(CompilerDiagnosticCodes.NumberTooLarge,
                        $"{SceneVocabulary.MetadataMarker}{SceneVocabulary.Priority} needs a whole number.", At(valueColumn));
                }

                return true;
            case SceneMetadataKey.Once:
                scene.Once = true;
                return true;
            case SceneMetadataKey.Hub:
                SceneHubMode? hubMode = SceneVocabulary.TryGetHubMode(value, out var parsedHubMode)
                    ? parsedHubMode
                    : null;
                var hubPrefix = hubMode is { } mode ? SceneVocabulary.HubModePrefix(mode) : string.Empty;
                var tags = hubMode is not null && value.EndsWith(ExpressionVocabulary.CloseParen)
                    ? value[hubPrefix.Length..^1].Split(ExpressionVocabulary.ArgumentSeparator, StringSplitOptions.TrimEntries)
                    : new[] { value };
                if (tags.Any(t => string.IsNullOrWhiteSpace(t) ||
                                  t.Any(c => char.IsWhiteSpace(c) || c is ExpressionVocabulary.OpenParen or
                                      ExpressionVocabulary.CloseParen or ExpressionVocabulary.ArgumentSeparator)) ||
                    tags.Distinct(StringComparer.Ordinal).Count() != tags.Length)
                {
                    _diagnostics.Error(CompilerDiagnosticCodes.InvalidSceneMetadata,
                        $"{SceneVocabulary.MetadataMarker}{SceneVocabulary.Hub} needs a tag or " +
                        $"{SceneVocabulary.HubAllPrefix}tag{ExpressionVocabulary.ArgumentSeparator} tag" +
                        $"{ExpressionVocabulary.CloseParen}/{SceneVocabulary.HubAnyPrefix}tag" +
                        $"{ExpressionVocabulary.ArgumentSeparator} tag{ExpressionVocabulary.CloseParen} " +
                        "with distinct nonempty tags.", At(indent));
                }
                else
                {
                    scene.Hub = new Talewright.Rules.Storylets.StoryletTags(tags, hubMode == SceneHubMode.All);
                }
                return true;
            case SceneMetadataKey.HubLimit:
                if (int.TryParse(value, out var limit) && limit > 0)
                {
                    scene.HubLimit = limit;
                }
                else
                {
                    _diagnostics.Error(CompilerDiagnosticCodes.InvalidSceneMetadata,
                        $"{SceneVocabulary.MetadataMarker}{SceneVocabulary.HubLimit} needs a positive whole number.", At(indent));
                }
                return true;
            default:
                throw new ArgumentOutOfRangeException(nameof(key), key, null);
        }
    }

    // ---- block conditionals ----------------------------------------------------------------------------------------

    private static bool IsBlockDirective(string line, CancellationToken cancellationToken, out string directive)
    {
        directive = string.Empty;
        if (line[0] != SceneVocabulary.InlineOpen)
        {
            return false;
        }

        var scan = DelimiterScanner.Scan(line, DelimiterScanMode.ReaderText, cancellationToken: cancellationToken);
        if (!scan.TryGetClose(0, out var close) || close != line.Length - 1)
        {
            return false;
        }

        var inner = line[1..^1].Trim();
        if (inner is SceneVocabulary.Else or SceneVocabulary.EndIf ||
            inner.StartsWith(SceneVocabulary.IfPrefix, StringComparison.Ordinal))
        {
            directive = inner;
            return true;
        }

        return false;
    }

    private void ParseBlockDirective(string directive, int indent, string line)
    {
        var scene = _scene!;
        var keyword = directive.StartsWith(SceneVocabulary.IfPrefix, StringComparison.Ordinal)
            ? SceneVocabulary.If : directive;
        var keywordStart = line.IndexOf(keyword, StringComparison.Ordinal);
        _symbols.Analysis?.Tokens.Add(new(keyword, AnalysisCollector.Range(At(indent + keywordStart), keyword))
            { Kind = AuthoringSyntaxVocabulary.ConditionalKind(keyword) });
        if (directive == SceneVocabulary.Else)
        {
            if (scene.Frames.Count == 0 || scene.Frames.Peek().InElse)
            {
                _diagnostics.Error(CompilerDiagnosticCodes.UnbalancedCondition,
                    $"{SceneVocabulary.InlineOpen}{SceneVocabulary.Else}{SceneVocabulary.InlineClose} without a matching " +
                    $"{SceneVocabulary.InlineOpen}{SceneVocabulary.If}{SceneVocabulary.InlineClose}.", At(indent));
                return;
            }

            scene.Frames.Peek().InElse = true;
            return;
        }

        if (directive == SceneVocabulary.EndIf)
        {
            if (scene.Frames.Count == 0)
            {
                _diagnostics.Error(CompilerDiagnosticCodes.UnbalancedCondition,
                    $"{SceneVocabulary.InlineOpen}{SceneVocabulary.EndIf}{SceneVocabulary.InlineClose} without a matching " +
                    $"{SceneVocabulary.InlineOpen}{SceneVocabulary.If}{SceneVocabulary.InlineClose}.", At(indent));
                return;
            }

            CloseFrame();
            return;
        }

        if (scene.Frames.Count == MaximumBlockConditionDepth)
        {
            _diagnostics.Error(CompilerDiagnosticCodes.NestingLimitExceeded,
                $"Conditions are nested more than {MaximumBlockConditionDepth} levels deep.", At(indent));
            scene.SuppressedConditionDepth = 1;
            scene.SuppressedConditionLocation = At(indent);
            return;
        }

        var lead = line[1..^1].Length - line[1..^1].TrimStart().Length;
        var condition = ExpressionParser.ParseCondition(
            directive[SceneVocabulary.IfPrefix.Length..],
            At(indent + 1 + SceneVocabulary.IfPrefix.Length), _symbols, _diagnostics,
            At(indent + 1 + lead + SceneVocabulary.IfPrefix.Length))
            ?? new BoolLiteral(false);
        scene.Frames.Push(new Frame(condition, At(indent)));
    }

    private void SkipSuppressedBlockLine(string line)
    {
        if (!IsBlockDirective(line, _cancellationToken, out var directive))
        {
            return;
        }

        if (directive.StartsWith(SceneVocabulary.IfPrefix, StringComparison.Ordinal))
        {
            _scene!.SuppressedConditionDepth++;
        }
        else if (directive == SceneVocabulary.EndIf)
        {
            _scene!.SuppressedConditionDepth--;
        }
    }

    private void CloseFrame()
    {
        var scene = _scene!;
        var frame = scene.Frames.Pop();
        scene.CurrentSteps.Add(new ConditionalStep(StepId(frame.Location), SpanFrom(frame.Location),
            frame.Condition, frame.Then, frame.Else));
    }

    // ---- prose -----------------------------------------------------------------------------------------------------

    private void FlushParagraph()
    {
        var scene = _scene!;
        if (scene.Paragraph.IsEmpty)
        {
            return;
        }

        var inlines = _discoverEffects is null
            ? InlineParser.Parse(scene.Paragraph, _symbols, _diagnostics, cancellationToken: _cancellationToken)
            : [];
        if (inlines.Count > 0)
        {
            var start = scene.Paragraph.Locate(0);
            scene.CurrentSteps.Add(new ProseStep(StepId(start),
                new SourceRange(start, scene.Paragraph.Locate(scene.Paragraph.Text.Length)), new ParagraphTemplate(inlines)));
        }

        scene.Paragraph = new SourceText();
    }

    private void ParseSpeech(string line, int indent)
    {
        if (_discoverEffects is not null)
        {
            return;
        }

        var body = line[1..];
        var colon = body.IndexOf(ExpressionVocabulary.Separator, StringComparison.Ordinal);
        var speakerText = (colon < 0 ? body : body[..colon]).Trim();
        _symbols.Analysis?.Reference(speakerText, At(indent + 1 + body.Length - body.TrimStart().Length),
            AuthoringSymbolKind.Character);
        var who = colon > 0 ? body[..colon].Trim() : string.Empty;
        if (who.Length == 0 || !IdentifierRules.IsValid(who))
        {
            _diagnostics.Error(CompilerDiagnosticCodes.MalformedDialogue,
                $"Speech lines look like '{SceneVocabulary.SpeechMarker} who{ExpressionVocabulary.Separator} text', " +
                "where who is a character id.", At(indent));
            return;
        }

        var speaker = new EntityId(who);
        if (!_symbols.World.Characters.ContainsKey(speaker))
        {
            _diagnostics.Error(CompilerDiagnosticCodes.UnknownNameOrProperty, $"Unknown character '{who}'. Declare it under characters in story.talewright.yaml.", At(indent + 1 + body.IndexOf(who, StringComparison.Ordinal)));
            if (_symbols.Analysis is not null)
            {
                var partial = body[(colon + 1)..];
                var whitespace = partial.Length - partial.TrimStart().Length;
                InlineParser.Parse(SourceText.Single(partial.Trim(), At(indent + colon + 2 + whitespace)),
                    _symbols, new DiagnosticBag(), cancellationToken: _cancellationToken);
            }
            return;
        }

        var text = body[(colon + 1)..];
        var lead = text.Length - text.TrimStart().Length;
        var inlines = InlineParser.Parse(SourceText.Single(text.Trim(), At(indent + 1 + colon + 1 + lead)),
            _symbols, _diagnostics, cancellationToken: _cancellationToken);
        var start = At(indent);
        _scene!.CurrentSteps.Add(new ProseStep(StepId(start), SpanFrom(start), new SpeechTemplate(speaker, inlines)));
    }

    // ---- effects and diverts ---------------------------------------------------------------------------------------

    private void ParseEntryEffects(string text, int column)
    {
        var lead = text.Length - text.TrimStart().Length;
        var effects = ParseEffects(text.Trim(), At(column + lead));
        if (effects is not null)
        {
            var start = At(column - 1);
            _scene!.CurrentSteps.Add(new EffectStep(StepId(start), SpanFrom(start), effects));
        }
    }

    private IReadOnlyList<Effect>? ParseEffects(string text, SourceLocation location)
    {
        if (_discoverEffects is not null)
        {
            _discoverEffects(text, location);
            return [];
        }

        return ExpressionParser.ParseEffects(text, location, _symbols, _diagnostics);
    }

    private DivertTarget? ParseDivertTarget(string text, int column)
    {
        var lead = text.Length - text.TrimStart().Length;
        var id = text.Trim();
        var location = At(column + lead);
        _symbols.Analysis?.Reference(id, location, AuthoringSymbolKind.Scene, SceneVocabulary.End);
        if (id == SceneVocabulary.End)
        {
            return DivertTarget.End;
        }

        if (id.Length == 0)
        {
            _diagnostics.Error(CompilerDiagnosticCodes.MissingDivertTarget,
                $"Divert needs a target: '{SceneVocabulary.Divert} scene.id' or '{SceneVocabulary.Divert} " +
                $"{SceneVocabulary.End}'.", location);
            return null;
        }

        if (!IdentifierRules.IsValid(id))
        {
            _diagnostics.Error(CompilerDiagnosticCodes.InvalidSceneId,
                $"'{id}' is not a valid scene id. Use an ASCII letter or '_' followed by letters, digits, '_' and single '-' or '.' separators.",
                location);
            return null;
        }

        if (!_symbols.Scenes.Contains(new SceneId(id)))
        {
            _diagnostics.Error(CompilerDiagnosticCodes.UnknownScene, $"Unknown scene '{id}'.", location);
            return null;
        }

        return new DivertTarget(new SceneId(id));
    }

    // ---- choices ---------------------------------------------------------------------------------------------------

    private void ParseChoice(string line, int indent)
    {
        var scene = _scene!;
        var start = At(indent);
        var rest = line[1..];
        var pos = SkipSpaces(rest, 0);
        IReadOnlyList<TemplateInline>? label = null;
        if (pos < rest.Length && rest[pos] == SceneVocabulary.OpenBracket)
        {
            var labelScan = DelimiterScanner.Scan(rest, DelimiterScanMode.ChoiceLabel, pos, _cancellationToken);
            if (!labelScan.TryGetClose(pos, out var close))
            {
                NoteDroppedChoice(scene);
                if (_discoverEffects is null)
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
                    var hint = ChoiceSyntax.FindMissingBracketHint(rest, pos, labelScan, _cancellationToken);
                    if (_symbols.Analysis is not null)
                    {
                        // Clauses after the label are not reader text, so they must not be analysed as substitutions.
                        var end = hint.LabelEnd;
                        if (unclosedImage)
                            end = Math.Min(end, unclosedBrace);
                        var partial = rest[(pos + 1)..end];
                        InlineParser.AnalyzePartial(SourceText.Single(partial, At(indent + pos + 2)), _symbols,
                            _cancellationToken);
                    }
                    if (unclosedImage)
                    {
                        ImageSyntax.Parse(SourceText.Single(rest, At(indent + 1)), unclosedBrace + 1,
                            rest.Length, false, _diagnostics, _symbols, _cancellationToken);
                    }
                    else if (unclosedBrace >= 0)
                    {
                        ExpressionParser.ReportStringErrors(rest[(unclosedBrace + 1)..],
                            offset => At(indent + unclosedBrace + 2 + offset), _diagnostics);
                    }
                    var message = $"Choice label is missing its closing '{SceneVocabulary.CloseBracket}'.";
                    if (hint.Marker is not null)
                        message += $" Place '{SceneVocabulary.CloseBracket}' before '{hint.Marker}'.";
                    _diagnostics.Error(CompilerDiagnosticCodes.MalformedChoice, message, At(indent + 1 + pos));
                }
                return;
            }

            var labelText = rest[(pos + 1)..close];
            label = _discoverEffects is null
                ? InlineParser.Parse(SourceText.Single(labelText, At(indent + 1 + pos + 1)), _symbols, _diagnostics,
                    cancellationToken: _cancellationToken)
                : [];
            pos = SkipSpaces(rest, close + 1);
        }

        Expr? requires = null;
        IReadOnlyList<Effect> effects = [];
        DivertTarget? divert = null;
        CheckBranch? check = null;
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
                    _diagnostics.Error(CompilerDiagnosticCodes.MalformedChoice,
                        $"Duplicate '{SceneVocabulary.ChoiceRequires}{ExpressionVocabulary.CloseParen}' condition. " +
                        "A choice accepts only one.", At(column));
                if (!clauseScan.TryGetClose(pos, out var end))
                {
                    var expressionStart = pos + SceneVocabulary.ChoiceRequires.Length;
                    var expression = rest[expressionStart..];
                    var tokens = _symbols.Analysis?.Expression(ExpressionContextKind.Condition, expression,
                                     At(column + SceneVocabulary.ChoiceRequires.Length))
                        ?? Lexer.Tokenize(expression);
                    ExpressionParser.ReportStringErrors(tokens,
                        offset => At(column + SceneVocabulary.ChoiceRequires.Length + offset), _diagnostics);
                    _diagnostics.Error(CompilerDiagnosticCodes.MalformedChoice,
                        $"'{SceneVocabulary.ChoiceRequires}' is missing its closing '{ExpressionVocabulary.CloseParen}'.",
                        At(column));
                    NoteDroppedChoice(scene);
                    return;
                }

                var condition = ExpressionParser.ParseCondition(
                    rest[(pos + SceneVocabulary.ChoiceRequires.Length)..end],
                    At(column + SceneVocabulary.ChoiceRequires.Length), _symbols, _diagnostics);
                if (!hasRequires)
                    requires = condition;
                hasRequires = true;
                pos = SkipSpaces(rest, end + 1);
            }
            else if (remaining[0] == SceneVocabulary.InlineOpen)
            {
                if (hasEffects)
                    _diagnostics.Error(CompilerDiagnosticCodes.MalformedChoice, "Duplicate effect block. A choice accepts only one.", At(column));
                if (!clauseScan.TryGetClose(pos, out var end))
                {
                    var expression = rest[(pos + 1)..];
                    var tokens = _symbols.Analysis?.Expression(ExpressionContextKind.Effects, expression, At(column + 1))
                        ?? Lexer.Tokenize(expression);
                    ExpressionParser.ReportStringErrors(tokens,
                        offset => At(column + 1 + offset), _diagnostics);
                    _diagnostics.Error(CompilerDiagnosticCodes.MalformedChoice,
                        $"Effect block is missing its closing '{SceneVocabulary.InlineClose}'.", At(column));
                    NoteDroppedChoice(scene);
                    return;
                }

                var parsedEffects = ParseEffects(rest[(pos + 1)..end], At(column + 1)) ?? [];
                if (!hasEffects)
                    effects = parsedEffects;
                hasEffects = true;
                pos = SkipSpaces(rest, end + 1);
            }
            else if (remaining.StartsWith(SceneVocabulary.Divert, StringComparison.Ordinal))
            {
                _symbols.Analysis?.Tokens.Add(new(SceneVocabulary.Divert,
                    AnalysisCollector.Range(At(column), SceneVocabulary.Divert))
                    { Kind = AuthoringTokenKind.Divert });
                hasDivert = true;
                var targetEnd = SkipSpaces(rest, pos + SceneVocabulary.Divert.Length);
                var targetCheckpoint = _cancellationToken.CanBeCanceled ? new CancellationCheckpoint(_cancellationToken) : null;
                while (targetEnd < rest.Length && !char.IsWhiteSpace(rest[targetEnd]) &&
                       rest[targetEnd] is not (SceneVocabulary.InlineOpen or ExpressionVocabulary.OpenParen) &&
                       !rest.AsSpan(targetEnd).StartsWith(SceneVocabulary.Divert, StringComparison.Ordinal))
                {
                    targetCheckpoint?.Check();
                    targetEnd++;
                }
                divert = ParseDivertTarget(rest[(pos + SceneVocabulary.Divert.Length)..targetEnd],
                    column + SceneVocabulary.Divert.Length);
                var trailing = SkipSpaces(rest, targetEnd);
                if (trailing < rest.Length)
                    ReportTrailingChoiceText(rest, trailing, indent + 1, afterCheck: false, insideBlock: false);
                pos = rest.Length;
            }
            else if (remaining.StartsWith(SceneVocabulary.SkillCheck, StringComparison.Ordinal))
            {
                if (label is null)
                {
                    _diagnostics.Error(CompilerDiagnosticCodes.MalformedChoice,
                        $"Textless choices need '{SceneVocabulary.Divert} target' or '{SceneVocabulary.Divert} " +
                        $"{SceneVocabulary.End}'; skill checks require a '{SceneVocabulary.OpenBracket}label" +
                        $"{SceneVocabulary.CloseBracket}'.", At(indent));
                    NoteDroppedChoice(scene);
                    return;
                }
                var specOffset = pos + SceneVocabulary.SkillCheck.Length;
                var spec = ParseCheckSpec(rest[specOffset..], column + SceneVocabulary.SkillCheck.Length,
                    clauseScan, specOffset);
                var (success, failure) = ParseOutcomeLines(At(column), insideBlock: false);
                if (spec is not null && success is not null && failure is not null)
                {
                    check = new CheckBranch(spec, success, failure);
                }

                pos = rest.Length;
            }
            else if (remaining[0] == ExpressionVocabulary.OpenParen &&
                     ChoiceSyntax.TryReadSpacedRequires(rest, pos, _cancellationToken, out var expressionStart))
            {
                var expressionEnd = clauseScan.TryGetClose(pos, out var end) ? end : rest.Length;
                _symbols.Analysis?.Expression(ExpressionContextKind.Condition, rest[expressionStart..expressionEnd],
                    At(indent + 1 + expressionStart));
                var spelling = ChoiceSyntax.Excerpt(rest.AsSpan(pos, expressionStart - pos));
                _diagnostics.Error(CompilerDiagnosticCodes.MalformedChoice,
                    $"Choice conditions use the exact marker " +
                    $"'{SceneVocabulary.ChoiceRequires}{ExpressionVocabulary.CloseParen}'. " +
                    $"'{spelling}' is not recognized.", At(column));
                NoteDroppedChoice(scene);
                return;
            }
            else
            {
                var help = label is null
                    ? $"Use '{SceneVocabulary.ChoiceMarker} {SceneVocabulary.OpenBracket}label" +
                      $"{SceneVocabulary.CloseBracket} ...' or a textless '{SceneVocabulary.ChoiceMarker} ... " +
                      $"{SceneVocabulary.Divert} target'."
                    : $"Expected {SceneVocabulary.ChoiceRequires} …{ExpressionVocabulary.CloseParen}, " +
                      $"{SceneVocabulary.InlineOpen} effects " +
                      $"{SceneVocabulary.InlineClose}, {SceneVocabulary.Divert} target or {SceneVocabulary.SkillCheck}.";
                var excerpt = ChoiceSyntax.Excerpt(remaining);
                var bareTarget = IdentifierRules.IsValid(remaining.ToString())
                    ? $" Insert '{SceneVocabulary.Divert}' before the target."
                    : "";
                _diagnostics.Error(CompilerDiagnosticCodes.MalformedChoice, $"Unexpected '{excerpt}' in choice.{bareTarget} {help}", At(column));
                NoteDroppedChoice(scene);
                return;
            }
        }

        if (label is null && !hasDivert)
        {
            _diagnostics.Error(CompilerDiagnosticCodes.MalformedChoice,
                $"Textless choices need '{SceneVocabulary.Divert} target' or '{SceneVocabulary.Divert} " +
                $"{SceneVocabulary.End}'.", At(indent));
            NoteDroppedChoice(scene);
            return;
        }

        scene.CurrentSteps.Add(new ChoiceStep(StepId(start), SpanFrom(start), new ChoiceDefinition
        {
            Key = $"c{scene.ChoiceCount++}",
            Label = label,
            Requires = requires,
            Effects = effects,
            Check = check,
            Divert = divert,
        }));
    }

    // ---- skill checks ----------------------------------------------------------------------------------------------

    private void ParseBlockCheck(string line, int indent)
    {
        var scene = _scene!;
        var spec = line[SceneVocabulary.SkillCheckFence.Length..].TrimStart();
        var location = At(indent);
        if (!spec.StartsWith(SceneVocabulary.SkillCheck, StringComparison.Ordinal))
        {
            _diagnostics.Error(CompilerDiagnosticCodes.InvalidBlock,
                $"Only '{SceneVocabulary.SkillCheckBlock}' blocks are supported.", location);
            return;
        }

        var check = ParseCheckSpec(spec[SceneVocabulary.SkillCheck.Length..],
            indent + line.IndexOf(SceneVocabulary.SkillCheck, StringComparison.Ordinal) + SceneVocabulary.SkillCheck.Length,
            insideBlock: true);
        var (success, failure) = ParseOutcomeLines(location, insideBlock: true);

        if (check is not null && success is not null && failure is not null)
        {
            scene.CurrentSteps.Add(new CheckStep(StepId(location), SpanFrom(location),
                new CheckBranch(check, success, failure)));
        }
    }

    /// <summary>Parses <c>[actor] skill dc expr</c>.</summary>
    private SkillCheck? ParseCheckSpec(string text, int column, DelimiterScanResult? scan = null, int scanOffset = 0,
        bool insideBlock = false)
    {
        var dcExpression = text.IndexOf(SceneVocabulary.DifficultySeparator, StringComparison.Ordinal);
        if (dcExpression >= 0)
        {
            scan ??= DelimiterScanner.Scan(text, DelimiterScanMode.CodeGroup, cancellationToken: _cancellationToken);
            var trailing = ChoiceSyntax.FindTrailingClause(text,
                dcExpression + SceneVocabulary.DifficultySeparator.Length, scan, scanOffset, _cancellationToken);
            if (trailing >= 0)
            {
                ReportTrailingChoiceText(text, trailing, column, afterCheck: true, insideBlock);
                text = text[..trailing].TrimEnd();
            }
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var dc = Array.IndexOf(words, SceneVocabulary.Difficulty);
        if (_symbols.Analysis is { } analysis)
        {
            analysis.Tokens.Add(new(SceneVocabulary.SkillCheck,
                AnalysisCollector.Range(At(column - SceneVocabulary.SkillCheck.Length), SceneVocabulary.SkillCheck))
                { Kind = AuthoringTokenKind.SkillCheck });
            var offset = 0;
            var references = words.TakeWhile(word => word != SceneVocabulary.Difficulty).Take(2).ToArray();
            if (references.Length == 0)
            {
                analysis.Reference("", At(column), AuthoringSymbolKind.Character);
                analysis.Reference("", At(column), AuthoringSymbolKind.Stat);
            }
            for (var index = 0; index < references.Length; index++)
            {
                var word = references[index];
                offset = text.IndexOf(word, offset, StringComparison.Ordinal);
                if (dc == 1)
                    analysis.Reference(word, At(column + offset), AuthoringSymbolKind.Stat);
                else if (dc == 2)
                    analysis.Reference(word, At(column + offset), index == 0 ? AuthoringSymbolKind.Character : AuthoringSymbolKind.Stat);
                else
                {
                    analysis.Reference(word, At(column + offset), AuthoringSymbolKind.Stat);
                    if (index == 0) analysis.Reference(word, At(column + offset), AuthoringSymbolKind.Character);
                }
                offset += word.Length;
            }
            if (dc < 0 && references.Length == 1 && column + offset < _lineLength &&
                char.IsWhiteSpace(_text[_lineStart + column + offset]) &&
                _symbols.World.Characters.ContainsKey(new EntityId(references[0])))
                analysis.Reference("", At(column + offset + 1), AuthoringSymbolKind.Stat);
            var dcStart = text.IndexOf(SceneVocabulary.DifficultySeparator, StringComparison.Ordinal);
            if (dcStart >= 0)
                analysis.Expression(ExpressionContextKind.Number,
                    text[(dcStart + SceneVocabulary.DifficultySeparator.Length)..],
                    At(column + dcStart + SceneVocabulary.DifficultySeparator.Length),
                    cacheForReuse: true);
            else if (text.EndsWith(SceneVocabulary.DifficultyPrefix, StringComparison.Ordinal))
                analysis.Expression(ExpressionContextKind.Number, string.Empty, At(column + text.Length));
        }
        if (_symbols.World.Ruleset.SkillCheckRule is null)
        {
            _diagnostics.Error(CompilerDiagnosticCodes.SkillChecksUnsupported,
                $"The '{_symbols.World.Ruleset.Id}' ruleset has no {SceneVocabulary.SkillCheck} rule, " +
                $"so {SceneVocabulary.SkillCheck} cannot be used.", At(column));
            return null;
        }

        if (dc is < 1 or > 2 || dc == words.Length - 1)
        {
            _diagnostics.Error(CompilerDiagnosticCodes.MalformedSkillCheck,
                $"Skill checks look like '{SceneVocabulary.SkillCheck} {SceneVocabulary.OpenBracket}actor" +
                $"{SceneVocabulary.CloseBracket} skill{SceneVocabulary.DifficultySeparator}12'.", At(column));
            return null;
        }

        var actor = dc == 2 ? new EntityId(words[0]) : EntityId.Player;
        if (!_symbols.World.Characters.ContainsKey(actor))
        {
            _diagnostics.Error(CompilerDiagnosticCodes.UnknownNameOrProperty, $"Unknown character '{actor}'.", At(column));
            return null;
        }

        var skill = new StatId(words[dc - 1]);
        if (!_symbols.World.Ruleset.HasStat(skill))
        {
            _diagnostics.Error(CompilerDiagnosticCodes.UnknownNameOrProperty, $"Unknown stat '{skill}' (ruleset '{_symbols.World.Ruleset.Id}').", At(column));
            return null;
        }

        var dcText = text[(text.IndexOf(SceneVocabulary.DifficultySeparator, StringComparison.Ordinal)
            + SceneVocabulary.DifficultySeparator.Length)..];
        var difficulty = ExpressionParser.ParseNumber(dcText,
            At(column + text.IndexOf(SceneVocabulary.DifficultySeparator, StringComparison.Ordinal)
                + SceneVocabulary.DifficultySeparator.Length), _symbols, _diagnostics);
        return difficulty is null ? null : new SkillCheck(actor, skill, difficulty);
    }

    /// <summary>
    /// Consumes the <c>success:</c>/<c>failure:</c> lines that follow a check (and the closing <c>:::</c> of a block).
    /// </summary>
    private (CheckOutcome? Success, CheckOutcome? Failure) ParseOutcomeLines(SourceLocation checkLocation, bool insideBlock)
    {
        CheckOutcome? success = null;
        CheckOutcome? failure = null;
        var sawSuccess = false;
        var sawFailure = false;
        var closed = false;
        while (_hasNextLine)
        {
            var raw = _text.AsSpan(_nextLineStart, _nextLineLength);
            var line = raw.Trim();
            var indent = raw.Length - raw.TrimStart().Length;
            if (line.StartsWith("//", StringComparison.Ordinal) || (insideBlock && line.Length == 0))
            {
                AdvanceLine();
                continue;
            }

            if (insideBlock && line.SequenceEqual(SceneVocabulary.SkillCheckFence))
            {
                AdvanceLine();
                closed = true;
                break;
            }

            var isSuccess = line.StartsWith(SceneVocabulary.SuccessOutcome, StringComparison.Ordinal);
            if (!isSuccess && !line.StartsWith(SceneVocabulary.FailureOutcome, StringComparison.Ordinal))
            {
                break;
            }

            AdvanceLine();
            var outcomePrefix = isSuccess ? SceneVocabulary.SuccessOutcome : SceneVocabulary.FailureOutcome;
            var outcome = ParseOutcome(line[outcomePrefix.Length..].ToString(), indent + outcomePrefix.Length);
            if (isSuccess)
            {
                sawSuccess = true;
                success = outcome;
            }
            else
            {
                sawFailure = true;
                failure = outcome;
            }
        }

        if (insideBlock && !closed)
        {
            _diagnostics.Error(CompilerDiagnosticCodes.InvalidBlock,
                $"'{SceneVocabulary.SkillCheckBlock}' block is never closed with '{SceneVocabulary.SkillCheckFence}'.",
                checkLocation);
        }

        if (!sawSuccess || !sawFailure)
        {
            _diagnostics.Error(CompilerDiagnosticCodes.MalformedSkillCheckOutcome,
                $"A skill check needs both a '{SceneVocabulary.SuccessOutcome}' and a " +
                $"'{SceneVocabulary.FailureOutcome}' line.", checkLocation);
        }

        return (success, failure);
    }

    private CheckOutcome? ParseOutcome(string text, int column)
    {
        var pos = SkipSpaces(text, 0);
        IReadOnlyList<Effect> effects = [];
        DivertTarget? divert = null;
        if (pos < text.Length && text[pos] == SceneVocabulary.InlineOpen)
        {
            var scan = DelimiterScanner.Scan(text, DelimiterScanMode.CodeGroup, pos, _cancellationToken);
            if (!scan.TryGetClose(pos, out var end))
            {
                _symbols.Analysis?.Expression(ExpressionContextKind.Effects, text[(pos + 1)..], At(column + pos + 1));
                _diagnostics.Error(CompilerDiagnosticCodes.MalformedSkillCheckOutcome,
                    $"Outcome effect block is missing its closing '{SceneVocabulary.InlineClose}'.", At(column + pos));
                return null;
            }

            effects = ParseEffects(text[(pos + 1)..end], At(column + pos + 1)) ?? [];
            pos = SkipSpaces(text, end + 1);
        }

        if (pos < text.Length)
        {
            if (!text[pos..].StartsWith(SceneVocabulary.Divert, StringComparison.Ordinal))
            {
                _diagnostics.Error(CompilerDiagnosticCodes.MalformedSkillCheckOutcome,
                    $"Outcomes look like '{SceneVocabulary.SuccessOutcome} {SceneVocabulary.InlineOpen} effects " +
                    $"{SceneVocabulary.InlineClose} {SceneVocabulary.Divert} target'.", At(column + pos));
                return null;
            }

            _symbols.Analysis?.Tokens.Add(new(SceneVocabulary.Divert,
                AnalysisCollector.Range(At(column + pos), SceneVocabulary.Divert))
                { Kind = AuthoringTokenKind.Divert });
            divert = ParseDivertTarget(text[(pos + SceneVocabulary.Divert.Length)..],
                column + pos + SceneVocabulary.Divert.Length);
        }

        return new CheckOutcome(effects, divert);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private static void NoteDroppedChoice(SceneBuilder scene)
    {
        if (scene.DroppedMalformedChoice)
            return;
        foreach (var frame in scene.Frames)
        {
            if (frame.Condition is BoolLiteral literal && literal.Value == frame.InElse)
                return;
        }
        scene.DroppedMalformedChoice = true;
    }

    private void ReportTrailingChoiceText(string text, int pos, int column, bool afterCheck, bool insideBlock)
    {
        var remaining = text.AsSpan(pos);
        if (remaining[0] == ExpressionVocabulary.OpenParen &&
            ChoiceSyntax.TryReadSpacedRequires(text, pos, _cancellationToken, out _))
        {
            var terminal = afterCheck ? "the skill check" : "the divert";
            _diagnostics.Error(CompilerDiagnosticCodes.MalformedChoice,
                $"Choice conditions use the exact marker '{SceneVocabulary.ChoiceRequires}" +
                $"{ExpressionVocabulary.CloseParen}' and cannot follow {terminal}.", At(column + pos));
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
                $"'{SceneVocabulary.ChoiceRequires}{ExpressionVocabulary.CloseParen}' is misplaced after the divert. " +
                $"Put the condition before '{SceneVocabulary.Divert}'.",
            _ when remaining.StartsWith(SceneVocabulary.Divert, StringComparison.Ordinal) && afterCheck =>
                $"Divert is misplaced after the skill check. Outcomes belong on " +
                $"'{SceneVocabulary.SuccessOutcome}' and '{SceneVocabulary.FailureOutcome}' lines.",
            _ => $"Unexpected '{ChoiceSyntax.Excerpt(remaining)}' after the {(afterCheck ? "skill check" : "divert")}.",
        };
        _diagnostics.Error(CompilerDiagnosticCodes.MalformedChoice, message, At(column + pos));
    }

    private static int SkipSpaces(string text, int pos)
    {
        while (pos < text.Length && char.IsWhiteSpace(text[pos]))
        {
            pos++;
        }

        return pos;
    }

    private sealed class Frame(Expr condition, SourceLocation location)
    {
        public Expr Condition { get; } = condition;
        public SourceLocation Location { get; } = location;
        public List<SceneStep> Then { get; } = [];
        public List<SceneStep> Else { get; } = [];
        public bool InElse { get; set; }
    }

    private sealed class SceneBuilder(SceneId id, SourceLocation location)
    {
        public SceneId Id { get; } = id;
        public SourceLocation Location { get; } = location;
        public bool HeaderDone { get; set; }
        public string? Title { get; set; }
        public Expr? Requires { get; set; }
        public HashSet<string> Tags { get; } = [];
        public int Priority { get; set; }
        public bool HasExplicitPriority { get; set; }
        public bool Once { get; set; }
        public Talewright.Rules.Storylets.StoryletTags? Hub { get; set; }
        public int? HubLimit { get; set; }
        public HashSet<SceneMetadataKey> HubDirectives { get; } = [];
        public List<SceneStep> Steps { get; } = [];
        public int ChoiceCount { get; set; }
        public bool DroppedMalformedChoice { get; set; }
        public Stack<Frame> Frames { get; } = new();
        public int SuppressedConditionDepth { get; set; }
        public SourceLocation SuppressedConditionLocation { get; set; }
        public SourceText Paragraph { get; set; } = new();

        public List<SceneStep> CurrentSteps =>
            Frames.Count == 0 ? Steps : Frames.Peek().InElse ? Frames.Peek().Else : Frames.Peek().Then;

        public SceneDefinition Build() => new()
        {
            Id = Id,
            Title = Title,
            Requires = Requires,
            Tags = Tags,
            Priority = Priority,
            Once = Once,
            Hub = Hub,
            HubLimit = HubLimit,
            Steps = Steps,
        };
    }
}
