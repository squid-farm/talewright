using System.Text.RegularExpressions;
using Talewright.Core.Identity;
using Talewright.Narrative.Templates;
using Talewright.Rules.Checks;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Tale.Diagnostics;
using Talewright.Tale.Model;
using Talewright.Tale.Analysis;

namespace Talewright.Tale.Parsing;

/// <summary>A compiled scene plus where it was declared (for validator diagnostics).</summary>
internal sealed record ParsedScene(SceneDefinition Definition, SourceLocation Location)
{
    public required int EndLine { get; init; }
}

/// <summary>
/// Line-oriented parser for <c>.tale</c> files. Each line is classified by its first characters (<c>===</c>,
/// <c>@</c>, <c>&gt;</c>, <c>~</c>, <c>*</c>, <c>-&gt;</c>, <c>:::</c>, <c>{if}</c>…); everything else is prose,
/// grouped into paragraphs by blank lines and handed to <see cref="InlineParser"/>.
/// </summary>
/// <remarks>
/// Block-level <c>{if}</c> lines push a condition onto a stack. Prose inside goes into a
/// <see cref="ConditionalBlockTemplate"/>; choices, <c>~</c> effects and diverts inside get the combined
/// condition attached (a choice's <c>requires</c> is ANDed with it), so "this choice only exists if…" can be
/// written either way.
/// </remarks>
internal sealed partial class TaleParser
{
    [GeneratedRegex(@"^[A-Za-z0-9_]+(?:[.\-][A-Za-z0-9_]+)*$")]
    internal static partial Regex IdPattern();

    private readonly string _file;
    private readonly string[] _lines;
    private readonly StorySymbols _symbols;
    private readonly DiagnosticBag _diagnostics;
    private readonly Action<string>? _discoverEffects;
    private readonly Action<SceneId, SourceLocation>? _discoverScene;
    private readonly List<ParsedScene> _scenes = [];
    private SceneBuilder? _scene;
    private int _index;

    private TaleParser(string file, string text, StorySymbols symbols, DiagnosticBag diagnostics,
        Action<string>? discoverEffects = null, Action<SceneId, SourceLocation>? discoverScene = null)
    {
        _file = file;
        _lines = text.ReplaceLineEndings("\n").Split('\n');
        _symbols = symbols;
        _diagnostics = diagnostics;
        _discoverEffects = discoverEffects;
        _discoverScene = discoverScene;
    }

    public static List<ParsedScene> Parse(string file, string text, StorySymbols symbols, DiagnosticBag diagnostics)
    {
        var parser = new TaleParser(file, text, symbols, diagnostics);
        parser.Run();
        return parser._scenes;
    }

    public static void Discover(string file, string text, StorySymbols symbols,
        Action<string> effects, Action<SceneId, SourceLocation> scene) =>
        new TaleParser(file, text, symbols, new DiagnosticBag(), effects, scene).Run();

    private SourceLocation At(int column) => new(_file, _index + 1, column + 1);

    private void Run()
    {
        for (_index = 0; _index < _lines.Length; _index++)
        {
            var raw = _lines[_index];
            var trimmed = raw.Trim();
            var indent = raw.Length - raw.TrimStart().Length;

            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed.StartsWith("===", StringComparison.Ordinal))
            {
                FinishScene();
                StartScene(trimmed[3..].Trim(), At(indent));
                continue;
            }

            if (_scene is null)
            {
                if (trimmed.Length > 0)
                {
                    _diagnostics.Error("TW3001", "Text before the first scene. Start a scene with '=== scene.id'.", At(indent));
                }

                continue;
            }

            ParseSceneLine(trimmed, indent);
        }

        FinishScene();
    }

    private void StartScene(string id, SourceLocation location)
    {
        _discoverScene?.Invoke(new SceneId(id), location);
        _scene = new SceneBuilder(new SceneId(id), location);
        if (!IdPattern().IsMatch(id) || id == "END")
        {
            _diagnostics.Error("TW1008", $"'{id}' is not a valid scene id. Use letters, digits, '-', '_' and '.' (e.g. temple.entrance).", location);
        }
    }

    private void FinishScene()
    {
        if (_scene is null)
        {
            return;
        }

        FlushParagraph();
        while (_scene.Frames.Count > 0)
        {
            _diagnostics.Error("TW1005", "{if} block is never closed with {/if}.", _scene.Frames.Peek().Location);
            CloseFrame();
        }

        _scenes.Add(new ParsedScene(_scene.Build(), _scene.Location) { EndLine = _index });
        _scene = null;
    }

    private void ParseSceneLine(string line, int indent)
    {
        var scene = _scene!;
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
                FlushParagraph();
                ParseSpeech(line, indent);
                return;
            case ['~', ..]:
                FlushParagraph();
                ParseEntryEffects(line[1..], indent + 1);
                return;
            case ['*', ' ' or '\t' or '[', ..]:
                // "* [label]" is a choice; "*text*" is emphasis.
                FlushParagraph();
                ParseChoice(line, indent);
                return;
            case ['-', '>', ..]:
                FlushParagraph();
                var target = ParseDivertTarget(line[2..], indent + 2);
                if (target is not null)
                {
                    scene.Diverts.Add(new ConditionalDivert(scene.CurrentCondition, target));
                }

                return;
            case [':', ':', ':', ..]:
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
                else if (grouped)
                {
                    scene.Hub = new Talewright.Rules.Storylets.StoryletTags(tags, value.StartsWith("all(", StringComparison.Ordinal));
                }
                else
                {
                    scene.HubTag = value;
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

        var lead = line[1..^1].Length - line[1..^1].TrimStart().Length;
        var condition = ExpressionParser.ParseCondition(directive[3..], At(indent + 1 + 3), _symbols, _diagnostics,
            At(indent + 1 + lead + 3))
            ?? new BoolLiteral(false);
        scene.Frames.Push(new Frame(condition, At(indent)));
    }

    private void CloseFrame()
    {
        var scene = _scene!;
        var frame = scene.Frames.Pop();
        if (frame.Then.Count > 0 || frame.Else.Count > 0)
        {
            scene.CurrentBlocks.Add(new ConditionalBlockTemplate(frame.Condition, frame.Then, frame.Else));
        }
    }

    // ---- prose -----------------------------------------------------------------------------------------------------

    private void FlushParagraph()
    {
        var scene = _scene!;
        if (scene.Paragraph.IsEmpty)
        {
            return;
        }

        var inlines = _discoverEffects is null ? InlineParser.Parse(scene.Paragraph, _symbols, _diagnostics) : [];
        if (inlines.Count > 0)
        {
            scene.CurrentBlocks.Add(new ParagraphTemplate(inlines));
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
        if (who.Length == 0 || !IdPattern().IsMatch(who))
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
                    _symbols, new DiagnosticBag());
            }
            return;
        }

        var text = body[(colon + 1)..];
        var lead = text.Length - text.TrimStart().Length;
        var inlines = InlineParser.Parse(SourceText.Single(text.Trim(), At(indent + 1 + colon + 1 + lead)), _symbols, _diagnostics);
        _scene!.CurrentBlocks.Add(new SpeechTemplate(speaker, inlines));
    }

    // ---- effects and diverts ---------------------------------------------------------------------------------------

    private void ParseEntryEffects(string text, int column)
    {
        var lead = text.Length - text.TrimStart().Length;
        var effects = ParseEffects(text.Trim(), At(column + lead));
        foreach (var effect in effects ?? [])
        {
            _scene!.EntryEffects.Add(new ConditionalEffect(_scene.CurrentCondition, effect));
        }
    }

    private IReadOnlyList<Effect>? ParseEffects(string text, SourceLocation location)
    {
        if (_discoverEffects is not null)
        {
            _discoverEffects(text);
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
        var rest = line[1..];
        var pos = SkipSpaces(rest, 0);
        if (pos >= rest.Length || rest[pos] != '[')
        {
            _diagnostics.Error("TW1011", "Choices look like '* [label] -> target'.", At(indent));
            return;
        }

        var close = FindClosingBracket(rest, pos);
        if (close < 0)
        {
            _diagnostics.Error("TW1011", "Choice label is missing its closing ']'.", At(indent + 1 + pos));
            return;
        }

        var labelText = rest[(pos + 1)..close];
        var label = _discoverEffects is null
            ? InlineParser.Parse(SourceText.Single(labelText, At(indent + 1 + pos + 1)), _symbols, _diagnostics)
            : [];
        Expr? requires = null;
        IReadOnlyList<Effect> effects = [];
        DivertTarget? divert = null;
        CheckBranch? check = null;

        pos = SkipSpaces(rest, close + 1);
        while (pos < rest.Length)
        {
            var column = indent + 1 + pos;
            var remaining = rest[pos..];
            if (remaining.StartsWith("(requires:", StringComparison.Ordinal))
            {
                var end = FindClosingParen(rest, pos);
                if (end < 0)
                {
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
                    _symbols.Analysis?.Expression(ExpressionContextKind.Effects, rest[(pos + 1)..], At(column + 1));
                    _diagnostics.Error("TW1011", "Effect block is missing its closing '}'.", At(column));
                    return;
                }

                effects = ParseEffects(rest[(pos + 1)..end], At(column + 1)) ?? [];
                pos = SkipSpaces(rest, end + 1);
            }
            else if (remaining.StartsWith("->", StringComparison.Ordinal))
            {
                divert = ParseDivertTarget(rest[(pos + 2)..], column + 2);
                pos = rest.Length;
            }
            else if (remaining.StartsWith("skill-check", StringComparison.Ordinal))
            {
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
                _diagnostics.Error("TW1011", $"Unexpected '{remaining}' in choice. Expected (requires: …), {{ effects }}, -> target or skill-check.", At(column));
                return;
            }
        }

        scene.Choices.Add(new ChoiceDefinition
        {
            Key = $"c{scene.Choices.Count}",
            Label = label,
            Requires = And(scene.CurrentCondition, requires),
            Effects = effects,
            Check = check,
            Divert = divert,
        });
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

        if (scene.Frames.Count > 0)
        {
            _diagnostics.Error("TW3006", "A '::: skill-check' block cannot be inside {if}. Put the check in its own scene and divert to it conditionally.", location);
            return;
        }

        if (scene.EntryCheck is not null)
        {
            _diagnostics.Error("TW3006", "A scene can have only one '::: skill-check' block.", location);
            return;
        }

        if (check is not null && success is not null && failure is not null)
        {
            scene.EntryCheck = new CheckBranch(check, success, failure);
        }
    }

    /// <summary>Parses <c>[actor] skill dc expr</c>.</summary>
    private SkillCheck? ParseCheckSpec(string text, int column)
    {
        if (_symbols.Analysis is { } analysis)
        {
            var offset = 0;
            var references = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).TakeWhile(word => word != "dc").Take(2).ToArray();
            if (references.Length == 0)
            {
                analysis.Reference("", At(column), AuthoringSymbolKind.Character);
                analysis.Reference("", At(column), AuthoringSymbolKind.Stat);
            }
            for (var index = 0; index < references.Length; index++)
            {
                var word = references[index];
                offset = text.IndexOf(word, offset, StringComparison.Ordinal);
                analysis.Reference(word, At(column + offset), AuthoringSymbolKind.Stat);
                if (index == 0) analysis.Reference(word, At(column + offset), AuthoringSymbolKind.Character);
                offset += word.Length;
            }
            if (references.Length == 1 && column + offset < _lines[_index].Length &&
                char.IsWhiteSpace(_lines[_index][column + offset]) &&
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

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var dc = Array.IndexOf(words, "dc");
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
        while (_index + 1 < _lines.Length)
        {
            var raw = _lines[_index + 1];
            var line = raw.Trim();
            var indent = raw.Length - raw.TrimStart().Length;
            if (line.StartsWith("//", StringComparison.Ordinal) || (insideBlock && line.Length == 0))
            {
                _index++;
                continue;
            }

            if (insideBlock && line == ":::")
            {
                _index++;
                closed = true;
                break;
            }

            var isSuccess = line.StartsWith("success:", StringComparison.Ordinal);
            if (!isSuccess && !line.StartsWith("failure:", StringComparison.Ordinal))
            {
                break;
            }

            _index++;
            var outcome = ParseOutcome(line[8..], indent + 8);
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

            divert = ParseDivertTarget(text[(pos + 2)..], column + pos + 2);
        }

        return new CheckOutcome(effects, divert);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private static Expr? And(Expr? a, Expr? b) => (a, b) switch
    {
        (null, _) => b,
        (_, null) => a,
        _ => new BinaryExpr(BinaryOp.And, a, b),
    };

    private static int SkipSpaces(string text, int pos)
    {
        while (pos < text.Length && char.IsWhiteSpace(text[pos]))
        {
            pos++;
        }

        return pos;
    }

    private static int FindClosingBracket(string text, int open)
    {
        var braces = 0;
        for (var i = open + 1; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '{':
                    braces++;
                    break;
                case '}':
                    braces--;
                    break;
                case '\\':
                    i++;
                    break;
                case ']' when braces == 0:
                    return i;
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
            if (c == '"')
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
        public List<TemplateBlock> Then { get; } = [];
        public List<TemplateBlock> Else { get; } = [];
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
        public bool Once { get; set; }
        public string? HubTag { get; set; }
        public Talewright.Rules.Storylets.StoryletTags? Hub { get; set; }
        public int? HubLimit { get; set; }
        public HashSet<string> HubDirectives { get; } = [];
        public List<ConditionalEffect> EntryEffects { get; } = [];
        public List<TemplateBlock> Body { get; } = [];
        public CheckBranch? EntryCheck { get; set; }
        public List<ChoiceDefinition> Choices { get; } = [];
        public List<ConditionalDivert> Diverts { get; } = [];
        public Stack<Frame> Frames { get; } = new();
        public SourceText Paragraph { get; set; } = new();

        public List<TemplateBlock> CurrentBlocks =>
            Frames.Count == 0 ? Body : Frames.Peek().InElse ? Frames.Peek().Else : Frames.Peek().Then;

        /// <summary>The conjunction of every enclosing <c>{if}</c> (negated inside <c>{else}</c>), or null.</summary>
        public Expr? CurrentCondition =>
            Frames.Reverse().Aggregate<Frame, Expr?>(null, (acc, f) =>
                And(acc, f.InElse ? new UnaryExpr(UnaryOp.Not, f.Condition) : f.Condition));

        public SceneDefinition Build() => new()
        {
            Id = Id,
            Title = Title,
            Requires = Requires,
            Tags = Tags,
            Priority = Priority,
            Once = Once,
            HubTag = HubTag,
            Hub = Hub,
            HubLimit = HubLimit,
            EntryEffects = EntryEffects,
            Body = Body,
            EntryCheck = EntryCheck,
            Choices = Choices,
            Diverts = Diverts,
        };
    }
}
