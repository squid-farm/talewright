using Talewright.Core.Identity;
using Talewright.Narrative.Templates;
using Talewright.Rules.Checks;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Model;
using Talewright.Talemark.Analysis;

namespace Talewright.Talemark.Parsing;

/// <summary>A compiled scene plus where it was declared (for validator diagnostics).</summary>
internal sealed record ParsedScene(SceneDefinition Definition, SourceLocation Location)
{
    public required int EndLine { get; init; }
    public bool HasExplicitPriority { get; init; }
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

            if (trimmed.StartsWith("===", StringComparison.Ordinal))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                _symbols.Analysis?.Tokens.Add(new("===", AnalysisCollector.Range(At(indent), "===")));
                FinishScene();
                StartScene(trimmed[3..].Trim().ToString(), At(indent));
                AdvanceLine();
                continue;
            }

            if (_scene is null)
            {
                if (trimmed.Length > 0)
                {
                    _diagnostics.Error("TW3001", "Text before the first scene. Start a scene with '=== scene.id'.", At(indent));
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
        if (!IdentifierRules.IsValid(id) || id == "END")
        {
            _diagnostics.Error("TW1008", $"'{id}' is not a valid scene id. Use an ASCII letter or '_' followed by letters, digits, '_' and single '-' or '.' separators (e.g. temple.entrance).", location);
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
            _diagnostics.Error("TW1005", "{if} block is never closed with {/if}.", _scene.SuppressedConditionLocation);
            _scene.SuppressedConditionDepth = 0;
        }
        while (_scene.Frames.Count > 0)
        {
            _diagnostics.Error("TW1005", "{if} block is never closed with {/if}.", _scene.Frames.Peek().Location);
            CloseFrame();
        }

        _scenes.Add(new ParsedScene(_scene.Build(), _scene.Location)
        {
            EndLine = _index,
            HasExplicitPriority = _scene.HasExplicitPriority,
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

        if (line[0] == '@' && !scene.HeaderDone && TryParseMetadata(line, indent))
        {
            return;
        }

        scene.HeaderDone = true;

        if (IsBlockDirective(line, out var directive))
        {
            FlushParagraph();
            ParseBlockDirective(directive, indent, line);
            return;
        }

        switch (line)
        {
            case ['>', ..]:
                _symbols.Analysis?.Tokens.Add(new(">", AnalysisCollector.Range(At(indent), ">")));
                FlushParagraph();
                ParseSpeech(line, indent);
                return;
            case ['~', ..]:
                _symbols.Analysis?.Tokens.Add(new("~", AnalysisCollector.Range(At(indent), "~")));
                FlushParagraph();
                ParseEntryEffects(line[1..], indent + 1);
                return;
            case ['*']:
            case ['*', ' ' or '\t' or '[', ..]:
                _symbols.Analysis?.Tokens.Add(new("*choice", AnalysisCollector.Range(At(indent), "*")));
                // "* [label]" and "* -> target" are choices; "*text*" is emphasis.
                FlushParagraph();
                ParseChoice(line, indent);
                return;
            case ['-', '>', ..]:
                _symbols.Analysis?.Tokens.Add(new("->", AnalysisCollector.Range(At(indent), "->")));
                FlushParagraph();
                var target = ParseDivertTarget(line[2..], indent + 2);
                if (target is not null)
                {
                    var start = At(indent);
                    scene.CurrentSteps.Add(new DivertStep(StepId(start), SpanFrom(start), target));
                }

                return;
            case [':', ':', ':', ..]:
                _symbols.Analysis?.Tokens.Add(new("::: skill-check", AnalysisCollector.Range(At(indent), ":::")));
                FlushParagraph();
                ParseBlockCheck(line, indent);
                return;
        }

        if (line.StartsWith("success:", StringComparison.Ordinal) || line.StartsWith("failure:", StringComparison.Ordinal))
        {
            _diagnostics.Error("TW1009", "success:/failure: lines must directly follow a skill-check choice or be inside a '::: skill-check' block.", At(indent));
            return;
        }

        if (line[0] == '@' && IsMetadataKey(line))
        {
            _diagnostics.Error("TW1006", "Metadata (@title, @tags, …) must come directly after the '===' line.", At(indent));
            return;
        }

        scene.Paragraph.AppendLine(line, At(indent));
    }

    // ---- metadata --------------------------------------------------------------------------------------------------

    private static readonly string[] MetadataKeys = ["title", "requires", "tags", "priority", "once", "hub", "hub-limit"];

    private static bool IsMetadataKey(string line)
    {
        var key = line[1..].Split(' ', 2)[0];
        return MetadataKeys.Contains(key);
    }

    private bool TryParseMetadata(string line, int indent)
    {
        var keyword = line[1..].Split(' ', 2)[0];
        _symbols.Analysis?.Reference(keyword, At(indent + 1), AuthoringSymbolKind.Directive, MetadataKeys);
        var parts = line[1..].Split(' ', 2, StringSplitOptions.TrimEntries);
        var key = parts[0];
        var value = parts.Length > 1 ? parts[1] : string.Empty;
        var valueColumn = indent + line.IndexOf(' ', StringComparison.Ordinal) + 1;
        var scene = _scene!;
        if (key is "hub" or "hub-limit" && !scene.HubDirectives.Add(key))
        {
            _diagnostics.Error("TW1006", $"Duplicate @{key} directive.", At(indent));
            return true;
        }
        switch (key)
        {
            case "title":
                scene.Title = value;
                return true;
            case "requires":
                var expressionColumn = line.IndexOf(' ', StringComparison.Ordinal) < 0
                    ? indent + line.Length
                    : valueColumn + line[(valueColumn - indent)..].Length - line[(valueColumn - indent)..].TrimStart().Length;
                scene.Requires = ExpressionParser.ParseCondition(value, At(valueColumn), _symbols, _diagnostics,
                    At(expressionColumn));
                return true;
            case "tags":
                foreach (var tag in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    scene.Tags.Add(tag);
                }

                return true;
            case "priority":
                if (int.TryParse(value, out var priority))
                {
                    scene.Priority = priority;
                    scene.HasExplicitPriority = true;
                }
                else
                {
                    _diagnostics.Error("TW1002", "@priority needs a whole number.", At(valueColumn));
                }

                return true;
            case "once":
                scene.Once = true;
                return true;
            case "hub":
                var grouped = value.StartsWith("all(", StringComparison.Ordinal) || value.StartsWith("any(", StringComparison.Ordinal);
                var tags = grouped && value.EndsWith(')')
                    ? value[4..^1].Split(',', StringSplitOptions.TrimEntries)
                    : new[] { value };
                if (tags.Any(t => string.IsNullOrWhiteSpace(t) || t.Any(c => char.IsWhiteSpace(c) || c is '(' or ')' or ',')) ||
                    tags.Distinct(StringComparer.Ordinal).Count() != tags.Length)
                {
                    _diagnostics.Error("TW1006", "@hub needs a tag or all(tag, tag)/any(tag, tag) with distinct nonempty tags.", At(indent));
                }
                else
                {
                    scene.Hub = new Talewright.Rules.Storylets.StoryletTags(tags, value.StartsWith("all(", StringComparison.Ordinal));
                }
                return true;
            case "hub-limit":
                if (int.TryParse(value, out var limit) && limit > 0)
                {
                    scene.HubLimit = limit;
                }
                else
                {
                    _diagnostics.Error("TW1006", "@hub-limit needs a positive whole number.", At(indent));
                }
                return true;
            default:
                // Not metadata (e.g. a paragraph starting with "@"); treat the line as prose.
                return false;
        }
    }

    // ---- block conditionals ----------------------------------------------------------------------------------------

    private static bool IsBlockDirective(string line, out string directive)
    {
        directive = string.Empty;
        if (line[0] != '{' || InlineParser.FindClosingBrace(line, 0) != line.Length - 1)
        {
            return false;
        }

        var inner = line[1..^1].Trim();
        if (inner is "else" or "/if" || inner.StartsWith("if ", StringComparison.Ordinal))
        {
            directive = inner;
            return true;
        }

        return false;
    }

    private void ParseBlockDirective(string directive, int indent, string line)
    {
        var scene = _scene!;
        var keyword = directive.StartsWith("if ", StringComparison.Ordinal) ? "if" : directive;
        var keywordStart = line.IndexOf(keyword, StringComparison.Ordinal);
        _symbols.Analysis?.Tokens.Add(new(keyword, AnalysisCollector.Range(At(indent + keywordStart), keyword)));
        if (directive == "else")
        {
            if (scene.Frames.Count == 0 || scene.Frames.Peek().InElse)
            {
                _diagnostics.Error("TW1005", "{else} without a matching {if}.", At(indent));
                return;
            }

            scene.Frames.Peek().InElse = true;
            return;
        }

        if (directive == "/if")
        {
            if (scene.Frames.Count == 0)
            {
                _diagnostics.Error("TW1005", "{/if} without a matching {if}.", At(indent));
                return;
            }

            CloseFrame();
            return;
        }

        if (scene.Frames.Count == MaximumBlockConditionDepth)
        {
            _diagnostics.Error("TW1016",
                $"Conditions are nested more than {MaximumBlockConditionDepth} levels deep.", At(indent));
            scene.SuppressedConditionDepth = 1;
            scene.SuppressedConditionLocation = At(indent);
            return;
        }

        var lead = line[1..^1].Length - line[1..^1].TrimStart().Length;
        var condition = ExpressionParser.ParseCondition(directive[3..], At(indent + 1 + 3), _symbols, _diagnostics,
            At(indent + 1 + lead + 3))
            ?? new BoolLiteral(false);
        scene.Frames.Push(new Frame(condition, At(indent)));
    }

    private void SkipSuppressedBlockLine(string line)
    {
        if (!IsBlockDirective(line, out var directive))
        {
            return;
        }

        if (directive.StartsWith("if ", StringComparison.Ordinal))
        {
            _scene!.SuppressedConditionDepth++;
        }
        else if (directive == "/if")
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
        var colon = body.IndexOf(':', StringComparison.Ordinal);
        var speakerText = (colon < 0 ? body : body[..colon]).Trim();
        _symbols.Analysis?.Reference(speakerText, At(indent + 1 + body.Length - body.TrimStart().Length),
            AuthoringSymbolKind.Character);
        var who = colon > 0 ? body[..colon].Trim() : string.Empty;
        if (who.Length == 0 || !IdentifierRules.IsValid(who))
        {
            _diagnostics.Error("TW1007", "Speech lines look like '> who: text', where who is a character id.", At(indent));
            return;
        }

        var speaker = new EntityId(who);
        if (!_symbols.World.Characters.ContainsKey(speaker))
        {
            _diagnostics.Error("TW2001", $"Unknown character '{who}'. Declare it under characters in story.talewright.yaml.", At(indent + 1 + body.IndexOf(who, StringComparison.Ordinal)));
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
        _symbols.Analysis?.Reference(id, location, AuthoringSymbolKind.Scene, "END");
        if (id == "END")
        {
            return DivertTarget.End;
        }

        if (id.Length == 0)
        {
            _diagnostics.Error("TW1010", "Divert needs a target: '-> scene.id' or '-> END'.", location);
            return null;
        }

        if (!IdentifierRules.IsValid(id))
        {
            _diagnostics.Error("TW1008",
                $"'{id}' is not a valid scene id. Use an ASCII letter or '_' followed by letters, digits, '_' and single '-' or '.' separators.",
                location);
            return null;
        }

        if (!_symbols.Scenes.Contains(new SceneId(id)))
        {
            _diagnostics.Error("TW2002", $"Unknown scene '{id}'.", location);
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
        if (pos < rest.Length && rest[pos] == '[')
        {
            var close = FindClosingBracket(rest, pos, out var unclosedBrace);
            if (close < 0)
            {
                if (_discoverEffects is null && unclosedBrace >= 0 && ImageSyntax.IsCall(rest.AsSpan(unclosedBrace + 1)))
                {
                    ImageSyntax.Parse(SourceText.Single(rest, At(indent + 1)), unclosedBrace + 1,
                        rest.Length, false, _diagnostics, _symbols, _cancellationToken);
                }
                else if (_discoverEffects is null && unclosedBrace >= 0)
                {
                    ExpressionParser.ReportStringErrors(rest[(unclosedBrace + 1)..],
                        offset => At(indent + unclosedBrace + 2 + offset), _diagnostics);
                }
                _diagnostics.Error("TW1011", "Choice label is missing its closing ']'.", At(indent + 1 + pos));
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

        while (pos < rest.Length)
        {
            var column = indent + 1 + pos;
            var remaining = rest[pos..];
            if (remaining.StartsWith("(requires:", StringComparison.Ordinal))
            {
                var end = FindClosingParen(rest, pos);
                if (end < 0)
                {
                    ExpressionParser.ReportStringErrors(rest[(pos + 10)..],
                        offset => At(column + 10 + offset), _diagnostics);
                    _symbols.Analysis?.Expression(ExpressionContextKind.Condition, rest[(pos + 10)..], At(column + 10));
                    _diagnostics.Error("TW1011", "'(requires:' is missing its closing ')'.", At(column));
                    return;
                }

                requires = ExpressionParser.ParseCondition(rest[(pos + 10)..end], At(column + 10), _symbols, _diagnostics);
                pos = SkipSpaces(rest, end + 1);
            }
            else if (remaining[0] == '{')
            {
                var end = InlineParser.FindClosingBrace(rest, pos);
                if (end < 0)
                {
                    ExpressionParser.ReportStringErrors(rest[(pos + 1)..],
                        offset => At(column + 1 + offset), _diagnostics);
                    _symbols.Analysis?.Expression(ExpressionContextKind.Effects, rest[(pos + 1)..], At(column + 1));
                    _diagnostics.Error("TW1011", "Effect block is missing its closing '}'.", At(column));
                    return;
                }

                effects = ParseEffects(rest[(pos + 1)..end], At(column + 1)) ?? [];
                pos = SkipSpaces(rest, end + 1);
            }
            else if (remaining.StartsWith("->", StringComparison.Ordinal))
            {
                _symbols.Analysis?.Tokens.Add(new("->", AnalysisCollector.Range(At(column), "->")));
                hasDivert = true;
                divert = ParseDivertTarget(rest[(pos + 2)..], column + 2);
                pos = rest.Length;
            }
            else if (remaining.StartsWith("skill-check", StringComparison.Ordinal))
            {
                if (label is null)
                {
                    _diagnostics.Error("TW1011", "Textless choices need '-> target' or '-> END'; skill checks require a '[label]'.", At(indent));
                    return;
                }
                var spec = ParseCheckSpec(remaining["skill-check".Length..], column + "skill-check".Length);
                var (success, failure) = ParseOutcomeLines(At(column), insideBlock: false);
                if (spec is not null && success is not null && failure is not null)
                {
                    check = new CheckBranch(spec, success, failure);
                }

                pos = rest.Length;
            }
            else
            {
                var help = label is null
                    ? "Use '* [label] ...' or a textless '* ... -> target'."
                    : "Expected (requires: …), { effects }, -> target or skill-check.";
                _diagnostics.Error("TW1011", $"Unexpected '{remaining}' in choice. {help}", At(column));
                return;
            }
        }

        if (label is null && !hasDivert)
        {
            _diagnostics.Error("TW1011", "Textless choices need '-> target' or '-> END'.", At(indent));
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
        var spec = line[3..].TrimStart();
        var location = At(indent);
        if (!spec.StartsWith("skill-check", StringComparison.Ordinal))
        {
            _diagnostics.Error("TW1012", "Only '::: skill-check' blocks are supported.", location);
            return;
        }

        var check = ParseCheckSpec(spec["skill-check".Length..], indent + line.IndexOf("skill-check", StringComparison.Ordinal) + "skill-check".Length);
        var (success, failure) = ParseOutcomeLines(location, insideBlock: true);

        if (check is not null && success is not null && failure is not null)
        {
            scene.CurrentSteps.Add(new CheckStep(StepId(location), SpanFrom(location),
                new CheckBranch(check, success, failure)));
        }
    }

    /// <summary>Parses <c>[actor] skill dc expr</c>.</summary>
    private SkillCheck? ParseCheckSpec(string text, int column)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var dc = Array.IndexOf(words, "dc");
        if (_symbols.Analysis is { } analysis)
        {
            analysis.Tokens.Add(new("skill-check",
                AnalysisCollector.Range(At(column - "skill-check".Length), "skill-check")));
            var offset = 0;
            var references = words.TakeWhile(word => word != "dc").Take(2).ToArray();
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
            var dcStart = text.IndexOf(" dc ", StringComparison.Ordinal);
            if (dcStart >= 0)
                analysis.Expression(ExpressionContextKind.Number, text[(dcStart + 4)..], At(column + dcStart + 4));
            else if (text.EndsWith(" dc", StringComparison.Ordinal))
                analysis.Expression(ExpressionContextKind.Number, string.Empty, At(column + text.Length));
        }
        if (_symbols.World.Ruleset.SkillCheckRule is null)
        {
            _diagnostics.Error("TW3005", $"The '{_symbols.World.Ruleset.Id}' ruleset has no skill-check rule, so skill-check cannot be used.", At(column));
            return null;
        }

        if (dc is < 1 or > 2 || dc == words.Length - 1)
        {
            _diagnostics.Error("TW1013", "Skill checks look like 'skill-check [actor] skill dc 12'.", At(column));
            return null;
        }

        var actor = dc == 2 ? new EntityId(words[0]) : EntityId.Player;
        if (!_symbols.World.Characters.ContainsKey(actor))
        {
            _diagnostics.Error("TW2001", $"Unknown character '{actor}'.", At(column));
            return null;
        }

        var skill = new StatId(words[dc - 1]);
        if (!_symbols.World.Ruleset.HasStat(skill))
        {
            _diagnostics.Error("TW2001", $"Unknown stat '{skill}' (ruleset '{_symbols.World.Ruleset.Id}').", At(column));
            return null;
        }

        var dcText = text[(text.IndexOf(" dc ", StringComparison.Ordinal) + 4)..];
        var difficulty = ExpressionParser.ParseNumber(dcText, At(column + text.IndexOf(" dc ", StringComparison.Ordinal) + 4), _symbols, _diagnostics);
        return difficulty is null ? null : new SkillCheck(actor, skill, difficulty);
    }

    /// <summary>
    /// Consumes the <c>success:</c>/<c>failure:</c> lines that follow a check (and the closing <c>:::</c> of a block).
    /// </summary>
    private (CheckOutcome? Success, CheckOutcome? Failure) ParseOutcomeLines(SourceLocation checkLocation, bool insideBlock)
    {
        CheckOutcome? success = null;
        CheckOutcome? failure = null;
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

            if (insideBlock && line.SequenceEqual(":::"))
            {
                AdvanceLine();
                closed = true;
                break;
            }

            var isSuccess = line.StartsWith("success:", StringComparison.Ordinal);
            if (!isSuccess && !line.StartsWith("failure:", StringComparison.Ordinal))
            {
                break;
            }

            AdvanceLine();
            var outcome = ParseOutcome(line[8..].ToString(), indent + 8);
            if (isSuccess)
            {
                success = outcome;
            }
            else
            {
                failure = outcome;
            }
        }

        if (insideBlock && !closed)
        {
            _diagnostics.Error("TW1012", "'::: skill-check' block is never closed with ':::'.", checkLocation);
        }

        if (success is null || failure is null)
        {
            _diagnostics.Error("TW1009", "A skill check needs both a 'success:' and a 'failure:' line.", checkLocation);
        }

        return (success, failure);
    }

    private CheckOutcome? ParseOutcome(string text, int column)
    {
        var pos = SkipSpaces(text, 0);
        IReadOnlyList<Effect> effects = [];
        DivertTarget? divert = null;
        if (pos < text.Length && text[pos] == '{')
        {
            var end = InlineParser.FindClosingBrace(text, pos);
            if (end < 0)
            {
                _symbols.Analysis?.Expression(ExpressionContextKind.Effects, text[(pos + 1)..], At(column + pos + 1));
                _diagnostics.Error("TW1011", "Effect block is missing its closing '}'.", At(column + pos));
                return null;
            }

            effects = ParseEffects(text[(pos + 1)..end], At(column + pos + 1)) ?? [];
            pos = SkipSpaces(text, end + 1);
        }

        if (pos < text.Length)
        {
            if (!text[pos..].StartsWith("->", StringComparison.Ordinal))
            {
                _diagnostics.Error("TW1009", "Outcomes look like 'success: { effects } -> target'.", At(column + pos));
                return null;
            }

            _symbols.Analysis?.Tokens.Add(new("->", AnalysisCollector.Range(At(column + pos), "->")));
            divert = ParseDivertTarget(text[(pos + 2)..], column + pos + 2);
        }

        return new CheckOutcome(effects, divert);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private static int SkipSpaces(string text, int pos)
    {
        while (pos < text.Length && char.IsWhiteSpace(text[pos]))
        {
            pos++;
        }

        return pos;
    }

    private static int FindClosingBracket(string text, int open, out int unclosedBrace)
    {
        unclosedBrace = -1;
        var brackets = 0;
        for (var i = open + 1; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '{':
                    var close = InlineParser.FindClosingBrace(text, i);
                    if (close < 0)
                    {
                        unclosedBrace = i;
                        return -1;
                    }
                    i = close;
                    break;
                case '\\':
                    i++;
                    break;
                case '[':
                    brackets++;
                    break;
                case ']':
                    if (brackets == 0) return i;
                    brackets--;
                    break;
            }
        }

        return -1;
    }

    private static int FindClosingParen(string text, int open)
    {
        var depth = 0;
        var inString = false;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (inString && c == '\\')
            {
                i++;
            }
            else if (c == '"')
            {
                inString = !inString;
            }
            else if (!inString && c == '(')
            {
                depth++;
            }
            else if (!inString && c == ')' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
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
        public HashSet<string> HubDirectives { get; } = [];
        public List<SceneStep> Steps { get; } = [];
        public int ChoiceCount { get; set; }
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
