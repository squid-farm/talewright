using System.Diagnostics.CodeAnalysis;
using Talewright.Core.Identity;
using Talewright.Core.Randomness;
using Talewright.Core.State;
using Talewright.Core.World;
using Talewright.Narrative.Templates;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Talemark.Parsing;

internal sealed class ExpressionBinder
{
    private readonly TokenBuffer _tokens;
    private readonly Func<int, SourceLocation> _locate;
    private readonly StorySymbols _symbols;
    private readonly DiagnosticBag _diagnostics;
    private readonly Func<EntityId, StatId, SkillCheckIdentity>? _createSkillCheckIdentity;
    private readonly SceneId? _sceneOwner;

    private ExpressionBinder(TokenBuffer tokens, Func<int, SourceLocation> locate, StorySymbols symbols,
        DiagnosticBag diagnostics, Func<EntityId, StatId, SkillCheckIdentity>? createSkillCheckIdentity = null,
        SceneId? sceneOwner = null)
    {
        _tokens = tokens;
        _locate = locate;
        _symbols = symbols;
        _diagnostics = diagnostics;
        _createSkillCheckIdentity = createSkillCheckIdentity;
        _sceneOwner = sceneOwner;
    }

    private sealed class BindingException : Exception;

    internal static Expr? Bind(ExpressionSyntaxTree syntax, StorySymbols symbols, DiagnosticBag diagnostics,
        Func<int, SourceLocation> locate, ExprType? expectedType = null,
        Func<EntityId, StatId, SkillCheckIdentity>? createSkillCheckIdentity = null, SceneId? sceneOwner = null)
    {
        if (syntax.HasErrors) return null;
        var binder = new ExpressionBinder(syntax.Tokens, locate, symbols, diagnostics, createSkillCheckIdentity, sceneOwner);
        try
        {
            var value = binder.Bind(syntax.Root);
            if (expectedType is { } expected && value.Type != expected)
            {
                if (expected == ExprType.Number) binder.RejectItemNumber(value, syntax.Root.Span);
                if (expected == ExprType.Number && value is FactionNameRef faction)
                {
                    binder.Fail(CompilerDiagnosticCodes.TypeMismatch,
                        FactionValueHint(faction, ExpressionVocabulary.Standing), TypeMismatchSpan(syntax.Root));
                }

                binder.Fail(CompilerDiagnosticCodes.TypeMismatch,
                    $"Expected a {Describe(expected)} expression but found a {Describe(value.Type)}.",
                    TypeMismatchSpan(syntax.Root));
            }

            return value;
        }
        catch (BindingException)
        {
            return null;
        }
    }

    internal static TemplateInline? Bind(SubstitutionSyntaxTree syntax, StorySymbols symbols,
        DiagnosticBag diagnostics, Func<int, SourceLocation> locate, bool allowFormattedOutput = true,
        SceneId? sceneOwner = null)
    {
        if (syntax.HasErrors) return null;
        var binder = new ExpressionBinder(syntax.Tokens, locate, symbols, diagnostics, sceneOwner: sceneOwner);
        try
        {
            var value = binder.Bind(syntax.Value);
            var modifiers = new List<ITextModifier>();
            var valueKind = TextModifierValueKind.Text;
            var style = TextStyle.None;
            foreach (var call in syntax.Modifiers)
            {
                ITextModifier modifier;
                if (call.Name == OneManyTextModifier.ModifierName)
                {
                    if (value.Type != ExprType.Number &&
                        value is not (PronounRef or ItemNameRef { Item: FixedItemOperand }) ||
                        call.Argument is null || call.PluralArgument is null)
                    {
                        binder.Fail(CompilerDiagnosticCodes.InvalidModifier,
                            "Use '#one-many(\"singular\", \"plural\")': two quoted strings, singular first, on numbers or on pronoun or item-name references only.",
                            call.Offset);
                    }

                    modifier = new OneManyTextModifier(call.Argument, call.PluralArgument);
                }
                else
                {
                    if (call.Argument is not null)
                    {
                        binder.Fail(CompilerDiagnosticCodes.InvalidModifier,
                            $"Modifier '#{call.Name}' does not take an argument.", call.Offset);
                    }

                    if (!symbols.Modifiers.TryGet(call.Name, out modifier!))
                    {
                        var knownModifiers = symbols.Modifiers.Names.Select(name =>
                            ExpressionVocabulary.ModifierMarker + name);
                        if (syntax.Tokens.TalemarkVersion == 2 && value.Type == ExprType.Number)
                            knownModifiers = knownModifiers.Concat(ExpressionVocabulary.NumericModifierSpellings);
                        binder.Fail(CompilerDiagnosticCodes.InvalidModifier,
                            $"Unknown modifier '#{call.Name}'. Known: {string.Join(", ", knownModifiers)}.",
                            call.Offset);
                    }
                }

                if (!modifier.AcceptsInput(valueKind))
                {
                    binder.Fail(CompilerDiagnosticCodes.InvalidModifier,
                        $"Modifier '#{call.Name}' cannot follow a modifier that produces formatted text.",
                        call.Offset);
                }

                if (!allowFormattedOutput && modifier.OutputKind == TextModifierValueKind.FormattedText)
                {
                    binder.Fail(CompilerDiagnosticCodes.InvalidModifier,
                        $"Formatting modifier '#{call.Name}' is not allowed in plain-text fields such as image alt text.",
                        call.Offset);
                }

                valueKind = modifier.OutputKind;
                if (modifier.OutputKind == TextModifierValueKind.FormattedText)
                {
                    if (modifier.OutputStyle == TextStyle.None)
                    {
                        binder.Fail(CompilerDiagnosticCodes.InvalidModifier,
                            $"Modifier '#{call.Name}' produces formatted text without a supported style.",
                            call.Offset);
                    }

                    style |= modifier.OutputStyle;
                }
                else
                {
                    modifiers.Add(modifier);
                }
            }

            var isPlural = value switch
            {
                NameRef name => symbols.World.Characters[name.Character].NameAgreement == GrammaticalNumber.Plural,
                PronounRef pronoun => symbols.World.Characters[pronoun.Character].Pronouns.Agreement == GrammaticalNumber.Plural,
                ItemNameRef item => item.Item is FixedItemOperand fixedItem
                    && symbols.World.Items[fixedItem.Item].NameAgreement == GrammaticalNumber.Plural,
                _ => false,
            };
            var substitution = new SubstitutionTemplate(value, modifiers, isPlural);
            return style == TextStyle.None ? substitution : new StyledTemplate(style, [substitution]);
        }
        catch (BindingException)
        {
            return null;
        }
    }

    internal static List<Effect>? Bind(EffectsSyntaxTree syntax, StorySymbols symbols, DiagnosticBag diagnostics,
        Func<int, SourceLocation> locate, Func<EntityId, StatId, SkillCheckIdentity>? createSkillCheckIdentity = null,
        SceneId? sceneOwner = null)
    {
        if (syntax.HasErrors) return null;
        var binder = new ExpressionBinder(syntax.Tokens, locate, symbols, diagnostics, createSkillCheckIdentity, sceneOwner);
        try
        {
            var effects = new List<Effect>(syntax.Effects.Length);
            foreach (var effect in syntax.Effects)
            {
                effects.Add(binder.Bind(effect));
            }

            return effects;
        }
        catch (BindingException)
        {
            return null;
        }
    }

    private Expr Bind(ExpressionNode node) => node switch
    {
        ErrorExpressionNode => Fail<Expr>(CompilerDiagnosticCodes.InvalidExpression, "Expression is incomplete.", node.Span.Start),
        NumberExpressionNode number => new NumberLiteral(number.Value),
        TextExpressionNode text => new TextLiteral(text.Value),
        BooleanExpressionNode boolean => new BoolLiteral(boolean.Value),
        NameExpressionNode name => BindName(name),
        NamedPredicateExpressionNode predicate => BindPredicate(predicate),
        PlayerVisitedExpressionNode visited => BindPlayerVisited(visited),
        ParenthesizedExpressionNode parenthesized => Bind(parenthesized.Inner),
        ReferenceExpressionNode reference => BindReference(reference),
        InventoryItemPropertyExpressionNode property => BindInventoryItemProperty(property),
        InventoryItemNameExpressionNode itemName => BindInventoryItemName(itemName),
        SkillCheckExpressionNode skillCheck => BindSkillCheck(skillCheck),
        HasItemExpressionNode hasItem => new HasItem(Character(hasItem.Character), ItemValue(hasItem.Item)),
        UnaryExpressionNode unary => BindUnary(unary),
        BinaryExpressionNode binary => BindBinary(binary),
        _ => throw new InvalidOperationException($"Unknown expression syntax node '{node.GetType().Name}'."),
    };

    private Expr BindSkillCheck(SkillCheckExpressionNode syntax)
    {
        if (_createSkillCheckIdentity is null)
            return Fail<Expr>(CompilerDiagnosticCodes.InvalidExpression,
                $"'{SceneVocabulary.SkillCheck}' is not allowed in this expression.", syntax.Span.Start);

        if (_symbols.World.Ruleset.SkillCheckRule is null)
            Fail(CompilerDiagnosticCodes.SkillChecksUnsupported,
                $"The '{_symbols.World.Ruleset.Id}' ruleset has no {SceneVocabulary.SkillCheck} rule, " +
                $"so {SceneVocabulary.SkillCheck} cannot be used.", syntax.Span.Start);

        var actor = syntax.Actor is { } actorSpan ? new EntityId(Text(actorSpan)) : EntityId.Player;
        if (!_symbols.World.Characters.ContainsKey(actor))
            Fail(CompilerDiagnosticCodes.UnknownNameOrProperty, $"Unknown character '{actor}'.",
                syntax.Actor?.Start ?? syntax.Span.Start);

        var skill = new StatId(Text(syntax.Skill));
        if (!_symbols.World.Ruleset.HasStat(skill))
            Fail(CompilerDiagnosticCodes.UnknownNameOrProperty,
                $"Unknown stat '{skill}' (ruleset '{_symbols.World.Ruleset.Id}')." +
                _symbols.StatGuidance?.ForMissingStat(skill), syntax.Skill.Start);

        var difficulty = syntax.Difficulty is { } difficultySyntax ? Bind(difficultySyntax) : null;
        if (difficulty is { Type: not ExprType.Number })
        {
            RejectItemNumber(difficulty, syntax.Difficulty!.Span);
            Fail(CompilerDiagnosticCodes.TypeMismatch,
                $"'{SceneVocabulary.Difficulty}' needs a number.", TypeMismatchSpan(syntax.Difficulty!));
        }

        var identity = _createSkillCheckIdentity(actor, skill);
        return new SkillCheckExpr(actor, skill, identity, difficulty, syntax.IsHidden);
    }

    private Expr BindName(NameExpressionNode name)
    {
        var value = Text(name.Name);
        if (_symbols.World.Ruleset.FindTier(value) is { } tier)
        {
            return new TierLiteral(tier);
        }

        return Fail<Expr>(CompilerDiagnosticCodes.UnknownNameOrProperty,
            $"Unknown name '{value}'. References look like 'player:dex', 'faction:{{id}}', 'flag:{{id}}' or 'var:{{id}}'.",
            name.Name.Start);
    }

    private Expr BindPredicate(NamedPredicateExpressionNode predicate)
    {
        var name = Text(predicate.Name);
        if (!_symbols.Predicates.Contains(name))
        {
            Fail(CompilerDiagnosticCodes.UnknownPredicate,
                $"Named predicate '@{name}' is not registered. Named predicates are provided by C# story projects.",
                predicate.Span.Start);
        }

        return new NamedPredicate(name);
    }

    private Expr BindPlayerVisited(PlayerVisitedExpressionNode visited)
    {
        var character = Character(visited.Character);
        if (character != EntityId.Player)
        {
            Fail(CompilerDiagnosticCodes.UnknownNameOrProperty,
                "Only player visits are tracked; use 'player visited <scene>'.",
                visited.Character.Start);
        }

        var name = Text(visited.Scene);
        var scene = new SceneId(name);
        if (!_symbols.Scenes.Contains(scene))
        {
            Fail(CompilerDiagnosticCodes.UnknownScene, $"Unknown scene '{name}'.", visited.Scene.Start);
        }

        return new Visited(scene);
    }

    private Expr BindReference(ReferenceExpressionNode reference)
    {
        var ns = Text(reference.Namespace);
        var path = Text(reference.Name);
        var member = reference.Member is { } memberSpan ? Text(memberSpan) : null;
        var subMember = reference.SubMember is { } subMemberSpan ? Text(subMemberSpan) : null;

        if (path == ExpressionVocabulary.Player && member == ExpressionVocabulary.Visits)
        {
            if (ns == ExpressionVocabulary.Scene)
                return new SceneVisitCountRef(_sceneOwner, true);

            var scene = new SceneId(ns);
            if (_symbols.Scenes.Contains(scene))
                return new SceneVisitCountRef(scene);

            if (_symbols.World.Characters.ContainsKey(new EntityId(ns)))
            {
                Fail(CompilerDiagnosticCodes.UnknownNameOrProperty,
                    "Only player visits are tracked; use '<scene>:player:visits'.",
                    reference.Namespace.Start);
            }

            Fail(CompilerDiagnosticCodes.UnknownScene, $"Unknown scene '{ns}'.", reference.Namespace.Start);
        }

        if (_symbols.World.Characters.ContainsKey(new EntityId(ns)) &&
            (path == ExpressionVocabulary.Visits || member == ExpressionVocabulary.Visits))
        {
            var hint = ns == ExpressionVocabulary.Player
                ? "Use 'scene:player:visits' or '<scene>:player:visits' to read player visits."
                : "Only player visits are tracked; use '<scene>:player:visits'.";
            Fail(CompilerDiagnosticCodes.UnknownNameOrProperty, hint, reference.Name.Start);
        }

        if (reference.Kind == ReferenceNamespace.Scene ||
            reference.Kind == ReferenceNamespace.Character && _symbols.Scenes.Contains(new SceneId(ns)))
        {
            Fail(CompilerDiagnosticCodes.UnknownNameOrProperty,
                $"Unknown scene property '{path}:{member}'. Use '{ExpressionVocabulary.Player}:{ExpressionVocabulary.Visits}'.",
                reference.Name.Start);
        }

        switch (reference.Kind)
        {
            case ReferenceNamespace.Faction:
            {
                var faction = new FactionId(path);
                if (!_symbols.World.Factions.ContainsKey(faction))
                    Fail(CompilerDiagnosticCodes.UnknownNameOrProperty, $"Unknown faction '{path}'.", reference.Name.Start);

                if (member is null) return new FactionNameRef(faction);
                return member switch
                {
                    ExpressionVocabulary.Standing => new StandingRef(faction),
                    ExpressionVocabulary.Tier => new StandingTierRef(faction),
                    ExpressionVocabulary.Name => new FactionNameRef(faction),
                    _ => Fail<Expr>(CompilerDiagnosticCodes.UnknownNameOrProperty,
                        $"Unknown faction property '{member}'. Use '{FactionReference(path, ExpressionVocabulary.Name)}', " +
                        $"'{FactionReference(path, ExpressionVocabulary.Standing)}' or " +
                        $"'{FactionReference(path, ExpressionVocabulary.Tier)}'.",
                        reference.Member!.Value.Start),
                };
            }

            case ReferenceNamespace.Item:
            {
                var item = Item(reference.Name);
                if (member is null || member == ExpressionVocabulary.Name)
                {
                    return new ItemNameRef(item);
                }

                var definition = _symbols.World.Items[item];
                if (definition.CustomProperties.TryGetValue(member, out var value))
                    return new ItemPropertyRef(item, member, value);

                Fail(CompilerDiagnosticCodes.UnknownNameOrProperty,
                    $"Unknown item property '{member}' on '{path}'.",
                    reference.Member!.Value);
                return default!;
            }
            case ReferenceNamespace.Flag:
            {
                if (member is not null)
                {
                    Fail(CompilerDiagnosticCodes.UnknownNameOrProperty, $"Flag references do not have properties; use 'flag:{path}'.",
                        reference.Member!.Value.Start);
                }

                var flag = new FlagId(path);
                if (!_symbols.Flags.Contains(flag))
                {
                    Fail(CompilerDiagnosticCodes.UnsetFlagOrUnassignedVariable, $"Flag '{path}' is never set by any effect and is not declared in story.talewright.yaml.",
                        reference.Name.Start);
                }

                return new FlagRef(flag);
            }

            case ReferenceNamespace.Variable:
            {
                var variable = new VarId(path);
                if (_symbols.UnresolvedVariables.Contains(variable))
                {
                    Fail(CompilerDiagnosticCodes.UnresolvedVariableType,
                        $"Cannot infer the type of variable '{path}'. Declare an initial value under 'vars' in story.talewright.yaml.",
                        reference.Name.Start);
                }

                if (!_symbols.Variables.Contains(variable))
                {
                    Fail(CompilerDiagnosticCodes.UnsetFlagOrUnassignedVariable, $"Variable '{path}' is never assigned by any effect and is not declared in story.talewright.yaml.",
                        reference.Name.Start);
                }

                if (_symbols.ItemVariables.Contains(variable))
                {
                    var operand = new ResolvedItemOperand(new TextVarRef(variable));
                    if (member is null || member == ExpressionVocabulary.Name)
                        return new ItemNameRef(operand);
                    var kinds = _symbols.World.Items.Values
                        .Where(item => item.CustomProperties.ContainsKey(member))
                        .Select(item => ItemPropertySnapshot.KindOf(item.CustomProperties[member])).Distinct().ToArray();
                    if (kinds.Length == 0)
                        Fail(CompilerDiagnosticCodes.UnknownNameOrProperty,
                            $"Unknown item property '{member}'.", reference.Member!.Value);
                    if (kinds.Length != 1)
                        Fail(CompilerDiagnosticCodes.TypeMismatch,
                            $"Item property '{member}' has different types on different items.", reference.Member!.Value);
                    return new ItemReferencePropertyRef(operand, member, kinds[0]);
                }

                if (member is not null)
                    Fail(CompilerDiagnosticCodes.UnknownNameOrProperty,
                        $"Variable references do not have properties; use 'var:{path}'.",
                        reference.Member!.Value);
                return _symbols.World.TextVariables.ContainsKey(variable)
                    ? new TextVarRef(variable)
                    : new VarRef(variable);
            }
        }

        var character = Character(reference.Namespace);
        if (member is not null)
        {
            if (path != ExpressionVocabulary.Pronoun)
            {
                Fail(CompilerDiagnosticCodes.UnknownNameOrProperty, $"Unknown character property '{path}:{member}'.", reference.Member!.Value.Start);
            }

            return new PronounRef(character, ParsePronounForm(member, subMember, reference));
        }

        if (ExpressionVocabulary.TryGetNamePart(path, out var part)) return new NameRef(character, part);
        return path == ExpressionVocabulary.Pronoun
            ? new PronounRef(character, PronounForm.Subject)
            : ResolveStat(character, reference.Name, reference.Namespace);
    }

    private Expr BindInventoryItemProperty(InventoryItemPropertyExpressionNode reference)
    {
        var character = InventoryCharacter(reference.Character);
        var operand = ItemValue(reference.Item, inventory: true);
        var property = Text(reference.Property);
        if (property == ExpressionVocabulary.Count)
            return new InventoryCountRef(character, operand);

        if (operand is not FixedItemOperand fixedItem)
            return Fail<Expr>(CompilerDiagnosticCodes.UnsupportedHeldItemPropertyAccess,
                $"Read an item variable's property directly: '{Text(reference.Item)}:{property}'.",
                reference.Property.Start);
        var item = fixedItem.Item;

        var definition = _symbols.World.Items[item];
        if (!definition.CustomProperties.TryGetValue(property, out var declaredValue))
        {
            Fail(CompilerDiagnosticCodes.UnknownNameOrProperty,
                $"Unknown inventory property '{property}' on '{Text(reference.Item)}'.",
                reference.Property);
        }

        RequireHeldItemProperty(definition, property, reference.Property);
        return new InventoryItemPropertyRef(character, item, property, declaredValue);
    }

    private Expr BindInventoryItemName(InventoryItemNameExpressionNode reference)
    {
        _ = InventoryCharacter(reference.Character);
        return new ItemNameRef(ItemValue(reference.Item, inventory: true));
    }

    private PronounForm ParsePronounForm(string property, string? subMember, ReferenceExpressionNode reference)
    {
        if (property == ExpressionVocabulary.PossessivePronoun)
        {
            return ExpressionVocabulary.TryGetPossessiveForm(subMember, out var possessive)
                ? possessive
                : Fail<PronounForm>(CompilerDiagnosticCodes.UnknownNameOrProperty,
                    $"Unknown possessive pronoun property '{subMember}'. Use '{ExpressionVocabulary.Determiner}' or '{ExpressionVocabulary.Independent}'.",
                    reference.SubMember!.Value.Start);
        }

        return ExpressionVocabulary.TryGetPronounForm(property, out var form)
            ? form
            : Fail<PronounForm>(CompilerDiagnosticCodes.UnknownNameOrProperty,
                $"Unknown pronoun property '{property}'. Use {string.Join(", ", ExpressionVocabulary.PronounProperties)}.",
                reference.Member!.Value.Start);
    }

    private Expr ResolveStat(EntityId character, ExpressionSpan path, ExpressionSpan ns)
    {
        var stat = new StatId(Text(path));
        return _symbols.World.Ruleset.HasStat(stat)
            ? new StatRef(character, stat)
            : Fail<Expr>(CompilerDiagnosticCodes.UnknownNameOrProperty,
                $"Unknown property '{Text(path)}' on '{Text(ns)}'." + _symbols.StatGuidance?.ForMissingStat(stat), path.Start);
    }

    private Expr BindUnary(UnaryExpressionNode unary)
    {
        var operand = Bind(unary.Operand);
        switch (unary.Operator)
        {
            case UnaryOp.Not:
                if (operand.Type != ExprType.Bool)
                {
                    Fail(CompilerDiagnosticCodes.TypeMismatch, $"'{ExpressionVocabulary.Not}' needs a true/false condition.",
                        TypeMismatchSpan(unary.Operand, unary.OperatorSpan));
                }

                return new UnaryExpr(UnaryOp.Not, operand);
            case UnaryOp.Negate:
                if (operand.Type != ExprType.Number)
                {
                    RejectItemNumber(operand, unary.Operand.Span);
                    if (operand is FactionNameRef faction)
                    {
                        Fail(CompilerDiagnosticCodes.TypeMismatch,
                            FactionValueHint(faction, ExpressionVocabulary.Standing),
                            TypeMismatchSpan(unary.Operand, unary.OperatorSpan));
                    }

                    Fail(CompilerDiagnosticCodes.TypeMismatch, $"'{ExpressionVocabulary.Minus}' needs a number.",
                        TypeMismatchSpan(unary.Operand, unary.OperatorSpan));
                }

                if (operand is NumberLiteral number)
                {
                    if (_symbols.World.TalemarkVersion == 1)
                    {
                        var integer = number.Value.ToInt32RoundedAwayFromZero();
                        return new NumberLiteral(integer == int.MinValue ? int.MaxValue : -integer);
                    }

                    return new NumberLiteral(-number.Value);
                }

                return new UnaryExpr(UnaryOp.Negate, operand);
            case UnaryOp.Round or UnaryOp.RoundUp or UnaryOp.RoundDown:
                var modifier = ExpressionVocabulary.NumericModifierSpelling(unary.Operator);
                if (_symbols.World.TalemarkVersion == 1)
                {
                    Fail(CompilerDiagnosticCodes.InvalidModifier,
                        $"Numeric modifier '{modifier}' is available only in Talemark 2. Set 'game.talemark: 2' to use it.",
                        unary.OperatorSpan);
                }

                if (operand.Type != ExprType.Number)
                {
                    RejectItemNumber(operand, unary.Operand.Span);
                    Fail(CompilerDiagnosticCodes.InvalidModifier,
                        $"Numeric modifier '{modifier}' requires a number-valued expression, but found {Describe(operand.Type)}.",
                        TypeMismatchSpan(unary.Operand, unary.OperatorSpan));
                }

                return new UnaryExpr(unary.Operator, operand);
            default:
                throw new InvalidOperationException($"Unsupported unary operator '{unary.Operator}'.");
        }
    }

    private Expr BindBinary(BinaryExpressionNode binary)
    {
        var chain = new Stack<BinaryExpressionNode>();
        ExpressionNode first = binary;
        while (first is BinaryExpressionNode current)
        {
            chain.Push(current);
            first = current.Left;
        }

        var left = Bind(first);
        while (chain.TryPop(out var current))
        {
            if (left is ItemNameRef { Item: ResolvedItemOperand operand } &&
                current.Operator is BinaryOp.Equal or BinaryOp.NotEqual)
            {
                var item = current.Right switch
                {
                    NameExpressionNode name => name.Name,
                    BooleanExpressionNode boolean => boolean.Span,
                    ReferenceExpressionNode { Kind: ReferenceNamespace.Item, Member: null } reference => reference.Name,
                    _ => Fail<ExpressionSpan>(CompilerDiagnosticCodes.TypeMismatch,
                        "Compare an item variable with a bare item id or item reference, for example 'var:tool == hammer'.",
                        current.Right.Span.Start),
                };
                left = new ItemComparison(operand, current.Operator, Item(item));
            }
            else
                left = BindBinaryOperation(current, left, Bind(current.Right));
        }

        return left;
    }

    private Expr BindBinaryOperation(BinaryExpressionNode binary, Expr left, Expr right)
    {
        var op = binary.Operator;
        switch (op)
        {
            case BinaryOp.Or or BinaryOp.And:
                if (left.Type != ExprType.Bool || right.Type != ExprType.Bool)
                {
                    Fail(CompilerDiagnosticCodes.TypeMismatch, $"'{ExpressionVocabulary.Spelling(op)}' needs true/false conditions on both sides.",
                        TypeMismatchSpan(binary, binary.OperatorSpan));
                }

                break;
            case BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply or BinaryOp.Divide:
                if (left.Type != ExprType.Number || right.Type != ExprType.Number)
                {
                    RejectItemNumber(left, binary.Left.Span);
                    RejectItemNumber(right, binary.Right.Span);
                    if (left is FactionNameRef leftFaction)
                    {
                        Fail(CompilerDiagnosticCodes.TypeMismatch,
                            FactionValueHint(leftFaction, ExpressionVocabulary.Standing),
                            TypeMismatchSpan(binary, binary.OperatorSpan));
                    }

                    if (right is FactionNameRef rightFaction)
                    {
                        Fail(CompilerDiagnosticCodes.TypeMismatch,
                            FactionValueHint(rightFaction, ExpressionVocabulary.Standing),
                            TypeMismatchSpan(binary, binary.OperatorSpan));
                    }

                    Fail(CompilerDiagnosticCodes.TypeMismatch, "Arithmetic needs numbers on both sides.",
                        TypeMismatchSpan(binary, binary.OperatorSpan));
                }

                break;
            case BinaryOp.Equal or BinaryOp.NotEqual or BinaryOp.Less or BinaryOp.LessOrEqual or BinaryOp.Greater
                or BinaryOp.GreaterOrEqual:
                RejectItemNumber(left, binary.Left.Span);
                RejectItemNumber(right, binary.Right.Span);
                if (left.Type != right.Type)
                {
                    if (left is FactionNameRef leftFaction && right.Type is ExprType.Number or ExprType.Tier)
                    {
                        Fail(CompilerDiagnosticCodes.TypeMismatch,
                            FactionValueHint(leftFaction, right.Type == ExprType.Tier
                                ? ExpressionVocabulary.Tier
                                : ExpressionVocabulary.Standing), TypeMismatchSpan(binary, binary.OperatorSpan));
                    }

                    if (right is FactionNameRef rightFaction && left.Type is ExprType.Number or ExprType.Tier)
                    {
                        Fail(CompilerDiagnosticCodes.TypeMismatch,
                            FactionValueHint(rightFaction, left.Type == ExprType.Tier
                                ? ExpressionVocabulary.Tier
                                : ExpressionVocabulary.Standing), TypeMismatchSpan(binary, binary.OperatorSpan));
                    }

                    Fail(CompilerDiagnosticCodes.TypeMismatch,
                        $"Cannot compare a {Describe(left.Type)} with a {Describe(right.Type)}.",
                        TypeMismatchSpan(binary, binary.OperatorSpan));
                }

                if (left.Type is ExprType.Bool or ExprType.Text && op is not (BinaryOp.Equal or BinaryOp.NotEqual))
                {
                    Fail(CompilerDiagnosticCodes.TypeMismatch, $"Only {ExpressionVocabulary.EqualTo} and {ExpressionVocabulary.NotEqualTo} can compare {Describe(left.Type)} values.",
                        TypeMismatchSpan(binary, binary.OperatorSpan));
                }

                break;
            default:
                throw new InvalidOperationException($"Unsupported binary operator '{op}'.");
        }

        return new BinaryExpr(op, left, right);
    }

    private Effect Bind(EffectSyntax effect) => effect switch
    {
        ErrorEffectSyntax => Fail<Effect>(CompilerDiagnosticCodes.InvalidExpression, "Expression is incomplete.", effect.Span.Start),
        SetFlagEffectSyntax setFlag => new SetFlag(new FlagId(Text(setFlag.Flag)), setFlag.Set),
        GiveTakeEffectSyntax giveTake => BindGiveTake(giveTake),
        InventoryPropertyAssignmentEffectSyntax assignment => BindInventoryPropertyAssignment(assignment),
        AssignmentEffectSyntax assignment => BindAssignment(assignment),
        _ => throw new InvalidOperationException($"Unknown effect syntax node '{effect.GetType().Name}'."),
    };

    private Effect BindGiveTake(GiveTakeEffectSyntax effect)
    {
        var character = Character(effect.Character);
        var item = ItemValue(effect.Item);
        Expr quantity = new NumberLiteral(1);
        if (effect.Quantity is { } quantitySyntax)
        {
            quantity = Bind(quantitySyntax);
            if (quantity.Type != ExprType.Number)
            {
                RejectItemNumber(quantity, quantitySyntax.Span);
                if (quantity is FactionNameRef faction)
                {
                    Fail(CompilerDiagnosticCodes.TypeMismatch,
                        FactionValueHint(faction, ExpressionVocabulary.Standing), quantitySyntax.Span.Start);
                }

                Fail(CompilerDiagnosticCodes.TypeMismatch, "Item quantity must be a number.", TypeMismatchSpan(quantitySyntax));
            }
        }

        return effect.Give
            ? new GiveItem(character, item, quantity)
            : new TakeItem(character, item, quantity);
    }

    private Effect BindInventoryPropertyAssignment(InventoryPropertyAssignmentEffectSyntax effect)
    {
        var character = InventoryCharacter(effect.Character);
        var item = InventoryItem(effect.Item);
        var property = Text(effect.Property);
        if (property == ExpressionVocabulary.Name)
        {
            Fail(CompilerDiagnosticCodes.UnknownNameOrProperty,
                "Item display names are read-only.",
                new ExpressionSpan(effect.Character.Start, effect.Property.End - effect.Character.Start));
        }

        if (property == ExpressionVocabulary.Count)
        {
            Fail(CompilerDiagnosticCodes.UnknownNameOrProperty,
                $"Inventory counts are read-only; use 'give {character} {item}' or 'take {character} {item}' to change inventory.",
                new ExpressionSpan(effect.Character.Start, effect.Property.End - effect.Character.Start));
        }

        var definition = _symbols.World.Items[item];
        if (!definition.CustomProperties.TryGetValue(property, out var declaredValue))
        {
            Fail(CompilerDiagnosticCodes.UnknownNameOrProperty,
                $"Unknown inventory property '{property}' on '{Text(effect.Item)}'.",
                effect.Property);
        }

        RequireHeldItemProperty(definition, property, effect.Property);
        var expectedType = ItemPropertyType(declaredValue);
        if (effect.Operator != AssignOp.Set && expectedType != ExprType.Number)
        {
            Fail(CompilerDiagnosticCodes.TypeMismatch,
                $"Only numeric held item properties support '{ExpressionVocabulary.Spelling(AssignOp.Add)}' or " +
                $"'{ExpressionVocabulary.Spelling(AssignOp.Subtract)}'.",
                effect.Property);
        }

        var amount = Bind(effect.Amount);
        if (amount.Type != expectedType)
        {
            Fail(CompilerDiagnosticCodes.TypeMismatch,
                $"Item property '{property}' needs a {Describe(expectedType)} expression, not {Describe(amount.Type)}.",
                TypeMismatchSpan(effect.Amount));
        }

        return new ChangeItemProperty(character, item, property, effect.Operator, amount);
    }

    private void RequireHeldItemProperty(ItemDefinition definition, string property, ExpressionSpan span)
    {
        if (_symbols.World.TalemarkVersion != 2)
        {
            Fail(CompilerDiagnosticCodes.UnsupportedHeldItemPropertyAccess,
                "Per-copy item properties are available only in Talemark 2. Set 'game.talemark: 2' to use this feature.",
                span);
        }

        if (definition.Divisible)
        {
            Fail(CompilerDiagnosticCodes.UnsupportedHeldItemPropertyAccess,
                $"Divisible items have no individual copies, so property '{property}' cannot be read or changed per copy.",
                span);
        }
    }

    private static ExprType ItemPropertyType(ItemPropertyValue value) => value switch
    {
        ItemPropertyValue.Number => ExprType.Number,
        ItemPropertyValue.Boolean => ExprType.Bool,
        ItemPropertyValue.Text => ExprType.Text,
        _ => throw new InvalidOperationException("Unknown item property value type."),
    };

    private Effect BindAssignment(AssignmentEffectSyntax effect)
    {
        var targetName = Text(effect.Target);
        if (effect.Kind == ReferenceNamespace.Item)
        {
            var item = Item(effect.Target);
            var definition = _symbols.World.Items[item];
            if (effect.Member is { } member &&
                Text(member) != ExpressionVocabulary.Name &&
                !definition.CustomProperties.ContainsKey(Text(member)))
            {
                Fail(CompilerDiagnosticCodes.UnknownNameOrProperty,
                    $"Unknown item property '{Text(member)}' on '{targetName}'.", member);
            }

            Fail(CompilerDiagnosticCodes.UnknownNameOrProperty, "Item properties are read-only.",
                effect.Member ?? effect.Target);
        }

        var variable = new VarId(targetName);
        if (effect.Kind == ReferenceNamespace.Variable && _symbols.UnresolvedVariables.Contains(variable))
        {
            Fail(CompilerDiagnosticCodes.UnresolvedVariableType,
                $"Cannot infer the type of variable '{targetName}'. Declare an initial value under 'vars' in story.talewright.yaml.",
                effect.Target.Start);
        }

        if (effect.Kind == ReferenceNamespace.Faction)
        {
            var faction = new FactionId(targetName);
            if (!_symbols.World.Factions.ContainsKey(faction))
            {
                Fail(CompilerDiagnosticCodes.UnknownNameOrProperty, $"Unknown faction '{targetName}'.", effect.Target.Start);
            }

            if (effect.Member is not { } member || Text(member) != ExpressionVocabulary.Standing)
            {
                Fail(CompilerDiagnosticCodes.UnknownNameOrProperty,
                    $"Faction changes must target '{FactionReference(targetName, ExpressionVocabulary.Standing)}'.",
                    effect.Member?.Start ?? effect.Target.Start);
            }
        }

        var op = effect.Operator;
        if (effect.Kind == ReferenceNamespace.Variable && _symbols.ItemVariables.Contains(variable))
        {
            if (op != AssignOp.Set)
                Fail(CompilerDiagnosticCodes.TypeMismatch,
                    $"Item variable '{targetName}' supports '=' only.", effect.Target);
            var source = Unwrap(effect.Amount);
            Expr stored;
            if (source is ReferenceExpressionNode { Kind: ReferenceNamespace.Item, Member: null } itemReference)
                stored = new TextLiteral(Item(itemReference.Name).Value);
            else if (source is ReferenceExpressionNode { Kind: ReferenceNamespace.Variable, Member: null } &&
                     Bind(source) is ItemNameRef { Item: ResolvedItemOperand resolved })
                stored = resolved.Expression;
            else
                return Fail<Effect>(CompilerDiagnosticCodes.TypeMismatch,
                    $"Item variable '{targetName}' needs an item reference, for example 'item:rope' or another item variable.",
                    effect.Amount.Span.Start);
            return new SetTextVariable(variable, stored);
        }
        var amount = Bind(effect.Amount);
        if (effect.Kind == ReferenceNamespace.Variable && _symbols.World.TextVariables.ContainsKey(variable))
        {
            if (op != AssignOp.Set)
            {
                Fail(CompilerDiagnosticCodes.TypeMismatch, $"Text variable '{targetName}' supports '{ExpressionVocabulary.Spelling(AssignOp.Set)}' only, not '{ExpressionVocabulary.Spelling(op)}'.",
                    effect.Target.Start);
            }

            if (amount.Type != ExprType.Text)
            {
                Fail(CompilerDiagnosticCodes.TypeMismatch, $"Text variable '{targetName}' needs text, not {Describe(amount.Type)}.",
                    TypeMismatchSpan(effect.Amount));
            }

            return new SetTextVariable(variable, amount);
        }

        if (amount.Type != ExprType.Number)
        {
            RejectItemNumber(amount, effect.Amount.Span);
            if (amount is FactionNameRef faction)
            {
                Fail(CompilerDiagnosticCodes.TypeMismatch,
                    FactionValueHint(faction, ExpressionVocabulary.Standing), TypeMismatchSpan(effect.Amount));
            }

            Fail(CompilerDiagnosticCodes.TypeMismatch, effect.Kind == ReferenceNamespace.Variable
                ? $"Variable '{targetName}' has a fixed numeric type and needs a number, not {Describe(amount.Type)}."
                : "Only numbers can be assigned to stats or faction standings.", TypeMismatchSpan(effect.Amount));
        }

        return effect.Kind switch
        {
            ReferenceNamespace.Faction => new ChangeStanding(new FactionId(targetName), op, amount),
            ReferenceNamespace.Variable => new ChangeVariable(variable, op, amount),
            ReferenceNamespace.Flag => Fail<Effect>(CompilerDiagnosticCodes.MalformedEffect, "Use 'set flag:x' or 'clear flag:x' for flags.", effect.Namespace.Start),
            _ => new ChangeStat(Character(effect.Namespace), Stat(effect.Target), op, amount),
        };
    }

    private static string FactionValueHint(FactionNameRef faction, string property)
    {
        var value = FactionReference(faction.Faction.Value, property);
        return property == ExpressionVocabulary.Tier
            ? $"Faction names are text. Use '{value}' to compare the faction's standing tier."
            : $"Faction names are text. Use '{value}' for the numeric standing.";
    }

    private static ExpressionNode Unwrap(ExpressionNode node) =>
        node is ParenthesizedExpressionNode parenthesized ? Unwrap(parenthesized.Inner) : node;

    internal static bool IsItemSource(ExpressionNode node, Expr? expression) => Unwrap(node) switch
    {
        ReferenceExpressionNode { Kind: ReferenceNamespace.Item, Member: null } => true,
        ReferenceExpressionNode { Kind: ReferenceNamespace.Variable, Member: null } =>
            expression is ItemNameRef { Item: ResolvedItemOperand },
        _ => false,
    };

    private void RejectItemNumber(Expr expression, ExpressionSpan span)
    {
        if (expression is ItemNameRef { Item: ResolvedItemOperand { Expression: TextVarRef variable } })
            Fail(CompilerDiagnosticCodes.TypeMismatch,
                $"Variable '{variable.Variable}' holds an item, not a number. " +
                $"Use 'player:inventory:var:{variable.Variable}:count' for its count.", span);
    }

    private ItemOperand ItemValue(ExpressionSpan span, bool inventory = false)
    {
        var text = Text(span);
        var separator = text.IndexOf(ExpressionVocabulary.Separator);
        if (separator < 0)
            return new FixedItemOperand(inventory ? InventoryItem(span) : Item(span));
        if (_symbols.World.TalemarkVersion != 2)
            return Fail<ItemOperand>(CompilerDiagnosticCodes.TypeMismatch,
                "Item variables are available only in Talemark 2. Set 'game.talemark: 2' to use them.", span.Start);
        var variable = new VarId(text[(separator + 1)..].Trim());
        if (!_symbols.ItemVariables.Contains(variable))
            return Fail<ItemOperand>(CompilerDiagnosticCodes.TypeMismatch,
                $"Variable '{variable}' does not hold an item. Assign it an item first, for example '~ var:{variable} = item:rope'.",
                span.Start);
        return new ResolvedItemOperand(new TextVarRef(variable));
    }

    private static string FactionReference(string faction, string property) =>
        $"{ExpressionVocabulary.FactionPrefix}{faction}{ExpressionVocabulary.Separator}{property}";

    private EntityId Character(ExpressionSpan span)
    {
        var name = Text(span);
        var id = new EntityId(name);
        if (!_symbols.World.Characters.ContainsKey(id))
        {
            Fail(CompilerDiagnosticCodes.UnknownNameOrProperty, $"Unknown character '{name}'.", span.Start);
        }

        return id;
    }

    private ItemId Item(ExpressionSpan span)
    {
        var name = Text(span);
        var id = new ItemId(name);
        if (!_symbols.World.Items.ContainsKey(id))
        {
            Fail(CompilerDiagnosticCodes.UnknownNameOrProperty, $"Unknown item '{name}'.", span.Start);
        }

        return id;
    }

    private EntityId InventoryCharacter(ExpressionSpan span)
    {
        var name = Text(span);
        var id = new EntityId(name);
        if (!_symbols.World.Characters.ContainsKey(id))
        {
            Fail(CompilerDiagnosticCodes.UnknownNameOrProperty, $"Unknown character '{name}'.", span);
        }

        return id;
    }

    private ItemId InventoryItem(ExpressionSpan span)
    {
        var name = Text(span);
        var id = new ItemId(name);
        if (!_symbols.World.Items.ContainsKey(id))
        {
            Fail(CompilerDiagnosticCodes.UnknownNameOrProperty, $"Unknown item '{name}'.", span);
        }

        return id;
    }

    [DoesNotReturn]
    private void Fail(string code, string message, ExpressionSpan span)
    {
        _diagnostics.Error(code, message, _locate(span.Start), _locate(span.End));
        throw new BindingException();
    }

    private StatId Stat(ExpressionSpan span)
    {
        var name = Text(span);
        var id = new StatId(name);
        if (!_symbols.World.Ruleset.HasStat(id))
        {
            Fail(CompilerDiagnosticCodes.UnknownNameOrProperty,
                $"Unknown stat '{name}' (not in ruleset '{_symbols.World.Ruleset.Id}')." +
                _symbols.StatGuidance?.ForMissingStat(id), span.Start);
        }

        return id;
    }

    private string Text(ExpressionSpan span) => _tokens.Source.AsSpan(span.Start, span.Length).ToString();

    private static ExpressionSpan TypeMismatchSpan(ExpressionNode syntax) =>
        TypeMismatchSpan(syntax, syntax.Span);

    private static ExpressionSpan TypeMismatchSpan(ExpressionNode syntax, ExpressionSpan fallback) => syntax switch
    {
        ReferenceExpressionNode { Kind: ReferenceNamespace.Item, Member: { } member } => member,
        ParenthesizedExpressionNode parenthesized => TypeMismatchSpan(parenthesized.Inner, fallback),
        UnaryExpressionNode unary => TypeMismatchSpan(unary.Operand, fallback),
        BinaryExpressionNode binary => TypeMismatchSpan(binary.Left, TypeMismatchSpan(binary.Right, fallback)),
        _ => fallback,
    };

    private static string Describe(ExprType type) => type switch
    {
        ExprType.Bool => "true/false",
        ExprType.Number => "number",
        ExprType.Text => "text",
        _ => "reputation tier",
    };

    [DoesNotReturn]
    private void Fail(string code, string message, int offset)
    {
        _diagnostics.Error(code, message, _locate(offset));
        throw new BindingException();
    }

    private T Fail<T>(string code, string message, int offset)
    {
        Fail(code, message, offset);
        return default!;
    }
}
