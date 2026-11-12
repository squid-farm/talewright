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
/// Binds a file's <see cref="TaleFileSyntax"/> against story-wide <see cref="StorySymbols"/>, producing scenes and
/// steps. Syntax has already classified every line; prose lines are grouped into paragraphs by blank lines and
/// handed to <see cref="InlineParser"/>. <see cref="TaleDeclarations"/> collects symbols from the same syntax first.
/// </summary>
/// <remarks>
/// Block-level conditions retain their nested steps so the runtime decides each branch once.
/// </remarks>
internal sealed partial class TaleBinder
{
    private readonly string _file;
    private readonly TaleFileSyntax _syntax;
    private readonly string _text;
    private readonly StorySymbols _symbols;
    private readonly DiagnosticBag _diagnostics;
    private readonly CancellationToken _cancellationToken;
    private int _linesSinceCancellationCheck;
    private readonly List<ParsedScene> _scenes = [];
    private SceneBuilder? _scene;
    private TaleSceneSyntax? _sceneSyntax;
    private int _lineCursor;
    private int _index;
    private int _lineStart;
    private int _lineLength;

    private TaleBinder(string file, TaleFileSyntax syntax, StorySymbols symbols, DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        _file = file;
        _syntax = syntax;
        _text = syntax.Text;
        _symbols = symbols;
        _diagnostics = diagnostics;
        _cancellationToken = cancellationToken;
    }

    public static List<ParsedScene> Parse(string file, TaleFileSyntax syntax, StorySymbols symbols,
        DiagnosticBag diagnostics, CancellationToken cancellationToken = default)
    {
        var parser = new TaleBinder(file, syntax, symbols, diagnostics, cancellationToken);
        parser.Run();
        return parser._scenes;
    }

    private SourceLocation At(int column) => new(_file, _index + 1, column + 1);
    private SourceRange SpanFrom(SourceLocation start) => new(start, At(_lineLength));
    private static string StepId(SourceLocation start) => $"step:{start.Line}:{start.Column}";

    private void Run()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        _syntax.Report(_file, _diagnostics);
        foreach (var sceneSyntax in _syntax.Scenes)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _index = sceneSyntax.Line;
            _symbols.Analysis?.Tokens.Add(new(SceneVocabulary.SceneHeader,
                AnalysisCollector.Range(At(sceneSyntax.HeaderIndent), SceneVocabulary.SceneHeader))
                { Kind = AuthoringTokenKind.SceneHeader });
            FinishScene();
            StartScene(sceneSyntax);
            CountLine();
            for (_lineCursor = 0; _lineCursor < sceneSyntax.Lines.Count; _lineCursor++)
            {
                var line = MoveTo(_lineCursor);
                if (line.Kind != TaleLineKind.Comment)
                    ParseSceneLine(line);
                CountLine();
            }
        }

        FinishScene();
    }

    private TaleLineSyntax MoveTo(int cursor)
    {
        var scene = _sceneSyntax!;
        var line = scene.Lines[cursor];
        _lineCursor = cursor;
        _index = scene.FileLine(line.Line);
        _lineStart = scene.FileOffset(line.Start);
        _lineLength = line.Length;
        return line;
    }

    private bool TryPeekLine(out TaleLineSyntax line)
    {
        var lines = _sceneSyntax!.Lines;
        if (_lineCursor + 1 < lines.Count)
        {
            line = lines[_lineCursor + 1];
            return true;
        }

        line = default;
        return false;
    }

    private void CountLine()
    {
        if ((_linesSinceCancellationCheck++ & 63) == 0)
            _cancellationToken.ThrowIfCancellationRequested();
    }

    private void StartScene(TaleSceneSyntax syntax)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var location = At(syntax.HeaderIndent);
        _sceneSyntax = syntax;
        _scene = new SceneBuilder(new SceneId(syntax.Id), location);
        TaleFileSyntax.Report(_file, syntax, _diagnostics);
    }

    private void FinishScene()
    {
        if (_scene is null)
        {
            return;
        }

        FlushParagraph();
        // Syntax reported unclosed blocks; close them so their steps are kept.
        while (_scene.Frames.Count > 0)
            CloseFrame();

        _scenes.Add(new ParsedScene(_scene.Build(), _scene.Location)
        {
            EndLine = _sceneSyntax!.Line + _sceneSyntax.LineCount,
            HasExplicitPriority = _scene.HasExplicitPriority,
            DroppedMalformedChoice = _scene.DroppedMalformedChoice,
        });
        _scene = null;
    }

    private void ParseSceneLine(TaleLineSyntax syntax)
    {
        var scene = _scene!;
        // Attached lines are bound by their skill check.
        if (syntax.IsSuppressed || syntax.IsAttached)
            return;

        if (syntax.Kind == TaleLineKind.Blank)
        {
            FlushParagraph();
            return;
        }

        var indent = syntax.Indent;
        if (syntax.Kind == TaleLineKind.Metadata && TryParseMetadata(syntax))
        {
            return;
        }

        if (syntax.Kind is TaleLineKind.BlockIf or TaleLineKind.BlockElse or TaleLineKind.BlockEndIf)
        {
            FlushParagraph();
            ParseBlockDirective(syntax);
            return;
        }

        switch (syntax.Kind)
        {
            case TaleLineKind.Speech:
                _symbols.Analysis?.Tokens.Add(new(SceneVocabulary.SpeechMarker.ToString(),
                    AnalysisCollector.Range(At(indent), SceneVocabulary.SpeechMarker.ToString()))
                    { Kind = AuthoringTokenKind.Speech });
                FlushParagraph();
                ParseSpeech(syntax);
                return;
            case TaleLineKind.Effects:
                _symbols.Analysis?.Tokens.Add(new(SceneVocabulary.EffectsMarker.ToString(),
                    AnalysisCollector.Range(At(indent), SceneVocabulary.EffectsMarker.ToString()))
                    { Kind = AuthoringTokenKind.Effects });
                FlushParagraph();
                var effects = ExpressionParser.ParseEffects(Value(syntax), At(Part(syntax).ValueStart), _symbols,
                    _diagnostics);
                if (effects is not null)
                {
                    var effectStart = At(indent);
                    scene.CurrentSteps.Add(new EffectStep(StepId(effectStart), SpanFrom(effectStart), effects));
                }

                return;
            case TaleLineKind.Choice:
                _symbols.Analysis?.Tokens.Add(new(SceneVocabulary.ChoiceMarker.ToString(),
                    AnalysisCollector.Range(At(indent), SceneVocabulary.ChoiceMarker.ToString()))
                    { Kind = AuthoringTokenKind.Choice });
                FlushParagraph();
                ParseChoice(syntax);
                return;
            case TaleLineKind.Divert:
                _symbols.Analysis?.Tokens.Add(new(SceneVocabulary.Divert,
                    AnalysisCollector.Range(At(indent), SceneVocabulary.Divert))
                    { Kind = AuthoringTokenKind.Divert });
                FlushParagraph();
                var targetLocation = At(Part(syntax).ValueStart);
                var targetId = Value(syntax);
                _symbols.Analysis?.Reference(targetId, targetLocation, AuthoringSymbolKind.Scene, SceneVocabulary.End);
                var target = syntax.IsInvalid ? null : BindDivertTarget(targetId, targetLocation);
                if (target is not null)
                {
                    var start = At(indent);
                    scene.CurrentSteps.Add(new DivertStep(StepId(start), SpanFrom(start), target));
                }

                return;
            case TaleLineKind.SkillCheckBlock:
                _symbols.Analysis?.Tokens.Add(new(SceneVocabulary.SkillCheckBlock,
                    AnalysisCollector.Range(At(indent), SceneVocabulary.SkillCheckFence))
                    { Kind = AuthoringTokenKind.SkillCheckBlock });
                FlushParagraph();
                ParseBlockCheck(syntax);
                return;
            // Syntax reported outcome lines that no check attached.
            case TaleLineKind.SuccessOutcome or TaleLineKind.FailureOutcome:
            case TaleLineKind.MisplacedMetadata:
                return;
        }

        scene.Paragraph.AppendLine(Content(syntax), At(indent));
    }

    private string Content(TaleLineSyntax line) => _syntax.Content(_sceneSyntax!, line).ToString();

    private TaleLinePart Part(TaleLineSyntax line) => _sceneSyntax!.Part(line);

    private string Head(TaleLineSyntax line) => _syntax.Head(_sceneSyntax!, line).ToString();

    private string Value(TaleLineSyntax line) => _syntax.Value(_sceneSyntax!, line).ToString();

    private ReadOnlySpan<TaleClause> Clauses(TaleLineSyntax line) => _sceneSyntax!.Clauses(line);

    private string Value(TaleLineSyntax line, TaleClause clause) => _syntax.Value(_sceneSyntax!, line, clause).ToString();

    // ---- metadata --------------------------------------------------------------------------------------------------

    private bool TryParseMetadata(TaleLineSyntax syntax)
    {
        _symbols.Analysis?.Reference(Head(syntax), At(Part(syntax).HeadStart), AuthoringSymbolKind.Directive,
            SceneVocabulary.MetadataSpellings);
        if (syntax.MetadataKey is not { } key)
        {
            if (Head(syntax) != SceneVocabulary.Clear)
                return false;

            if (!syntax.IsInvalid)
                _scene!.ClearsPrevious = true;
            return true;
        }

        var metadataPart = Part(syntax);
        var value = Value(syntax);
        var valueLocation = At(value.Length == 0
            ? metadataPart.HeadStart + metadataPart.HeadLength
            : metadataPart.MetadataValueColumn(syntax));
        if (key == SceneMetadataKey.Hub)
            _symbols.Analysis?.HubSelector(value, valueLocation);
        if (key == SceneMetadataKey.Tags)
            _symbols.Analysis?.TagSelector(value, valueLocation);
        if (syntax.IsInvalid)
            return true;
        var scene = _scene!;
        switch (key)
        {
            case SceneMetadataKey.Title:
                scene.Title = value;
                return true;
            case SceneMetadataKey.ChoiceLabel:
                scene.ChoiceLabel = InlineParser.Parse(SourceText.Single(value, valueLocation), _symbols,
                    _diagnostics, cancellationToken: _cancellationToken);
                return true;
            case SceneMetadataKey.Continues:
                scene.ContinuesPrevious = true;
                return true;
            case SceneMetadataKey.Requires:
                var part = Part(syntax);
                var (requirement, shown) = BindRequirement(value, At(part.MetadataValueColumn(syntax)),
                    CompilerDiagnosticCodes.InvalidSceneMetadata, authoringLocation: At(part.ValueStart));
                scene.Requires = requirement;
                scene.Shown = shown;
                return true;
            case SceneMetadataKey.Tags:
                _symbols.Analysis?.Tags(value, valueLocation);
                foreach (var tag in TaleSyntaxParser.ReadTags(value))
                {
                    scene.Tags.Add(tag);
                }

                return true;
            case SceneMetadataKey.Priority:
                scene.Priority = int.Parse(value);
                scene.HasExplicitPriority = true;
                return true;
            case SceneMetadataKey.Once:
                scene.Once = true;
                return true;
            case SceneMetadataKey.Hub:
                TaleSyntaxParser.TryReadHub(value, out var tags, out var all);
                _symbols.Analysis?.HubTags(value, valueLocation);
                scene.Hub = new Talewright.Rules.Storylets.StoryletTags(tags, all);
                return true;
            case SceneMetadataKey.HubLimit:
                scene.HubLimit = int.Parse(value);
                return true;
            default:
                throw new ArgumentOutOfRangeException(nameof(key), key, null);
        }
    }

    // ---- block conditionals ----------------------------------------------------------------------------------------

    private void ParseBlockDirective(TaleLineSyntax syntax)
    {
        var scene = _scene!;
        var keyword = Head(syntax);
        _symbols.Analysis?.Tokens.Add(new(keyword, AnalysisCollector.Range(At(Part(syntax).HeadStart), keyword))
            { Kind = AuthoringSyntaxVocabulary.ConditionalKind(keyword) });
        // Syntax reported unmatched and over-nested directives; the frames below always balance.
        if (syntax.IsInvalid)
            return;
        switch (syntax.Kind)
        {
            case TaleLineKind.BlockElse:
                scene.Frames.Peek().InElse = true;
                return;
            case TaleLineKind.BlockEndIf:
                CloseFrame();
                return;
        }

        var condition = ExpressionParser.ParseCondition(Value(syntax),
            At(syntax.Indent + 1 + SceneVocabulary.IfPrefix.Length), _symbols, _diagnostics, At(Part(syntax).ValueStart))
            ?? new BoolLiteral(false);
        scene.Frames.Push(new Frame(condition, At(syntax.Indent)));
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

        var inlines = InlineParser.Parse(scene.Paragraph, _symbols, _diagnostics, cancellationToken: _cancellationToken);
        if (inlines.Count > 0)
        {
            var start = scene.Paragraph.Locate(0);
            scene.CurrentSteps.Add(new ProseStep(StepId(start),
                new SourceRange(start, scene.Paragraph.Locate(scene.Paragraph.Text.Length)), new ParagraphTemplate(inlines)));
        }

        scene.Paragraph = new SourceText();
    }

    private void ParseSpeech(TaleLineSyntax syntax)
    {
        var who = Head(syntax);
        var speakerLocation = At(Part(syntax).HeadStart);
        _symbols.Analysis?.Reference(who, speakerLocation, AuthoringSymbolKind.Character);
        if (syntax.IsInvalid)
            return;

        var speaker = new EntityId(who);
        var text = SourceText.Single(Value(syntax), At(Part(syntax).ValueStart));
        if (!_symbols.World.Characters.ContainsKey(speaker))
        {
            _diagnostics.Error(CompilerDiagnosticCodes.UnknownNameOrProperty, $"Unknown character '{who}'. Declare it under characters in story.talewright.yaml.", speakerLocation);
            if (_symbols.Analysis is not null)
                InlineParser.Parse(text, _symbols, new DiagnosticBag(), cancellationToken: _cancellationToken);
            return;
        }

        var inlines = InlineParser.Parse(text, _symbols, _diagnostics, cancellationToken: _cancellationToken);
        var start = At(syntax.Indent);
        _scene!.CurrentSteps.Add(new ProseStep(StepId(start), SpanFrom(start), new SpeechTemplate(speaker, inlines)));
    }

    // ---- effects and diverts ---------------------------------------------------------------------------------------

    private DivertTarget? BindDivertTarget(string id, SourceLocation location)
    {
        if (id == SceneVocabulary.End)
        {
            return DivertTarget.End;
        }

        if (!_symbols.Scenes.Contains(new SceneId(id)))
        {
            _diagnostics.Error(CompilerDiagnosticCodes.UnknownScene, $"Unknown scene '{id}'.", location);
            return null;
        }

        return new DivertTarget(new SceneId(id));
    }

    // ---- choices ---------------------------------------------------------------------------------------------------

    private void ParseChoice(TaleLineSyntax syntax)
    {
        var scene = _scene!;
        var start = At(syntax.Indent);
        IReadOnlyList<TemplateInline>? label = null;
        SourceLocation? labelLocation = null;
        Expr? requires = null;
        ShownDefinition? shown = null;
        IReadOnlyList<Effect> effects = [];
        DivertTarget? divert = null;
        CheckBranch? check = null;
        var hasRequires = false;
        var hasEffects = false;
        var malformedTextless = false;
        var clauses = Clauses(syntax);
        for (var index = 0; index < clauses.Length; index++)
        {
            var clause = clauses[index];
            var value = Value(syntax, clause);
            var location = At(clause.ValueStart);
            switch (clause.Kind)
            {
                case TaleClauseKind.Label:
                    labelLocation = location;
                    label = InlineParser.Parse(SourceText.Single(value, location), _symbols, _diagnostics,
                        cancellationToken: _cancellationToken);
                    break;
                case TaleClauseKind.UnclosedLabel when _symbols.Analysis is not null:
                    InlineParser.AnalyzePartial(SourceText.Single(value, location), _symbols, _cancellationToken);
                    break;
                case TaleClauseKind.UnclosedImage:
                    var rest = Content(syntax)[1..];
                    ImageSyntax.Parse(SourceText.Single(rest, At(syntax.Indent + 1)), clause.ValueStart - syntax.Indent - 1,
                        rest.Length, false, _diagnostics, _symbols, _cancellationToken);
                    break;
                case TaleClauseKind.UnclosedBrace:
                    ExpressionParser.ReportStringErrors(value, offset => At(clause.ValueStart + offset), _diagnostics);
                    break;
                case TaleClauseKind.Requires:
                    if (label is null)
                    {
                        _diagnostics.Error(CompilerDiagnosticCodes.MalformedChoice,
                            "A choice without a label only allows a divert. Wrap it in a `{if ...}` block or add a label, " +
                            $"e.g. `{SceneVocabulary.ChoiceMarker} [{ChoiceDefinition.ContinueLabelText}] " +
                            $"{SceneVocabulary.ChoiceRequiresExample} {SceneVocabulary.Divert} target`.",
                            At(clause.Start), At(clause.ValueStart + clause.ValueLength + 1));
                        malformedTextless = true;
                    }

                    var (condition, parsedShown) = BindRequirement(value, location,
                        CompilerDiagnosticCodes.MalformedChoice, highlightUnexpectedTokens: true);
                    if (!hasRequires)
                    {
                        requires = condition;
                        shown = parsedShown;
                    }
                    hasRequires = true;
                    break;
                case TaleClauseKind.UnclosedRequires or TaleClauseKind.UnclosedEffects:
                    var context = clause.Kind == TaleClauseKind.UnclosedRequires
                        ? ExpressionContextKind.Condition
                        : ExpressionContextKind.Effects;
                    var parsed = clause.Kind == TaleClauseKind.UnclosedRequires
                        ? RequirementClauseSyntax.Parse(value, _cancellationToken)
                        : default;
                    if (clause.Kind == TaleClauseKind.UnclosedRequires)
                        _symbols.Analysis?.RequirementSuffix(value, location, parsed);
                    var expression = clause.Kind == TaleClauseKind.UnclosedRequires
                        ? value[..parsed.ExpressionLength]
                        : value;
                    var tokens = _symbols.Analysis?.Expression(context, expression, location) ?? Lexer.Tokenize(expression);
                    ExpressionParser.ReportStringErrors(tokens, offset => At(clause.ValueStart + offset), _diagnostics);
                    break;
                case TaleClauseKind.InvalidCondition when _symbols.Analysis is not null:
                    _symbols.Analysis.Expression(ExpressionContextKind.Condition, value, location);
                    break;
                case TaleClauseKind.Effects:
                    if (label is null)
                    {
                        _diagnostics.Error(CompilerDiagnosticCodes.MalformedChoice,
                            "A choice without a label only allows a divert. Add a label, " +
                            $"e.g. `{SceneVocabulary.ChoiceMarker} [{ChoiceDefinition.ContinueLabelText}] " +
                            $"{{ effects }} {SceneVocabulary.Divert} target`.",
                            At(clause.Start), At(clause.ValueStart + clause.ValueLength + 1));
                        malformedTextless = true;
                    }

                    var parsedEffects = ExpressionParser.ParseEffects(value, location, _symbols, _diagnostics) ?? [];
                    if (!hasEffects)
                        effects = parsedEffects;
                    hasEffects = true;
                    break;
                case TaleClauseKind.Divert:
                    divert = BindDivert(syntax, clause);
                    break;
                case TaleClauseKind.SkillCheck:
                    // The spec's clauses are the last on the line.
                    var spec = BindCheckSpec(syntax, clauses[index..]);
                    var (success, failure) = BindOutcomes();
                    if (spec is not null && success is not null && failure is not null)
                        check = new CheckBranch(spec, success, failure);
                    index = clauses.Length;
                    break;
            }
        }

        if (syntax.IsInvalid || malformedTextless)
        {
            NoteDroppedChoice(scene);
            return;
        }

        if (label is not null && ContainsDecorativeMedia(label) && IsCertainlyNameless(label))
        {
            _diagnostics.Error(CompilerDiagnosticCodes.MissingAccessibleChoiceLabel,
                "A choice label with only decorative images has no accessible name. Add label text or use a descriptive image.",
                labelLocation ?? start);
        }

        scene.CurrentSteps.Add(new ChoiceStep(StepId(start), SpanFrom(start), new ChoiceDefinition
        {
            Key = $"c{scene.ChoiceCount++}",
            Label = label,
            Requires = requires,
            Shown = shown,
            Effects = effects,
            Check = check,
            Divert = divert,
        }));
    }

    private static bool ContainsDecorativeMedia(IReadOnlyList<TemplateInline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case MediaTemplate { Accessibility: MediaAccessibility.Decorative }:
                    return true;
                case StyledTemplate styled when ContainsDecorativeMedia(styled.Children):
                    return true;
                case ConditionalInlineTemplate conditional
                    when ContainsDecorativeMedia(conditional.Then) || ContainsDecorativeMedia(conditional.Else):
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether every branch holds only whitespace, line breaks and decorative images. Text, substitutions
    /// and descriptive images are trusted to name the choice; what they render is left to playtesting.
    /// </summary>
    private static bool IsCertainlyNameless(IReadOnlyList<TemplateInline> inlines) =>
        inlines.All(inline => inline switch
        {
            TextTemplate text => string.IsNullOrWhiteSpace(text.Text),
            LineBreakTemplate => true,
            MediaTemplate media => media.Accessibility == MediaAccessibility.Decorative,
            StyledTemplate styled => IsCertainlyNameless(styled.Children),
            ConditionalInlineTemplate conditional =>
                IsCertainlyNameless(conditional.Then) && IsCertainlyNameless(conditional.Else),
            _ => false,
        });

    private DivertTarget? BindDivert(TaleLineSyntax syntax, TaleClause clause)
    {
        _symbols.Analysis?.Tokens.Add(new(SceneVocabulary.Divert,
            AnalysisCollector.Range(At(clause.Start), SceneVocabulary.Divert))
            { Kind = AuthoringTokenKind.Divert });
        var id = Value(syntax, clause);
        var location = At(clause.ValueStart);
        _symbols.Analysis?.Reference(id, location, AuthoringSymbolKind.Scene, SceneVocabulary.End);
        return clause.IsInvalid ? null : BindDivertTarget(id, location);
    }

    // ---- skill checks ----------------------------------------------------------------------------------------------

    private void ParseBlockCheck(TaleLineSyntax syntax)
    {
        // Syntax reported a fence that does not open a skill check.
        if (syntax.IsInvalid)
            return;
        var location = At(syntax.Indent);
        var check = BindCheckSpec(syntax, Clauses(syntax));
        var (success, failure) = BindOutcomes();
        if (check is not null && success is not null && failure is not null)
        {
            _scene!.CurrentSteps.Add(new CheckStep(StepId(location), SpanFrom(location),
                new CheckBranch(check, success, failure)));
        }
    }

    /// <summary>
    /// Binds a <c>[actor] stat dc expr</c> spec: a <see cref="TaleClauseKind.SkillCheck"/> clause followed by its words
    /// and difficulty. The actor, stat and difficulty are checked independently so each mistake is reported.
    /// </summary>
    private SkillCheck? BindCheckSpec(TaleLineSyntax syntax, ReadOnlySpan<TaleClause> clauses)
    {
        var spec = clauses[0];
        var column = spec.ValueStart;
        var words = new List<string>(2);
        var wordClauses = new List<TaleClause>(2);
        TaleClause? difficultyClause = null;
        foreach (var clause in clauses[1..])
        {
            if (clause.Kind == TaleClauseKind.CheckWord)
            {
                words.Add(Value(syntax, clause));
                wordClauses.Add(clause);
            }
            else if (clause.Kind == TaleClauseKind.Difficulty)
                difficultyClause = clause;
        }

        var difficultyText = difficultyClause is { } found ? Value(syntax, found) : null;
        if (_symbols.Analysis is { } analysis)
        {
            analysis.Tokens.Add(new(SceneVocabulary.SkillCheck,
                AnalysisCollector.Range(At(spec.Start), SceneVocabulary.SkillCheck))
                { Kind = AuthoringTokenKind.SkillCheck });
            if (words.Count == 0)
            {
                analysis.Reference("", At(column), AuthoringSymbolKind.Character);
                analysis.Reference("", At(column), AuthoringSymbolKind.Stat);
            }

            for (var index = 0; index < words.Count; index++)
            {
                var location = At(wordClauses[index].ValueStart);
                switch (spec.DifficultyWord)
                {
                    case 1:
                        analysis.Reference(words[index], location, AuthoringSymbolKind.Stat);
                        break;
                    case 2:
                        analysis.Reference(words[index], location,
                            index == 0 ? AuthoringSymbolKind.Character : AuthoringSymbolKind.Stat);
                        break;
                    default:
                        analysis.Reference(words[index], location, AuthoringSymbolKind.Stat);
                        if (index == 0)
                            analysis.Reference(words[index], location, AuthoringSymbolKind.Character);
                        break;
                }
            }

            // "skill-check guard " with no dc yet: the word is a known actor, so offer stats after it.
            if (spec.DifficultyWord == 0 && difficultyClause is null && words.Count == 1)
            {
                var end = wordClauses[0].ValueStart + wordClauses[0].ValueLength;
                if (end < _lineLength && char.IsWhiteSpace(_text[_lineStart + end]) &&
                    _symbols.World.Characters.ContainsKey(new EntityId(words[0])))
                    analysis.Reference("", At(end + 1), AuthoringSymbolKind.Stat);
            }

            if (difficultyClause is { } difficulty)
                analysis.Expression(ExpressionContextKind.Number, difficultyText!, At(difficulty.ValueStart),
                    cacheForReuse: difficulty.ValueLength > 0);
        }

        if (_symbols.World.Ruleset.SkillCheckRule is null)
        {
            _diagnostics.Error(CompilerDiagnosticCodes.SkillChecksUnsupported,
                $"The '{_symbols.World.Ruleset.Id}' ruleset has no {SceneVocabulary.SkillCheck} rule, " +
                $"so {SceneVocabulary.SkillCheck} cannot be used.", At(column));
            return null;
        }

        // Syntax reported a spec that is not [actor] stat dc expr.
        if (spec.IsInvalid)
            return null;

        var bound = true;
        var actor = words.Count == 2 ? new EntityId(words[0]) : EntityId.Player;
        if (!_symbols.World.Characters.ContainsKey(actor))
        {
            _diagnostics.Error(CompilerDiagnosticCodes.UnknownNameOrProperty, $"Unknown character '{actor}'.", At(column));
            bound = false;
        }

        var skill = new StatId(words[^1]);
        if (!_symbols.World.Ruleset.HasStat(skill))
        {
            _diagnostics.Error(CompilerDiagnosticCodes.UnknownNameOrProperty,
                $"Unknown stat '{skill}' (ruleset '{_symbols.World.Ruleset.Id}')." +
                _symbols.StatGuidance?.ForMissingStat(skill), At(column));
            bound = false;
        }

        var dc = ExpressionParser.ParseNumber(difficultyText!, At(difficultyClause!.Value.ValueStart), _symbols,
            _diagnostics);
        return bound && dc is not null ? new SkillCheck(actor, skill, dc) : null;
    }

    /// <summary>
    /// Binds the outcome lines syntax attached to the check just bound, moving past them and the closing fence.
    /// </summary>
    private (CheckOutcome? Success, CheckOutcome? Failure) BindOutcomes()
    {
        CheckOutcome? success = null;
        CheckOutcome? failure = null;
        while (TryPeekLine(out var next) && next.IsAttached)
        {
            MoveTo(_lineCursor + 1);
            CountLine();
            if (next.Kind == TaleLineKind.SuccessOutcome)
                success = BindOutcome(next);
            else if (next.Kind == TaleLineKind.FailureOutcome)
                failure = BindOutcome(next);
        }

        return (success, failure);
    }

    private CheckOutcome? BindOutcome(TaleLineSyntax syntax)
    {
        IReadOnlyList<Effect> effects = [];
        DivertTarget? divert = null;
        foreach (var clause in Clauses(syntax))
        {
            switch (clause.Kind)
            {
                case TaleClauseKind.Effects:
                    effects = ExpressionParser.ParseEffects(Value(syntax, clause), At(clause.ValueStart), _symbols,
                        _diagnostics) ?? [];
                    break;
                case TaleClauseKind.UnclosedEffects:
                    _symbols.Analysis?.Expression(ExpressionContextKind.Effects, Value(syntax, clause),
                        At(clause.ValueStart));
                    break;
                case TaleClauseKind.Divert:
                    divert = BindDivert(syntax, clause);
                    break;
            }
        }

        return syntax.IsInvalid ? null : new CheckOutcome(effects, divert);
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

    private (Expr? Requirement, ShownDefinition? Shown) BindRequirement(string value, SourceLocation location,
        string diagnosticCode, bool highlightUnexpectedTokens = false, SourceLocation? authoringLocation = null)
    {
        var parsed = RequirementClauseSyntax.Parse(value, _cancellationToken);
        var sourceLocation = authoringLocation ?? location;
        _symbols.Analysis?.RequirementSuffix(value, sourceLocation, parsed);
        foreach (var error in parsed.Errors)
        {
            var message = error.Kind switch
            {
                RequirementClauseErrorKind.ExpectedShown =>
                    $"Expected '{SceneVocabulary.Shown}' after the requirement comma.",
                RequirementClauseErrorKind.DuplicateShown =>
                    $"Duplicate '{SceneVocabulary.Shown}' clause. A requirement accepts it only once.",
                RequirementClauseErrorKind.MissingTextQuote =>
                    $"The optional text after '{SceneVocabulary.Shown}' must be a closed double-quoted reader-text string.",
                RequirementClauseErrorKind.UnexpectedAfterShown =>
                    $"After '{SceneVocabulary.Shown}', use only an optional double-quoted reader-text string.",
                _ => throw new ArgumentOutOfRangeException(nameof(error)),
            };
            _diagnostics.Error(diagnosticCode, message, sourceLocation.Offset(error.Offset));
        }

        var requirementText = value[..parsed.ExpressionLength];
        var requirement = ExpressionParser.ParseCondition(requirementText, location, _symbols, _diagnostics,
            authoringLocation ?? location, highlightUnexpectedTokens: highlightUnexpectedTokens);
        if (!parsed.HasShown)
            return (requirement, null);

        IReadOnlyList<TemplateInline>? text = null;
        if (parsed.Text is { } slot)
        {
            var quoted = QuotedReaderTextSyntax.Read(SourceText.Single(value, sourceLocation), slot.QuoteOffset,
                slot.End, _cancellationToken);
            if (quoted.Authored.Length == 0)
                _diagnostics.Warning(diagnosticCode,
                    $"An empty '{SceneVocabulary.Shown}' text will display no text while the requirement is false.",
                    sourceLocation.Offset(slot.QuoteOffset));
            text = InlineParser.Parse(quoted.ToSource(), _symbols, _diagnostics,
                cancellationToken: _cancellationToken);
        }

        return (requirement, new ShownDefinition(text));
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
        public string? Title { get; set; }
        public IReadOnlyList<TemplateInline>? ChoiceLabel { get; set; }
        public bool ContinuesPrevious { get; set; }
        public bool ClearsPrevious { get; set; }
        public Expr? Requires { get; set; }
        public ShownDefinition? Shown { get; set; }
        public HashSet<string> Tags { get; } = [];
        public int Priority { get; set; }
        public bool HasExplicitPriority { get; set; }
        public bool Once { get; set; }
        public Talewright.Rules.Storylets.StoryletTags? Hub { get; set; }
        public int? HubLimit { get; set; }
        public List<SceneStep> Steps { get; } = [];
        public int ChoiceCount { get; set; }
        public bool DroppedMalformedChoice { get; set; }
        public Stack<Frame> Frames { get; } = new();
        public SourceText Paragraph { get; set; } = new();

        public List<SceneStep> CurrentSteps =>
            Frames.Count == 0 ? Steps : Frames.Peek().InElse ? Frames.Peek().Else : Frames.Peek().Then;

        public SceneDefinition Build() => new()
        {
            Id = Id,
            Title = Title,
            ChoiceLabel = ChoiceLabel,
            ContinuesPrevious = ContinuesPrevious,
            ClearsPrevious = ClearsPrevious,
            Requires = Requires,
            Shown = Shown,
            Tags = Tags,
            Priority = Priority,
            Once = Once,
            Hub = Hub,
            HubLimit = HubLimit,
            Steps = Steps,
        };
    }
}
