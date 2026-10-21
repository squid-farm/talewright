using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Core.World;
using Talewright.Rules.Checks;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Rules.Storylets;
using Talewright.Rules.Triggers;
using Talewright.Rulesets.Generic;

namespace Talewright.Rules.Tests;

public class RulesTests
{
    private static readonly FactionId Guild = new("guild");
    private static readonly ItemId Rope = new("rope");
    private static readonly StatId Athletics = new("athletics");

    private static EventLog NewLog(ulong seed = 7) => new(
        new WorldDefinition(
            GenericRuleset.Create(),
            [new CharacterDefinition(EntityId.Player, "Mara", "Burns", Pronouns.She,
                new Dictionary<StatId, int> { [GenericRuleset.Strength] = 14, [Athletics] = 2 },
                new Dictionary<ItemId, int> { [Rope] = 1 })],
            [new ItemDefinition(Rope, "coil of rope", new Dictionary<StatId, int>())],
            [new FactionDefinition(Guild, "Explorers' Guild", 0)],
            [new FlagId("door-open")],
            new Dictionary<VarId, int>()),
        seed);

    private static Value Eval(Expr expr, EventLog log) => ExpressionEvaluator.Evaluate(expr, EvaluationContext.For(log.State));

    [Fact]
    public void Evaluates_references_arithmetic_and_logic()
    {
        var log = NewLog();
        var expr = new BinaryExpr(BinaryOp.And,
            new BinaryExpr(BinaryOp.GreaterOrEqual,
                new BinaryExpr(BinaryOp.Add, new StatRef(EntityId.Player, GenericRuleset.Strength), new NumberLiteral(1)),
                new NumberLiteral(15)),
            new HasItem(EntityId.Player, Rope));

        Assert.True(Eval(expr, log).Bool);
        Assert.Equal("her", Eval(new PronounRef(EntityId.Player, PronounForm.Possessive), log).Text);
        Assert.Equal("Mara Burns", Eval(new NameRef(EntityId.Player, NamePart.Full), log).Text);
    }

    [Fact]
    public void Tier_comparisons_use_tier_order()
    {
        var log = NewLog();
        var friendly = log.State.Ruleset.FindTier("friendly")!;
        var atLeastFriendly = new BinaryExpr(BinaryOp.GreaterOrEqual, new StandingTierRef(Guild), new TierLiteral(friendly));

        Assert.False(Eval(atLeastFriendly, log).Bool);
        EffectExecutor.Execute(new ChangeStanding(Guild, AssignOp.Add, new NumberLiteral(60)), log, NamedPredicateRegistry.Empty);
        Assert.True(Eval(atLeastFriendly, log).Bool);
        Assert.Equal("Allied", Eval(new StandingTierRef(Guild), log).ToString());
    }

    [Fact]
    public void Named_predicates_call_registered_csharp()
    {
        var log = NewLog();
        var registry = new NamedPredicateRegistry().Register("IsStrong", s => s.GetStat(EntityId.Player, GenericRuleset.Strength) > 12);

        Assert.True(ExpressionEvaluator.IsTrue(new NamedPredicate("IsStrong"), new EvaluationContext(log.State, registry)));
    }

    [Fact]
    public void Effects_run_sequentially_and_emit_events()
    {
        var log = NewLog();

        EffectExecutor.ExecuteAll(
        [
            new ChangeStat(EntityId.Player, GenericRuleset.HitPoints, AssignOp.Subtract, new NumberLiteral(3)),
            new ChangeStat(EntityId.Player, GenericRuleset.HitPoints, AssignOp.Subtract, new NumberLiteral(3)),
            new TakeItem(EntityId.Player, Rope, new NumberLiteral(1)),
            new SetFlag(new FlagId("door-open"), true),
        ], log, NamedPredicateRegistry.Empty);

        Assert.Equal(4, log.State.GetStat(EntityId.Player, GenericRuleset.HitPoints));
        Assert.False(log.State.GetItemCount(EntityId.Player, Rope) > 0);
        Assert.True(log.State.GetFlag(new FlagId("door-open")));
        Assert.Equal(4, log.Events.Count);
    }

    private sealed record TestStorylet(string Name, Expr? Requires, int Priority = 0, bool Once = false) : IStorylet
    {
        public SceneId Id => new(Name);
        public IReadOnlySet<string> Tags { get; } = new HashSet<string> { "hub" };
    }

    [Fact]
    public void Storylets_are_filtered_by_requirements_once_and_sorted_by_priority()
    {
        var log = NewLog();
        TestStorylet[] storylets =
        [
            new("low", null),
            new("high", null, Priority: 5),
            new("blocked", new BoolLiteral(false)),
            new("one-shot", null, Once: true),
        ];
        log.Append(new SceneEntered(new SceneId("one-shot")));

        var eligible = StoryletSelector.SelectEligible(storylets, "hub", EvaluationContext.For(log.State));

        Assert.Equal(["high", "low"], eligible.Select(s => s.Name));
    }

    [Fact]
    public void Triggers_fire_on_rising_edge_and_once_triggers_fire_once()
    {
        var log = NewLog();
        var hurt = new BinaryExpr(BinaryOp.Less, new StatRef(EntityId.Player, GenericRuleset.HitPoints), new NumberLiteral(5));
        var engine = new TriggerEngine(
        [
            new TriggerDefinition("wounded", hurt, [new ChangeVariable(new VarId("wounds"), AssignOp.Add, new NumberLiteral(1))], Once: false),
            new TriggerDefinition("first-blood", hurt, [new SetFlag(new FlagId("bloodied"), true)], Once: true),
        ], NamedPredicateRegistry.Empty, log.State);

        void SetHp(int hp)
        {
            EffectExecutor.Execute(new ChangeStat(EntityId.Player, GenericRuleset.HitPoints, AssignOp.Set, new NumberLiteral(hp)), log, NamedPredicateRegistry.Empty);
            engine.Run(log);
        }

        SetHp(3);
        SetHp(2); // still true: no new edge
        SetHp(9);
        SetHp(1); // new edge

        Assert.Equal(2, log.State.GetVariable(new VarId("wounds")));
        Assert.Single(log.Events.OfType<TriggerFired>(), e => e.TriggerId == "first-blood");
    }

    [Fact]
    public void Triggers_cascade_within_a_single_run()
    {
        var log = NewLog();
        var alarm = new FlagId("alarm");
        var engine = new TriggerEngine(
        [
            new TriggerDefinition("guards-alerted", new FlagRef(alarm), [new ChangeStanding(Guild, AssignOp.Subtract, new NumberLiteral(50))], Once: true),
            new TriggerDefinition("noisy", new BinaryExpr(BinaryOp.Less, new StatRef(EntityId.Player, GenericRuleset.HitPoints), new NumberLiteral(5)), [new SetFlag(alarm, true)], Once: true),
        ], NamedPredicateRegistry.Empty, log.State);
        EffectExecutor.Execute(new ChangeStat(EntityId.Player, GenericRuleset.HitPoints, AssignOp.Set, new NumberLiteral(1)), log, NamedPredicateRegistry.Empty);

        var fired = engine.Run(log);

        Assert.Equal(["noisy", "guards-alerted"], fired);
        Assert.Equal(-50, log.State.GetStanding(Guild));
    }

    [Fact]
    public void Skill_checks_roll_seeded_dice_and_record_events()
    {
        var check = new SkillCheck(EntityId.Player, Athletics, new NumberLiteral(14));
        var first = NewLog(seed: 123);
        var second = NewLog(seed: 123);

        var a = SkillCheckResolver.Resolve(check, first, RandomnessMode.ChecksOnly, NamedPredicateRegistry.Empty);
        var b = SkillCheckResolver.Resolve(check, second, RandomnessMode.ChecksOnly, NamedPredicateRegistry.Empty);

        Assert.Equal(a, b);
        Assert.Equal(4, a.Modifier); // +2 STR mod, +2 ranks
        Assert.IsType<DiceRolled>(first.Events[0]);
        Assert.IsType<SkillCheckResolved>(first.Events[1]);
        Assert.Equal(1, first.State.RollCount);
    }

    [Fact]
    public void Skill_checks_without_randomness_take_ten()
    {
        var log = NewLog();

        var result = SkillCheckResolver.Resolve(new SkillCheck(EntityId.Player, Athletics, new NumberLiteral(14)), log, RandomnessMode.Off, NamedPredicateRegistry.Empty);

        Assert.True(result.Success); // 10 + 4
        Assert.Empty(log.Events.OfType<DiceRolled>());
    }
}
