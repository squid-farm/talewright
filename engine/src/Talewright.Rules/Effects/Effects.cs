using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Core.World;
using Talewright.Core;
using Talewright.Rules.Diagnostics;
using Talewright.Rules.Expressions;

namespace Talewright.Rules.Effects;

public enum AssignOp
{
    Set,
    Add,
    Subtract,
}

/// <summary>
/// A typed state change requested by a story (<c>player:hp -= 3</c>, <c>give player relic</c>).
/// Effects are executed by <see cref="EffectExecutor"/>, which turns them into events.
/// </summary>
public abstract record Effect;

public sealed record ChangeStat(EntityId Character, StatId Stat, AssignOp Op, Expr Amount) : Effect;

public sealed record ChangeStanding(FactionId Faction, AssignOp Op, Expr Amount) : Effect;

public sealed record ChangeVariable(VarId Variable, AssignOp Op, Expr Amount) : Effect;

public sealed record SetTextVariable(VarId Variable, Expr Value) : Effect;

public sealed record SetFlag(FlagId Flag, bool Value) : Effect;

public sealed record ChangeItemProperty(EntityId Character, ItemId Item, string Property,
    AssignOp Op, Expr Amount) : Effect;

public sealed record GiveItem(EntityId Character, ItemOperand Item, Expr Quantity) : Effect
{
    public GiveItem(EntityId character, ItemId item, Expr quantity)
        : this(character, new FixedItemOperand(item), quantity) { }
    public GiveItem(EntityId character, Expr item, Expr quantity)
        : this(character, new ResolvedItemOperand(item), quantity) { }
}

public sealed record TakeItem(EntityId Character, ItemOperand Item, Expr Quantity) : Effect
{
    public TakeItem(EntityId character, ItemId item, Expr quantity)
        : this(character, new FixedItemOperand(item), quantity) { }
    public TakeItem(EntityId character, Expr item, Expr quantity)
        : this(character, new ResolvedItemOperand(item), quantity) { }
}

/// <summary>Executes effects against an <see cref="EventLog"/>, one at a time so later effects see earlier results.</summary>
public static class EffectExecutor
{
    public static void ExecuteAll(IEnumerable<Effect> effects, EventLog log, NamedPredicateRegistry predicates,
        Func<SkillCheckExpr, Value>? resolveSkillCheck = null) =>
        ExecuteAll(effects, log, predicates, resolveSkillCheck, observeExpression: null);

    public static void ExecuteAll(IEnumerable<Effect> effects, EventLog log, NamedPredicateRegistry predicates,
        Func<SkillCheckExpr, Value>? resolveSkillCheck, Action<ExpressionTrace>? observeExpression)
    {
        foreach (var effect in effects)
        {
            Execute(effect, log, predicates, resolveSkillCheck, observeExpression);
        }
    }

    public static void Execute(Effect effect, EventLog log, NamedPredicateRegistry predicates,
        Func<SkillCheckExpr, Value>? resolveSkillCheck = null) =>
        Execute(effect, log, predicates, resolveSkillCheck, observeExpression: null);

    public static void Execute(Effect effect, EventLog log, NamedPredicateRegistry predicates,
        Func<SkillCheckExpr, Value>? resolveSkillCheck, Action<ExpressionTrace>? observeExpression)
    {
        var state = log.State;
        var context = new EvaluationContext(state, predicates)
        {
            ResolveSkillCheck = resolveSkillCheck,
            Trace = observeExpression,
        };
        var integerArithmetic = state.World.TalemarkVersion == 1;
        ExactNumber Eval(Expr e) => ExpressionEvaluator.EvaluateExactNumber(e, context);
        int EvalWhole(Expr e) => integerArithmetic
            ? ExpressionEvaluator.EvaluateNumber(e, context)
            : Eval(e).ToInt32RoundedAwayFromZero();

        IEnumerable<GameEvent> events = effect switch
        {
            ChangeStat e when integerArithmetic => StateChanges.SetStat(state, e.Character, e.Stat,
                Apply(state.GetBaseStat(e.Character, e.Stat), e.Op, EvalWhole(e.Amount))),
            ChangeStat e when state.Ruleset.IsFractionalResource(e.Stat) =>
                StateChanges.SetStat(state, e.Character, e.Stat,
                    Apply(state, state.GetExactBaseStat(e.Character, e.Stat), e.Op, Eval(e.Amount))),
            ChangeStat e => StateChanges.SetStat(state, e.Character, e.Stat,
                Apply(state.GetBaseStat(e.Character, e.Stat), e.Op, EvalWhole(e.Amount))),
            ChangeStanding e => StateChanges.SetStanding(state, e.Faction,
                Apply(state.GetStanding(e.Faction), e.Op, EvalWhole(e.Amount))),
            ChangeVariable e when integerArithmetic => StateChanges.SetVariable(state, e.Variable,
                Apply(state.GetVariable(e.Variable), e.Op, EvalWhole(e.Amount))),
            ChangeVariable e => StateChanges.SetVariable(state, e.Variable,
                Apply(state, state.GetExactVariable(e.Variable), e.Op, Eval(e.Amount))),
            SetTextVariable e when e.Value.Type == ExprType.Text =>
                StateChanges.SetTextVariable(state, e.Variable, ExpressionEvaluator.Evaluate(e.Value, context).Text
                    ?? throw new InvalidOperationException("A text expression produced no text.")),
            SetFlag e => StateChanges.SetFlag(state, e.Flag, e.Value),
            ChangeItemProperty e => ChangeHeldItemProperty(state, e, context),
            GiveItem e => e.Item.Resolve(context) is { } item
                ? integerArithmetic
                    ? StateChanges.GiveItem(e.Character, item, EvalWhole(e.Quantity))
                    : StateChanges.GiveItem(state, e.Character, item, Eval(e.Quantity))
                : [],
            TakeItem e => e.Item.Resolve(context) is { } item
                ? integerArithmetic
                    ? StateChanges.TakeItem(state, e.Character, item, EvalWhole(e.Quantity))
                    : StateChanges.TakeItem(state, e.Character, item, Eval(e.Quantity))
                : [],
            _ => throw new NotSupportedException($"Unknown effect {effect.GetType().Name}."),
        };
        log.AppendRange(events.ToArray());
    }

    private static IEnumerable<GameEvent> ChangeHeldItemProperty(IGameStateView state,
        ChangeItemProperty change, EvaluationContext context)
    {
        if (state.GetItemGroups(change.Character, change.Item).Count == 0)
            return [];

        var current = state.GetItemProperty(change.Character, change.Item, change.Property);
        var amount = ExpressionEvaluator.Evaluate(change.Amount, context);
        ItemPropertyValue value = (current, change.Op, amount.Type) switch
        {
            (ItemPropertyValue.Number number, _, ExprType.Number) =>
                new ItemPropertyValue.Number(Apply(state, number.Value, change.Op, amount.ExactNumber)),
            (ItemPropertyValue.Boolean, AssignOp.Set, ExprType.Bool) =>
                new ItemPropertyValue.Boolean(amount.Bool),
            (ItemPropertyValue.Text, AssignOp.Set, ExprType.Text) =>
                new ItemPropertyValue.Text(amount.Text
                    ?? throw new InvalidOperationException("A text expression produced no text.")),
            _ => throw new InvalidOperationException(
                $"Cannot apply '{change.Op}' with a {amount.Type} expression to item property '{change.Property}'."),
        };
        return StateChanges.SetItemProperty(state, change.Character, change.Item, change.Property, value);
    }

    private static int Apply(int current, AssignOp op, int amount) => op switch
    {
        AssignOp.Set => amount,
        AssignOp.Add => Arithmetic.Saturate((long)current + amount),
        AssignOp.Subtract => Arithmetic.Saturate((long)current - amount),
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unknown assignment operation."),
    };

    private static ExactNumber Apply(IGameStateView state, ExactNumber current, AssignOp op, ExactNumber amount)
    {
        if (state.World.TalemarkVersion == 1)
            return ExactNumber.FromInteger(Apply(current.ToInt32RoundedAwayFromZero(), op,
                amount.ToInt32RoundedAwayFromZero()));

        return op switch
        {
            AssignOp.Set => amount,
            AssignOp.Add => current + amount,
            AssignOp.Subtract => current - amount,
            _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unknown assignment operation."),
        };
    }
}
