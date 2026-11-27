using Talewright.Core.Identity;
using Talewright.Core.Rulesets;
using Talewright.Core.State;
using Talewright.Core.World;
using Talewright.Core;

namespace Talewright.Rules.Expressions;

/// <summary>Result of evaluating an <see cref="Expr"/>. Exactly one payload is meaningful, per <see cref="Type"/>.</summary>
public readonly record struct Value(ExprType Type, int Number, bool Bool, string? Text, ReputationTier? Tier)
{
    public ExactNumber ExactNumber { get; init; } =
        Talewright.Core.ExactNumber.FromInteger(Number);

    public static Value Of(int number) => new(ExprType.Number, number, false, null, null);
    public static Value Of(ExactNumber number) =>
        new(ExprType.Number, number.ToInt32RoundedAwayFromZero(), false, null, null)
        {
            ExactNumber = number,
        };
    public static Value Of(bool value) => new(ExprType.Bool, 0, value, null, null);
    public static Value Of(string text) => new(ExprType.Text, 0, false, text, null);
    public static Value Of(ReputationTier tier) => new(ExprType.Tier, 0, false, null, tier);

    /// <summary>Exact invariant form used when inspecting evaluated expressions.</summary>
    public override string ToString() => Type switch
    {
        ExprType.Number => ExactNumber.ToDecimalString(),
        ExprType.Bool => Bool ? "true" : "false",
        ExprType.Text => Text ?? string.Empty,
        ExprType.Tier => Tier?.Name ?? string.Empty,
        _ => string.Empty,
    };
}

/// <summary>Everything an expression may read.</summary>
public sealed record EvaluationContext(IGameStateView State, NamedPredicateRegistry Predicates)
{
    public Action<ExpressionTrace>? Trace { get; init; }

    public Func<SkillCheckExpr, Value>? ResolveSkillCheck { get; init; }

    public static EvaluationContext For(IGameStateView state) => new(state, NamedPredicateRegistry.Empty);
}

/// <summary>Tree-walking evaluator. Fast enough for interactive fiction and fully trim/AOT safe.</summary>
public static class ExpressionEvaluator
{
    public static bool IsTrue(Expr expr, EvaluationContext context) => Evaluate(expr, context).Bool;

    public static int EvaluateNumber(Expr expr, EvaluationContext context) => Evaluate(expr, context).Number;

    public static ExactNumber EvaluateExactNumber(Expr expr, EvaluationContext context) =>
        Evaluate(expr, context).ExactNumber;

    public static Value Evaluate(Expr expr, EvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(expr);
        ArgumentNullException.ThrowIfNull(context);

        if (expr is not (UnaryExpr or BinaryExpr))
        {
            var value = EvaluateAtom(expr, context, out var childTrace);
            context.Trace?.Invoke(new ExpressionTrace(expr, value,
                childTrace is null ? Array.Empty<ExpressionTrace>() : [childTrace]));
            return value;
        }

        var frames = new Stack<EvaluationFrame>();
        frames.Push(new EvaluationFrame(expr, context.Trace is not null));
        var returnedValue = default(Value);
        ExpressionTrace? returnedTrace = null;

        while (frames.TryPeek(out var frame))
        {
            switch (frame.Expression)
            {
                case UnaryExpr unary when frame.State == 0:
                    frame.State = 1;
                    frames.Push(new EvaluationFrame(unary.Operand, context.Trace is not null));
                    break;
                case UnaryExpr unary:
                    if (frame.Children is not null) frame.Children.Add(returnedTrace!);
                    returnedValue = unary.Op switch
                    {
                        UnaryOp.Not => Value.Of(!returnedValue.Bool),
                        UnaryOp.Negate => context.State.World.TalemarkVersion == 1
                            ? Value.Of(Arithmetic.Saturate(-(long)returnedValue.Number))
                            : Value.Of(-returnedValue.ExactNumber),
                        UnaryOp.Round => ApplyNumericModifier(unary.Op, returnedValue.ExactNumber, context),
                        UnaryOp.RoundUp => ApplyNumericModifier(unary.Op, returnedValue.ExactNumber, context),
                        UnaryOp.RoundDown => ApplyNumericModifier(unary.Op, returnedValue.ExactNumber, context),
                        _ => throw new NotSupportedException(unary.Op.ToString()),
                    };
                    Complete(frame, returnedValue);
                    break;
                case BinaryExpr binary when frame.State == 0:
                    frame.State = 1;
                    frames.Push(new EvaluationFrame(binary.Left, context.Trace is not null));
                    break;
                case BinaryExpr binary when frame.State == 1:
                    frame.Left = returnedValue;
                    if (frame.Children is not null) frame.Children.Add(returnedTrace!);
                    if (binary.Op == BinaryOp.And && !returnedValue.Bool)
                    {
                        if (frame.Children is not null) frame.Children.Add(ExpressionTrace.NotEvaluated(binary.Right));
                        returnedValue = Value.Of(false);
                        Complete(frame, returnedValue);
                    }
                    else if (binary.Op == BinaryOp.Or && returnedValue.Bool)
                    {
                        if (frame.Children is not null) frame.Children.Add(ExpressionTrace.NotEvaluated(binary.Right));
                        returnedValue = Value.Of(true);
                        Complete(frame, returnedValue);
                    }
                    else
                    {
                        frame.State = 2;
                        frames.Push(new EvaluationFrame(binary.Right, context.Trace is not null));
                    }
                    break;
                case BinaryExpr binary:
                    if (frame.Children is not null) frame.Children.Add(returnedTrace!);
                    returnedValue = ApplyBinary(binary, frame.Left, returnedValue, context);
                    Complete(frame, returnedValue);
                    break;
                default:
                    returnedValue = EvaluateAtom(frame.Expression, context, out var childTrace);
                    if (frame.Children is not null && childTrace is not null)
                        frame.Children.Add(childTrace);
                    Complete(frame, returnedValue);
                    break;
            }
        }

        if (context.Trace is { } observe)
        {
            observe(returnedTrace!);
        }
        return returnedValue;

        void Complete(EvaluationFrame completed, Value value)
        {
            returnedTrace = completed.Children is null
                ? null
                : new ExpressionTrace(completed.Expression, value, completed.Children.AsReadOnly());
            frames.Pop();
        }
    }

    private sealed class EvaluationFrame(Expr expression, bool trace)
    {
        public Expr Expression { get; } = expression;
        public int State { get; set; }
        public Value Left { get; set; }
        public List<ExpressionTrace>? Children { get; } = trace ? [] : null;
    }

    private static Value EvaluateAtom(Expr expr, EvaluationContext context, out ExpressionTrace? childTrace)
    {
        childTrace = null;
        var state = context.State;
        if (expr is ItemNameRef itemName)
        {
            var item = ResolveItemOperand(itemName.Item, context, out childTrace);
            return item is { } itemNameId && state.World.Items.TryGetValue(itemNameId, out var definition)
                ? Value.Of(definition.Name)
                : Value.Of(string.Empty);
        }
        if (expr is HasItem hasItem)
            return Value.Of(HeldQuantity(hasItem.Character, hasItem.Item, context, out childTrace) > 0);
        if (expr is InventoryCountRef inventoryCount)
            return Value.Of(HeldQuantity(inventoryCount.Character, inventoryCount.Item, context, out childTrace));
        if (expr is ItemComparison comparison)
        {
            var item = ResolveItemOperand(comparison.Item, context, out childTrace);
            return Value.Of((item == comparison.Expected) == (comparison.Op == BinaryOp.Equal));
        }
        if (expr is ItemReferencePropertyRef referenceProperty)
        {
            var reference = ResolveItemReference(referenceProperty.Item, context, out childTrace);
            return ItemProperty(ReferencedProperty(reference, referenceProperty, state));
        }
        if (expr is InventoryItemPropertyRef itemProperty)
            return ItemProperty(state.GetItemProperty(itemProperty.Character, itemProperty.Item, itemProperty.Property));

        return expr switch
        {
            NumberLiteral e => Value.Of(e.Value),
            BoolLiteral e => Value.Of(e.Value),
            TextLiteral e => Value.Of(e.Value),
            TierLiteral e => Value.Of(e.Tier),
            StatRef e => state.World.TalemarkVersion == 1
                ? Value.Of(state.GetStat(e.Character, e.Stat))
                : Value.Of(state.GetExactStat(e.Character, e.Stat)),
            StandingRef e => Value.Of(state.GetStanding(e.Faction)),
            StandingTierRef e => state.Ruleset.TierFor(state.GetStanding(e.Faction)) is { } tier
                ? Value.Of(tier)
                : throw new InvalidOperationException("The ruleset defines no reputation tiers."),
            FlagRef e => Value.Of(state.GetFlag(e.Flag)),
            VarRef e => Value.Of(state.GetExactVariable(e.Variable)),
            TextVarRef e => Value.Of(state.GetTextVariable(e.Variable)),
            NameRef e => Value.Of(Name(state, e)),
            PronounRef e => Value.Of(Pronoun(state, e)),
            FactionNameRef e => Value.Of(state.World.Factions[e.Faction].Name),
            ItemPropertyRef e => ItemProperty(e.Value),
            Visited e => Value.Of(state.VisitCount(e.Scene) > 0),
            SceneVisitCountRef e => Value.Of(e.Scene is { } scene
                ? state.VisitCount(scene)
                : state.CurrentScene is { } current ? state.VisitCount(current) : 0),
            SkillCheckExpr e => context.ResolveSkillCheck?.Invoke(e)
                ?? throw new InvalidOperationException("A skill check requires an active play evaluation context."),
            NamedPredicate e => Value.Of(context.Predicates.Evaluate(e.Name, state)),
            _ => throw new NotSupportedException($"Unknown expression {expr.GetType().Name}."),
        };
    }

    private static ItemId? ResolveItemOperand(ItemOperand item, EvaluationContext context,
        out ExpressionTrace? trace) => ResolveItemReference(item, context, out trace)?.Resolve(context.State);

    private static ItemReference? ResolveItemReference(ItemOperand item, EvaluationContext context,
        out ExpressionTrace? trace)
    {
        trace = null;
        if (item is not ResolvedItemOperand || context.Trace is null)
            return item.ResolveReference(context);

        ExpressionTrace? operandTrace = null;
        var operandContext = context with { Trace = observed => operandTrace = observed };
        var resolved = item.ResolveReference(operandContext);
        trace = operandTrace;
        return resolved;
    }

    // A group reference counts only the character's group with the same values; a plain item counts every copy.
    private static ExactNumber HeldQuantity(EntityId character, ItemOperand operand, EvaluationContext context,
        out ExpressionTrace? trace)
    {
        var reference = ResolveItemReference(operand, context, out trace);
        if (reference is null)
            return ExactNumber.Zero;
        if (!reference.IsGroup)
            return context.State.GetExactItemCount(character, reference.Item);
        if (reference.Holder != character)
            return ExactNumber.Zero;
        var groups = context.State.GetItemGroups(character, reference.Item);
        var index = reference.FindGroup(groups);
        return index >= 0 ? groups[index].Quantity : ExactNumber.Zero;
    }

    private static ItemPropertyValue ReferencedProperty(ItemReference? reference, ItemReferencePropertyRef read,
        IGameStateView state)
    {
        ItemPropertyValue? value = null;
        if (reference?.Resolve(state) is { } item)
        {
            if (reference.Holder is { } holder)
            {
                var groups = state.GetItemGroups(holder, item);
                groups[reference.FindGroup(groups)].Properties.TryGetValue(read.Property, out value);
            }
            else
            {
                state.World.Items[item].CustomProperties.TryGetValue(read.Property, out value);
            }
        }

        return value is not null && ItemPropertySnapshot.KindOf(value) == read.Kind
            ? value
            : read.Kind switch
            {
                ItemPropertyValueKind.Number => new ItemPropertyValue.Number(ExactNumber.Zero),
                ItemPropertyValueKind.Boolean => new ItemPropertyValue.Boolean(false),
                ItemPropertyValueKind.Text => new ItemPropertyValue.Text(string.Empty),
                _ => throw new InvalidOperationException("Unknown item property value type."),
            };
    }

    private static Value ItemProperty(ItemPropertyValue value) => value switch
    {
        ItemPropertyValue.Number number => Value.Of(number.Value),
        ItemPropertyValue.Boolean boolean => Value.Of(boolean.Value),
        ItemPropertyValue.Text text when text.Value is not null => Value.Of(text.Value),
        _ => throw new InvalidOperationException("Compiled item property has an invalid value."),
    };

    private static Value ApplyBinary(BinaryExpr e, Value left, Value right, EvaluationContext context)
    {
        return e.Op switch
        {
            BinaryOp.Add => Numeric(left, right, context, BinaryOp.Add),
            BinaryOp.Subtract => Numeric(left, right, context, BinaryOp.Subtract),
            BinaryOp.Multiply => Numeric(left, right, context, BinaryOp.Multiply),
            BinaryOp.Divide => Numeric(left, right, context, BinaryOp.Divide),
            BinaryOp.Equal => Value.Of(Compare(left, right, context) == 0),
            BinaryOp.NotEqual => Value.Of(Compare(left, right, context) != 0),
            BinaryOp.Less => Value.Of(Compare(left, right, context) < 0),
            BinaryOp.LessOrEqual => Value.Of(Compare(left, right, context) <= 0),
            BinaryOp.Greater => Value.Of(Compare(left, right, context) > 0),
            BinaryOp.GreaterOrEqual => Value.Of(Compare(left, right, context) >= 0),
            BinaryOp.And => Value.Of(left.Bool && right.Bool),
            BinaryOp.Or => Value.Of(left.Bool || right.Bool),
            _ => throw new NotSupportedException(e.Op.ToString()),
        };
    }

    private static Value ApplyNumericModifier(UnaryOp op, ExactNumber value, EvaluationContext context)
    {
        if (context.State.World.TalemarkVersion == 1)
            throw new InvalidOperationException("Numeric rounding modifiers require Talemark 2.");

        return Value.Of(op switch
        {
            UnaryOp.Round => value.RoundToNearestInteger(),
            UnaryOp.RoundUp => value.Ceiling(),
            UnaryOp.RoundDown => value.Floor(),
            _ => throw new NotSupportedException(op.ToString()),
        });
    }

    private static int Compare(Value left, Value right, EvaluationContext context) => left.Type switch
    {
        ExprType.Number => left.ExactNumber.CompareTo(right.ExactNumber),
        ExprType.Bool => left.Bool.CompareTo(right.Bool),
        ExprType.Text => string.CompareOrdinal(left.Text, right.Text),
        ExprType.Tier => context.State.Ruleset.TierIndex(left.Tier!).CompareTo(context.State.Ruleset.TierIndex(right.Tier!)),
        _ => 0,
    };

    private static Value Numeric(Value left, Value right, EvaluationContext context, BinaryOp op)
    {
        if (context.State.World.TalemarkVersion == 1)
        {
            var value = op switch
            {
                BinaryOp.Add => Arithmetic.Saturate((long)left.Number + right.Number),
                BinaryOp.Subtract => Arithmetic.Saturate((long)left.Number - right.Number),
                BinaryOp.Multiply => Arithmetic.Saturate((long)left.Number * right.Number),
                BinaryOp.Divide => right.Number == 0 ? 0 :
                    Arithmetic.Saturate((long)left.Number / right.Number),
                _ => throw new NotSupportedException(op.ToString()),
            };
            return Value.Of(value);
        }

        if (op == BinaryOp.Divide && right.ExactNumber == ExactNumber.Zero)
            return Value.Of(ExactNumber.Zero);

        return Value.Of(op switch
        {
            BinaryOp.Add => left.ExactNumber + right.ExactNumber,
            BinaryOp.Subtract => left.ExactNumber - right.ExactNumber,
            BinaryOp.Multiply => left.ExactNumber * right.ExactNumber,
            BinaryOp.Divide => left.ExactNumber / right.ExactNumber,
            _ => throw new NotSupportedException(op.ToString()),
        });
    }

    private static string Name(IGameStateView state, NameRef e)
    {
        var character = state.GetCharacterDefinition(e.Character);
        return e.Part switch
        {
            NamePart.First => character.FirstName,
            NamePart.Last => character.LastName,
            _ => character.FullName,
        };
    }

    private static string Pronoun(IGameStateView state, PronounRef e)
    {
        var p = state.GetCharacterDefinition(e.Character).Pronouns;
        return e.Form switch
        {
            PronounForm.Subject => p.Subject,
            PronounForm.Object => p.Object,
            PronounForm.Possessive => p.Possessive,
            PronounForm.PossessivePronoun => p.PossessivePronoun,
            _ => p.Reflexive,
        };
    }
}
