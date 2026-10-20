using Talewright.Core.Events;
using Talewright.Core.State;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;

namespace Talewright.Rules.Triggers;

/// <summary>
/// A reactive rule: when <see cref="When"/> becomes true, run <see cref="Effects"/>.
/// Triggers fire on the rising edge (false → true); <see cref="Once"/> triggers fire at most once per playthrough.
/// </summary>
public sealed record TriggerDefinition(string Id, Expr When, IReadOnlyList<Effect> Effects, bool Once);

/// <summary>
/// Evaluates triggers after every state change. Cascades (a trigger's effects firing another trigger)
/// are allowed but bounded by <see cref="MaxPasses"/> to prevent infinite loops.
/// </summary>
public sealed class TriggerEngine
{
    public const int MaxPasses = 8;

    private readonly IReadOnlyList<TriggerDefinition> _triggers;
    private readonly NamedPredicateRegistry _predicates;
    private readonly HashSet<string> _currentlyTrue = [];

    public TriggerEngine(IReadOnlyList<TriggerDefinition> triggers, NamedPredicateRegistry predicates, IGameStateView initialState)
    {
        _triggers = triggers;
        _predicates = predicates;
        Prime(initialState);
    }

    /// <summary>
    /// Records which conditions are already true so they do not fire immediately (e.g. after loading a save).
    /// Edge memory is derived state, so it is recomputed rather than stored in events.
    /// </summary>
    public void Prime(IGameStateView state)
    {
        _currentlyTrue.Clear();
        var context = new EvaluationContext(state, _predicates);
        foreach (var trigger in _triggers)
        {
            if (ExpressionEvaluator.IsTrue(trigger.When, context))
            {
                _currentlyTrue.Add(trigger.Id);
            }
        }
    }

    /// <summary>Fires every trigger whose condition has newly become true. Returns the ids fired, in order.</summary>
    public IReadOnlyList<string> Run(EventLog log)
    {
        var fired = new List<string>();
        for (var pass = 0; pass < MaxPasses; pass++)
        {
            var firedThisPass = false;
            foreach (var trigger in _triggers)
            {
                var isTrue = ExpressionEvaluator.IsTrue(trigger.When, new EvaluationContext(log.State, _predicates));
                var wasTrue = _currentlyTrue.Contains(trigger.Id);
                if (!isTrue)
                {
                    _currentlyTrue.Remove(trigger.Id);
                    continue;
                }

                _currentlyTrue.Add(trigger.Id);
                if (wasTrue || (trigger.Once && log.State.HasFiredTrigger(trigger.Id)))
                {
                    continue;
                }

                log.Append(new TriggerFired(trigger.Id));
                EffectExecutor.ExecuteAll(trigger.Effects, log, _predicates);
                fired.Add(trigger.Id);
                firedThisPass = true;
            }

            if (!firedThisPass)
            {
                break;
            }
        }

        return fired;
    }
}
