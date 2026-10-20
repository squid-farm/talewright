using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.State;
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

public sealed record SetFlag(FlagId Flag, bool Value) : Effect;

public sealed record GiveItem(EntityId Character, ItemId Item, Expr Quantity) : Effect;

public sealed record TakeItem(EntityId Character, ItemId Item, Expr Quantity) : Effect;

/// <summary>Executes effects against an <see cref="EventLog"/>, one at a time so later effects see earlier results.</summary>
public static class EffectExecutor
{
    public static void ExecuteAll(IEnumerable<Effect> effects, EventLog log, NamedPredicateRegistry predicates)
    {
        foreach (var effect in effects)
        {
            Execute(effect, log, predicates);
        }
    }

    public static void Execute(Effect effect, EventLog log, NamedPredicateRegistry predicates)
    {
        var state = log.State;
        var context = new EvaluationContext(state, predicates);
        int Eval(Expr e) => ExpressionEvaluator.EvaluateNumber(e, context);

        IEnumerable<GameEvent> events = effect switch
        {
            ChangeStat e => StateChanges.SetStat(state, e.Character, e.Stat, Apply(state.GetBaseStat(e.Character, e.Stat), e.Op, Eval(e.Amount))),
            ChangeStanding e => StateChanges.SetStanding(state, e.Faction, Apply(state.GetStanding(e.Faction), e.Op, Eval(e.Amount))),
            ChangeVariable e => StateChanges.SetVariable(state, e.Variable, Apply(state.GetVariable(e.Variable), e.Op, Eval(e.Amount))),
            SetFlag e => StateChanges.SetFlag(state, e.Flag, e.Value),
            GiveItem e => StateChanges.GiveItem(e.Character, e.Item, Eval(e.Quantity)),
            TakeItem e => StateChanges.TakeItem(state, e.Character, e.Item, Eval(e.Quantity)),
            _ => throw new NotSupportedException($"Unknown effect {effect.GetType().Name}."),
        };
        log.AppendRange(events.ToArray());
    }

    private static int Apply(int current, AssignOp op, int amount) => op switch
    {
        AssignOp.Set => amount,
        AssignOp.Add => current + amount,
        _ => current - amount,
    };
}
