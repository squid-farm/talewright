using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Rules.Diagnostics;
using Talewright.Rules.Expressions;
using Talewright.Rules.Storylets;
using Talewright.Tale.Compilation;
using Talewright.Tale.Model;

namespace Talewright.Runtime.Tests;

public sealed class AuthorInspectionTests
{
    private static readonly EntityId Guide = new("guide");
    private static readonly StatId Strength = new("str");
    private static readonly VarId Count = new("count");
    private const string Yaml = """
        id: inspection
        title: Inspection
        start: start
        randomness: checks-only
        flags: [ready, unused]
        vars: {count: 0, untouched: 0}
        characters:
          player: {first-name: Mara, last-name: Vale, inventory: {rope: 1}}
          guide: {first-name: Robin, last-name: North, pronouns: they, stats: {str: 14}, inventory: {rope: 2}}
        items:
          rope: {name: Rope, modifiers: {str: 2}}
        factions:
          guild: {name: Guild, standing: -5}
        """;

    private static StoryDefinition Compile(string tale, string yaml = Yaml, NamedPredicateRegistry? predicates = null)
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = yaml,
            ["main.tale"] = tale,
        }), predicates is null ? null : new CompilerOptions { Predicates = predicates });
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.Story!;
    }

    private static AuthorSession Start(StoryDefinition story, bool capture = true)
        => AuthorSession.Create(story, story.Start, capture, seed: 42);

    [Theory]
    [InlineData(BinaryOp.And, false, 0)]
    [InlineData(BinaryOp.Or, true, 0)]
    [InlineData(BinaryOp.And, true, 1)]
    [InlineData(BinaryOp.Or, false, 1)]
    public void Expression_trace_preserves_short_circuit_and_opaque_predicate_calls(BinaryOp op, bool left, int expectedCalls)
    {
        var calls = 0;
        var predicates = new NamedPredicateRegistry().Register("Opaque", _ => { calls++; return true; });
        var story = Compile("=== start\n-> END");
        var context = new EvaluationContext(GameState.CreateInitial(story.World, 42), predicates);
        var expression = new BinaryExpr(op, new BoolLiteral(left),
            new UnaryExpr(UnaryOp.Not, new NamedPredicate("Opaque")));
        ExpressionTrace? observed = null;
        var value = ExpressionEvaluator.Evaluate(expression, context with { Trace = trace => observed = trace });
        Assert.Equal(expectedCalls, calls);
        Assert.NotNull(observed);
        Assert.Equal(value, observed.Result);
        Assert.Equal(2, observed.Children.Count);
        Assert.Equal(Value.Of(left), observed.Children[0].Result);
        var right = observed.Children[1];
        Assert.Equal(expectedCalls == 0, right.Result is null);
        var opaque = Assert.Single(right.Children);
        Assert.True(opaque.IsOpaque);
        Assert.Empty(opaque.Children);
        Assert.Equal(expectedCalls == 0, opaque.Result is null);
        var untraced = ExpressionEvaluator.Evaluate(expression, context);
        Assert.Equal(value, untraced);
        Assert.Equal(expectedCalls * 2, calls);
    }

    [Fact]
    public void False_comparison_retains_actual_effective_operands_and_skipped_predicates()
    {
        var calls = 0;
        var story = Compile("""
            === start
            * [Hidden] (requires: player:str >= 15 and @Never) -> END
            * [Wait] -> END
            """, predicates: new NamedPredicateRegistry().Register("Never", _ => { calls++; return true; }));
        var session = Start(story);
        var decision = Assert.Single(session.Inspect().FindDecisions("c0", DecisionKind.Choice));
        Assert.Equal(DecisionOutcome.Excluded, decision.Outcome);
        Assert.Equal(DecisionReason.ConditionFalse, decision.Reason);
        var trace = Assert.IsType<ExpressionTrace>(decision.Expression);
        var comparison = trace.Children[0];
        Assert.Equal(false, comparison.Result?.Bool);
        Assert.Equal(12, comparison.Children[0].Result?.Number);
        Assert.Equal(15, comparison.Children[1].Result?.Number);
        Assert.True(trace.Children[1].IsOpaque);
        Assert.Null(trace.Children[1].Result);
        Assert.Equal(0, calls);
        Assert.Equal(decision.EventStart, decision.EventEnd);
    }

    [Fact]
    public void Inspection_includes_all_characters_false_flags_zero_variables_and_unvisited_scenes()
    {
        var story = Compile("""
            === start
            * [Change] { set flag:ready; var:count += 1; take guide rope } -> next
            === next
            -> END
            """, Yaml + """

            triggers:
              - id: ready
                when: flag:ready
                do: var:count += 1
                once: true
            """);
        var session = Start(story);
        var before = session.Inspect();
        var guide = Assert.Single(before.FindCharacters("NORTH"));
        Assert.Equal(Guide, guide.Id);
        Assert.Equal("they", guide.Pronouns.Subject);
        var strength = Assert.Single(guide.Stats, stat => stat.Id == Strength);
        Assert.Equal(14, strength.BaseValue);
        Assert.Equal(16, strength.EffectiveValue);
        Assert.Equal(2, Assert.Single(guide.Inventory).Quantity);
        Assert.Equal(2, before.Characters.Count);
        Assert.All(before.Flags, flag => Assert.False(flag.Value));
        Assert.Equal(2, before.Flags.Count);
        Assert.All(before.Variables, variable => Assert.Equal(0, variable.Value));
        Assert.Equal(2, before.Variables.Count);
        Assert.Equal(-5, Assert.Single(before.Factions).Standing);
        Assert.NotNull(before.Factions[0].Tier);
        Assert.Equal(0, Assert.Single(before.Scenes, scene => scene.Id == new SceneId("next")).Visits);
        Assert.False(Assert.Single(before.Triggers).HasFired);
        Assert.True(before.DecisionsCaptured);
        Assert.False(before.IsEnded);
        Assert.Equal(42UL, before.Seed);
        Assert.Equal(0, before.RollCount);
        Assert.Throws<NotSupportedException>(() => ((IList<AuthorStatInspection>)guide.Stats).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<RuleDecision>)before.Decisions).Clear());
        session.Choose("c0");
        var after = session.Inspect();
        Assert.True(after.IsEnded);
        Assert.True(after.Triggers[0].HasFired);
        Assert.Equal(2, after.Variables.Single(v => v.Key == Count).Value);
        Assert.Equal(1, Assert.Single(after.FindCharacters("guide")).Inventory[0].Quantity);
        Assert.Equal(2, guide.Inventory[0].Quantity);
        Assert.False(before.Triggers[0].HasFired);
        Assert.All(before.Variables, variable => Assert.Equal(0, variable.Value));
        Assert.Equal("start", before.CurrentScene?.Value);
        Assert.Equal("next", after.CurrentScene?.Value);
    }

    [Theory]
    [InlineData("all(a,b)", "fourth")]
    [InlineData("any(a,b)", "third")]
    public void Hub_trace_explains_tags_self_once_requirements_priority_and_limit(string query, string next)
    {
        var neverCalls = 0;
        var story = Compile($$"""
            === start
            @hub {{query}}
            @hub-limit 2
            @tags a,b
            @priority 999
            * [Leave] -> END
            === blocked
            @tags a,b
            @priority 100
            @requires false
            -> start
            === wrong-tags
            @tags other
            @requires @Never
            -> start
            === first
            @tags a,b
            @priority 10
            @once
            -> start
            === second
            @tags a,b
            @priority 10
            -> start
            === third
            @tags a
            @priority 9
            -> start
            === fourth
            @tags a,b
            @priority 8
            -> start
            """, predicates: new NamedPredicateRegistry().Register("Never", _ => { neverCalls++; return true; }));
        var session = Start(story);
        var unobserved = Start(story, capture: false);
        Assert.Equal(session.Events, unobserved.Events);
        Assert.Equivalent(session.Current, unobserved.Current, strict: true);
        var hub = session.Inspect().FindDecisions(kind: DecisionKind.Storylet).ToArray();
        Assert.Equal(DecisionReason.SelfExcluded, Assert.Single(hub, d => d.Subject == "start").Reason);
        Assert.Equal(DecisionReason.TagMismatch, Assert.Single(hub, d => d.Subject == "wrong-tags").Reason);
        Assert.Null(Assert.Single(hub, d => d.Subject == "wrong-tags").Expression!.Result);
        Assert.Equal(DecisionReason.ConditionFalse, Assert.Single(hub, d => d.Subject == "blocked").Reason);
        Assert.Equal(false, Assert.Single(hub, d => d.Subject == "blocked").Expression?.Result?.Bool);
        Assert.Equal(["first", "second"], hub.Where(d => d.Outcome == DecisionOutcome.Selected).Select(d => d.Subject));
        var second = Assert.Single(hub, d => d.Subject == "second");
        Assert.Equal(2, second.Rank);
        Assert.Equal(10, second.Priority);
        Assert.Equal(2, second.Limit);
        Assert.Equal(DecisionReason.HubLimit, Assert.Single(hub, d => d.Subject == "fourth").Reason);
        Assert.Equal(0, neverCalls);

        session.Choose("storylet:first");
        session.Choose("continue");
        unobserved.Choose("storylet:first");
        unobserved.Choose("continue");
        Assert.Equal(session.Events, unobserved.Events);
        Assert.Equivalent(session.Current, unobserved.Current, strict: true);
        Assert.Equal(["c0", "storylet:second", $"storylet:{next}"], session.Current.Choices.Select(c => c.Key));
        Assert.Contains(session.Decisions, d => d.Subject == "first" && d.Reason == DecisionReason.AlreadyVisited);
        Assert.Equal(0, neverCalls);
    }

    [Fact]
    public void Selector_preserves_duplicate_removal_and_evaluates_eligible_requirements_before_limit()
    {
        var calls = 0;
        var predicates = new NamedPredicateRegistry().Register("Counted", _ => { calls++; return true; });
        var story = Compile("=== start\n-> END");
        var context = new EvaluationContext(GameState.CreateInitial(story.World, 42), predicates);
        static SceneDefinition Candidate(string id, int priority) => new()
        {
            Id = new(id), Tags = new HashSet<string> { "hub" }, Priority = priority, Requires = new NamedPredicate("Counted"),
        };
        var candidates = new[] { Candidate("a", 2), Candidate("a", 2), Candidate("b", 1), Candidate("c", 0) };
        var decisions = new List<RuleDecision>();
        var selected = StoryletSelector.SelectEligible(candidates, new StoryletTags(["hub"]), context,
            exclude: null, limit: 1, decisions.Add);
        Assert.Equal(4, calls);
        Assert.Equal("a", Assert.Single(selected).Id.Value);
        Assert.Equal(DecisionReason.Duplicate, decisions[1].Reason);
        Assert.Equal(DecisionReason.HubLimit, decisions[2].Reason);
        Assert.Equal(2, decisions[2].Rank);
        Assert.All(decisions, decision => Assert.Null(decision.EventStart));
        Assert.All(decisions, decision => Assert.True(decision.Expression!.IsOpaque));
        Assert.Equal(selected.Select(s => s.Id), StoryletSelector.SelectEligible(candidates, new StoryletTags(["hub"]),
            context, limit: 1).Select(s => s.Id));
        Assert.Equal(8, calls);
    }

    [Fact]
    public void Diverts_stop_at_first_match_and_do_not_apply_storylet_requires_to_explicit_targets()
    {
        var neverCalls = 0;
        var story = Compile("""
            === start
            {if false}
            -> wrong
            {/if}
            -> target
            {if @Never}
            -> wrong
            {/if}
            === target
            @requires false
            * [Finish] -> END
            === wrong
            -> END
            """, predicates: new NamedPredicateRegistry().Register("Never", _ => { neverCalls++; return true; }));
        var session = Start(story);
        var diverts = session.Decisions.Where(d => d.Kind == DecisionKind.Divert).ToArray();
        Assert.Equal(3, diverts.Length);
        Assert.Equal(DecisionOutcome.Excluded, diverts[0].Outcome);
        Assert.Equal(DecisionOutcome.Selected, diverts[1].Outcome);
        Assert.Equal(DecisionOutcome.NotEvaluated, diverts[2].Outcome);
        Assert.Equal(DecisionReason.EarlierDivert, diverts[2].Reason);
        Assert.Null(diverts[2].Expression!.Result);
        session.Choose("continue");
        Assert.Equal("target", session.Current.Scene?.Value);
        Assert.Equal("c0", Assert.Single(session.Current.Choices).Key);
        Assert.Equal(0, neverCalls);
    }

    [Fact]
    public void Available_choices_and_explicit_targets_explain_unevaluated_fallbacks()
    {
        var calls = 0;
        var story = Compile("""
            === start
            * [Next] -> target
            {if @Never}
            -> END
            {/if}
            === target
            -> END
            """, predicates: new NamedPredicateRegistry().Register("Never", _ => { calls++; return true; }));
        var session = Start(story);
        Assert.Contains(session.Decisions, d => d.Kind == DecisionKind.Divert && d.Reason == DecisionReason.ChoicesAvailable);
        session.Choose("c0");
        Assert.Contains(session.Decisions, d => d.Kind == DecisionKind.Divert && d.Reason == DecisionReason.ExplicitTarget);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(1, DecisionOutcome.Passed, DecisionReason.CheckSucceeded)]
    [InlineData(99, DecisionOutcome.Failed, DecisionReason.CheckFailed)]
    public void Entry_checks_capture_difficulty_roll_result_effect_range_and_skip_other_choices(
        int dc, DecisionOutcome outcome, DecisionReason reason)
    {
        var calls = 0;
        var story = Compile($$"""
            === start
            @hub hub
            * [Unused] (requires: @Never) -> END
            ::: skill-check athletics dc {{dc}}
            success: { var:count += 1 }
            failure: { var:count += 2 }
            :::
            -> target
            === target
            @tags hub
            @requires @Never
            -> END
            """, predicates: new NamedPredicateRegistry().Register("Never", _ => { calls++; return true; }));
        var session = Start(story);
        var check = Assert.Single(session.Decisions, d => d.Kind == DecisionKind.Check);
        Assert.Equal(outcome, check.Outcome);
        Assert.Equal(reason, check.Reason);
        Assert.Equal(dc, check.Expression?.Result?.Number);
        Assert.Equal(1, session.State.RollCount);
        var checkEvents = session.Events.Skip(check.EventStart!.Value)
            .Take(check.EventEnd!.Value - check.EventStart.Value).ToArray();
        Assert.IsType<DiceRolled>(checkEvents[0]);
        Assert.IsType<SkillCheckResolved>(checkEvents[1]);
        var effects = Assert.Single(session.Decisions, d => d.Kind == DecisionKind.CheckOutcome);
        Assert.Equal(reason, effects.Reason);
        Assert.Contains(session.Events.Skip(effects.EventStart!.Value)
            .Take(effects.EventEnd!.Value - effects.EventStart.Value), e => e is VariableChanged);
        Assert.Contains(session.Decisions, d => d.Kind == DecisionKind.Choice && d.Reason == DecisionReason.EntryCheck);
        Assert.Contains(session.Decisions, d => d.Kind == DecisionKind.Storylet && d.Reason == DecisionReason.EntryCheck);
        Assert.DoesNotContain(session.Decisions, d => d.Kind == DecisionKind.Divert && d.Outcome == DecisionOutcome.NotEvaluated);
        Assert.Contains(session.Decisions, d => d.Kind == DecisionKind.Divert && d.Outcome == DecisionOutcome.Selected);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Entry_effects_and_trigger_cascades_reference_actual_events_and_historical_conditions()
    {
        var story = Compile("""
            === start
            {if flag:ready}
            ~ var:count += 100
            {/if}
            ~ set flag:ready
            * [Lower] { clear flag:ready } -> start
            * [Wait] -> END
            """, Yaml + """

            triggers:
              - id: count
                when: flag:ready
                do: var:count += 1
                once: true
              - id: reward
                when: var:count >= 1
                do: give guide rope
                once: false
            """);
        var session = Start(story);
        var excluded = Assert.Single(session.Decisions, d => d.Kind == DecisionKind.EntryEffect && d.Subject == "entry:0");
        Assert.Equal(DecisionReason.ConditionFalse, excluded.Reason);
        Assert.Equal(false, excluded.Expression?.Result?.Bool);
        Assert.True(session.State.GetFlag(new FlagId("ready")));
        Assert.Equal(1, session.State.GetVariable(Count));
        Assert.Contains(session.Decisions, d => d.Kind == DecisionKind.Trigger && d.Outcome == DecisionOutcome.Primed);
        var fired = session.Decisions.Where(d => d.Outcome == DecisionOutcome.Fired).ToArray();
        Assert.Equal(["count", "reward"], fired.Select(d => d.Subject));
        foreach (var decision in fired)
        {
            Assert.Equal(0, decision.Pass);
            Assert.Equal(DecisionReason.RisingEdge, decision.Reason);
            var events = session.Events.Skip(decision.EventStart!.Value)
                .Take(decision.EventEnd!.Value - decision.EventStart.Value).ToArray();
            Assert.IsType<TriggerFired>(events[0]);
            Assert.Equal(2, events.Length);
        }
        Assert.Contains(session.Decisions, d => d.Reason == DecisionReason.NotRising);
        session.Choose("c0");
        Assert.Contains(session.Decisions, d => d.Reason == DecisionReason.AlreadyFired && d.Subject == "count");
        Assert.Equal(false, excluded.Expression?.Result?.Bool);
        Assert.Equal(1, session.State.GetVariable(Count));
        var checkpoint = session.Checkpoint!.Reenter();
        Assert.True(checkpoint.CaptureDecisions);
        Assert.Equal(session.Decisions.Select(Describe), checkpoint.Decisions.Select(Describe));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Observed_and_unobserved_normal_and_author_runs_have_identical_events_frames_and_predicate_calls(bool author)
    {
        var calls = 0;
        var story = Compile("""
            === start
            ~ var:count += 1
            * [Try] (requires: @Counted) skill-check athletics dc 12
              success: { set flag:ready } -> start
              failure: -> start
            * [End] -> END
            """, Yaml + """

            triggers:
              - id: counted
                when: "@Counted"
                do: var:count += 1
                once: true
            """, new NamedPredicateRegistry().Register("Counted", _ => { calls++; return true; }));
        (IReadOnlyList<GameEvent> Events, StoryFrame[] Frames, RuleDecision[] Decisions) Play(bool capture)
        {
            var frames = new List<StoryFrame>();
            var decisions = new List<RuleDecision>();
            if (author)
            {
                var session = Start(story, capture);
                frames.Add(session.Current);
                for (var i = 0; i < 3; i++)
                {
                    session.Choose("c0");
                    frames.Add(session.Current);
                }
                session.Choose("c1");
                frames.Add(session.Current);
                return (session.Events, frames.ToArray(), session.Decisions.ToArray());
            }
            var normal = new StorySession(story, 42, grammar: null, character: null, capture ? decisions.Add : null);
            frames.Add(normal.Start());
            for (var i = 0; i < 3; i++)
            {
                frames.Add(normal.Choose("c0"));
            }
            frames.Add(normal.Choose("c1"));
            return (normal.Events, frames.ToArray(), decisions.ToArray());
        }
        var without = Play(false);
        var expectedCalls = calls;
        calls = 0;
        var with = Play(true);
        Assert.Equal(expectedCalls, calls);
        Assert.Equal(without.Events, with.Events);
        Assert.Equivalent(without.Frames, with.Frames, strict: true);
        Assert.Empty(without.Decisions);
        Assert.NotEmpty(with.Decisions);
        Assert.Equal(3, with.Events.OfType<DiceRolled>().Count());
    }

    [Fact]
    public void Inspection_and_search_never_reevaluate_predicates_and_disabled_observation_is_explicit()
    {
        var calls = 0;
        var story = Compile("""
            === start
            * [Wait] (requires: @Counted) -> start
            """, predicates: new NamedPredicateRegistry().Register("Counted", _ => { calls++; return true; }));
        var session = Start(story);
        Assert.Equal(1, calls);
        var inspection = session.Inspect();
        Assert.Single(inspection.FindDecisions("C0", DecisionKind.Choice));
        Assert.Empty(inspection.FindDecisions("missing"));
        Assert.Single(inspection.FindCharacters("robin"));
        Assert.Empty(inspection.FindCharacters("missing"));
        session.Inspect();
        Assert.Equal(1, calls);
        var disabled = Start(story, capture: false).Inspect();
        Assert.False(disabled.DecisionsCaptured);
        Assert.Empty(disabled.Decisions);
        Assert.Equal(2, calls);
        session.JumpFresh(story.Start, seed: 42);
        Assert.True(session.CaptureDecisions);
        Assert.Single(session.Decisions);
    }

    [Fact]
    public void Failed_candidates_keep_the_previous_trace_and_propagate_extension_errors()
    {
        var story = Compile("""
            === start
            * [Break] -> bad
            === bad
            * [Hidden] (requires: @Broken) -> END
            """, predicates: new NamedPredicateRegistry().Register("Broken", _ => throw new InvalidOperationException("broken")));
        var session = Start(story);
        var decisions = session.Decisions;
        var inspection = session.Inspect();
        Assert.Equal("broken", Assert.Throws<InvalidOperationException>(() => session.Choose("c0")).Message);
        Assert.Same(decisions, session.Decisions);
        Assert.Equal(inspection.Events, session.Events);
        Assert.Equal(inspection.Decisions, session.Inspect().Decisions);
        Assert.Equal("broken", Assert.Throws<InvalidOperationException>(() => session.JumpFresh(new SceneId("bad"))).Message);
        Assert.Same(decisions, session.Decisions);
    }

    [Fact]
    public void Cascade_limit_is_observed_before_the_error_and_observer_errors_are_not_swallowed()
    {
        var triggers = string.Join("\n", Enumerable.Range(0, 40).Reverse().Select(i => $"""
              - id: chain-{i}
                when: flag:chain-{i}
                do: set flag:chain-{i + 1}
                once: true
            """));
        var story = Compile("=== start\n~ set flag:chain-0\n-> END", Yaml + "\ntriggers:\n" + triggers);
        var decisions = new List<RuleDecision>();
        var session = new StorySession(story, 42, grammar: null, character: null, decisions.Add);
        Assert.Contains("did not settle", Assert.Throws<InvalidOperationException>(() => session.Start()).Message);
        var limit = Assert.Single(decisions, d => d.Reason == DecisionReason.CascadeLimit);
        Assert.Equal("chain-32", limit.Subject);
        Assert.Equal(32, limit.Pass);
        Assert.Equal(DecisionOutcome.Suppressed, limit.Outcome);
        Assert.True(limit.Expression!.Result!.Value.Bool);
        Assert.Equal(limit.EventStart, limit.EventEnd);
        Assert.Equal(32, decisions.Count(d => d.Outcome == DecisionOutcome.Fired));

        var simple = Compile("=== start\n* [Wait] -> END");
        var failingObserver = new StorySession(simple, 42, grammar: null, character: null,
            observer: _ => throw new InvalidOperationException("observer failed"));
        Assert.Equal("observer failed", Assert.Throws<InvalidOperationException>(() => failingObserver.Start()).Message);
    }

    private static string Describe(RuleDecision decision) => $"{decision.Kind}|{decision.Subject}|{decision.Scene}|" +
        $"{decision.Outcome}|{decision.Reason}|{decision.EventStart}|{decision.EventEnd}|{decision.Pass}|" +
        DescribeExpression(decision.Expression);

    private static string DescribeExpression(ExpressionTrace? trace) => trace is null ? "none" :
        $"{trace.Expression}:{trace.Result}[{string.Join(",", trace.Children.Select(DescribeExpression))}]";
}
