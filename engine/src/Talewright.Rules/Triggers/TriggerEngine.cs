using Talewright.Core.Events;
using Talewright.Core.State;
using Talewright.Rules.Diagnostics;
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
    public const int MaxPasses = 32;

    private readonly IReadOnlyList<TriggerDefinition> _triggers;
    private readonly NamedPredicateRegistry _predicates;
    private readonly Action<RuleDecision>? _observer;
    private readonly HashSet<string> _currentlyTrue = [];

    /// <summary>Snapshot of rising-edge memory, without evaluating conditions again.</summary>
    public IReadOnlyList<string> CaptureEdgeMemory() =>
        Array.AsReadOnly(_currentlyTrue.Order(StringComparer.Ordinal).ToArray());

    public TriggerEngine(IReadOnlyList<TriggerDefinition> triggers, NamedPredicateRegistry predicates, IGameStateView initialState)
        : this(triggers, predicates, initialState, observer: null)
    {
    }

    public TriggerEngine(IReadOnlyList<TriggerDefinition> triggers, NamedPredicateRegistry predicates,
        IGameStateView initialState, Action<RuleDecision>? observer, int? eventIndex = null)
    {
        _triggers = triggers;
        _predicates = predicates;
        _observer = observer;
        Prime(initialState, eventIndex);
    }

    /// <summary>
    /// Records which conditions are already true so they do not fire immediately (e.g. after loading a save).
    /// Edge memory is derived state, so it is recomputed rather than stored in events.
    /// </summary>
    public void Prime(IGameStateView state) => Prime(state, eventIndex: null);

    private void Prime(IGameStateView state, int? eventIndex)
    {
        _currentlyTrue.Clear();
        var context = new EvaluationContext(state, _predicates);
        foreach (var trigger in _triggers)
        {
            if (RuleObservation.Test(trigger.When, context, _observer is not null, out var trace))
            {
                _currentlyTrue.Add(trigger.Id);
            }
            _observer?.Invoke(new RuleDecision(DecisionKind.Trigger, trigger.Id, state.CurrentScene,
                DecisionOutcome.Primed, DecisionReason.InitialCondition, trace, eventIndex, eventIndex));
        }
    }

    /// <summary>Fires every trigger whose condition has newly become true. Returns the ids fired, in order.</summary>
    public IReadOnlyList<string> Run(EventLog log)
    {
        var fired = new List<string>();
        for (var pass = 0; pass <= MaxPasses; pass++)
        {
            var firedThisPass = false;
            var pending = new List<string>();
            foreach (var trigger in _triggers)
            {
                var start = log.Events.Count;
                var isTrue = RuleObservation.Test(trigger.When, new EvaluationContext(log.State, _predicates),
                    _observer is not null, out var trace);
                var wasTrue = _currentlyTrue.Contains(trigger.Id);
                if (!isTrue)
                {
                    _currentlyTrue.Remove(trigger.Id);
                    Observe(DecisionOutcome.Suppressed, DecisionReason.ConditionFalse);
                    continue;
                }

                _currentlyTrue.Add(trigger.Id);
                if (wasTrue || (trigger.Once && log.State.HasFiredTrigger(trigger.Id)))
                {
                    Observe(DecisionOutcome.Suppressed, wasTrue ? DecisionReason.NotRising : DecisionReason.AlreadyFired);
                    continue;
                }

                if (pass == MaxPasses)
                {
                    pending.Add(trigger.Id);
                    Observe(DecisionOutcome.Suppressed, DecisionReason.CascadeLimit);
                    continue;
                }

                log.Append(new TriggerFired(trigger.Id));
                EffectExecutor.ExecuteAll(trigger.Effects, log, _predicates);
                fired.Add(trigger.Id);
                firedThisPass = true;
                Observe(DecisionOutcome.Fired, DecisionReason.RisingEdge);

                void Observe(DecisionOutcome outcome, DecisionReason reason) => _observer?.Invoke(
                    new RuleDecision(DecisionKind.Trigger, trigger.Id, log.State.CurrentScene, outcome, reason,
                        trace, start, log.Events.Count, Pass: pass));
            }

            if (pass == MaxPasses)
            {
                if (pending.Count > 0)
                {
                    var firedIds = string.Join(", ", fired.Distinct(StringComparer.Ordinal));
                    throw new InvalidOperationException(
                        $"Trigger cascade did not settle after {MaxPasses} passes. " +
                        $"Fired: {firedIds}. Still eligible: {string.Join(", ", pending)}.");
                }

                return fired;
            }

            if (!firedThisPass)
            {
                return fired;
            }
        }

        throw new InvalidOperationException("Trigger cascade pass accounting failed.");
    }
}
