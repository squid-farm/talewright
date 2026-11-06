using System.Text;
using Talewright.Core.Identity;
using Talewright.Core.Rulesets;
using Talewright.Rules.Checks;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;

namespace Talewright.Talemark.Tests;

/// <summary>
/// Player saves store hashes of these descriptions. A failure here means saved identities would change; keep the
/// old encoding or make an explicit, documented version decision instead of updating the expected value.
/// </summary>
public sealed class ChoiceReplayIdentityEncodingTests
{
    private static readonly EntityId Player = new("player");
    private static readonly StatId Hp = new("hp");
    private static readonly FactionId Guild = new("guild");
    private static readonly VarId Gold = new("gold");
    private static readonly VarId Nickname = new("nickname");
    private static readonly ItemId Torch = new("torch");
    private static readonly ItemId Rope = new("rope");

    public static TheoryData<string, Effect, string> Effects => new()
    {
        { "stat", new ChangeStat(Player, Hp, AssignOp.Subtract, new NumberLiteral(3)),
            """["stat","player","hp","Subtract",["number",3]]""" },
        { "standing", new ChangeStanding(Guild, AssignOp.Add, new NumberLiteral(5)),
            """["standing","guild","Add",["number",5]]""" },
        { "variable", new ChangeVariable(Gold, AssignOp.Set, new NumberLiteral(2)),
            """["variable","gold","Set",["number",2]]""" },
        { "text variable", new SetTextVariable(Nickname, new TextLiteral("Ace")),
            """["text-variable","nickname",["text","Ace"]]""" },
        { "flag", new SetFlag(new FlagId("door-open"), true), """["flag","door-open",true]""" },
        { "give", new GiveItem(Player, Torch, new NumberLiteral(2)), """["give-item","player","torch",["number",2]]""" },
        { "take", new TakeItem(Player, Rope, new NumberLiteral(1)), """["take-item","player","rope",["number",1]]""" },
    };

    public static TheoryData<string, Expr, string> Expressions => new()
    {
        { "number", new NumberLiteral(-4), """["number",-4]""" },
        { "bool", new BoolLiteral(false), """["bool",false]""" },
        { "escaped text", new TextLiteral("Joel \"big\" <&>"), """["text","Joel \u0022big\u0022 \u003C\u0026\u003E"]""" },
        { "tier", new TierLiteral(new ReputationTier("trusted", "Trusted", 10)), """["tier","trusted"]""" },
        { "stat", new StatRef(Player, Hp), """["stat-ref","player","hp"]""" },
        { "standing", new StandingRef(Guild), """["standing-ref","guild"]""" },
        { "standing tier", new StandingTierRef(Guild), """["standing-tier-ref","guild"]""" },
        { "flag", new FlagRef(new FlagId("door-open")), """["flag-ref","door-open"]""" },
        { "variable", new VarRef(Gold), """["variable-ref","gold"]""" },
        { "text variable", new TextVarRef(Nickname), """["text-variable-ref","nickname"]""" },
        { "name", new NameRef(Player, NamePart.Full), """["name-ref","player","Full"]""" },
        { "pronoun", new PronounRef(Player, PronounForm.PossessivePronoun),
            """["pronoun-ref","player","PossessivePronoun"]""" },
        { "faction name", new FactionNameRef(Guild), """["faction-name-ref","guild"]""" },
        { "item name", new ItemNameRef(Torch), """["item-name-ref","torch"]""" },
        { "has item", new HasItem(Player, Rope), """["has-item","player","rope"]""" },
        { "visited", new Visited(new SceneId("cave")), """["visited","cave"]""" },
        { "current scene visits", new SceneVisitCountRef(null), """["scene-visit-count-ref",null]""" },
        { "scene visits", new SceneVisitCountRef(new SceneId("cave")), """["scene-visit-count-ref","cave"]""" },
        { "predicate", new NamedPredicate("is-night"), """["predicate","is-night"]""" },
        { "unary", new UnaryExpr(UnaryOp.Negate, new VarRef(Gold)), """["unary","Negate",["variable-ref","gold"]]""" },
        { "binary", new BinaryExpr(BinaryOp.GreaterOrEqual, new StatRef(Player, Hp), new NumberLiteral(3)),
            """["binary","GreaterOrEqual",["stat-ref","player","hp"],["number",3]]""" },
    };

    [Theory]
    [MemberData(nameof(Effects))]
    public void Effect_descriptions_are_frozen(string name, Effect effect, string expected)
    {
        Assert.NotEmpty(name);
        Assert.Equal($"""["choice-replay-v1","start","finish",[{expected}],null]""",
            Describe(new SceneId("finish"), [effect], check: null));
    }

    [Theory]
    [MemberData(nameof(Expressions))]
    public void Expression_descriptions_are_frozen(string name, Expr expression, string expected)
    {
        Assert.NotEmpty(name);
        Assert.Equal($"""["choice-replay-v1","start","finish",[["text-variable","nickname",{expected}]],null]""",
            Describe(new SceneId("finish"), [new SetTextVariable(Nickname, expression)], check: null));
    }

    [Fact]
    public void Skill_check_and_end_descriptions_are_frozen()
    {
        var check = new CheckBranch(
            new SkillCheck(Player, new StatId("athletics"), new NumberLiteral(14)),
            new CheckOutcome([new SetFlag(new FlagId("climbed"), true)], new DivertTarget(new SceneId("top"))),
            new CheckOutcome([new ChangeStat(Player, Hp, AssignOp.Subtract, new NumberLiteral(2))],
                new DivertTarget(null)));

        Assert.Equal(
            """["choice-replay-v1","start",null,[],["player","athletics",["number",14],"""
            + """[[["flag","climbed",true]],"top"],[[["stat","player","hp","Subtract",["number",2]]],null]]]""",
            Describe(divert: null, [], check));
        Assert.Equal("""["choice-replay-v1","start",null,[],null]""", Describe(divert: null, [], check: null));
    }

    [Fact]
    public void Operator_and_reference_tokens_are_frozen()
    {
        Assert.Equal(["Set", "Add", "Subtract"], Enum.GetValues<AssignOp>().Select(ChoiceReplayIdentity.Token));
        Assert.Equal(["First", "Last", "Full"], Enum.GetValues<NamePart>().Select(ChoiceReplayIdentity.Token));
        Assert.Equal(["Subject", "Object", "Possessive", "PossessivePronoun", "Reflexive"],
            Enum.GetValues<PronounForm>().Select(ChoiceReplayIdentity.Token));
        Assert.Equal(["Not", "Negate"], Enum.GetValues<UnaryOp>().Select(ChoiceReplayIdentity.Token));
        Assert.Equal(
            ["Add", "Subtract", "Multiply", "Divide", "Equal", "NotEqual", "Less", "LessOrEqual", "Greater",
                "GreaterOrEqual", "And", "Or"],
            Enum.GetValues<BinaryOp>().Select(ChoiceReplayIdentity.Token));
    }

    [Fact]
    public void Every_built_in_effect_and_expression_type_has_a_frozen_description()
    {
        Assert.Equal(ConcreteTypes<Effect>(), Effects.Select(row => row[1].GetType().FullName!).ToHashSet());
        Assert.Equal(ConcreteTypes<Expr>(), Expressions.Select(row => row[1].GetType().FullName!).ToHashSet());
    }

    [Fact]
    public void Compiled_choice_identities_are_frozen()
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = """
                game: { id: replay, title: Replay, start: start }
                characters:
                  player: { first-name: Ada }
                vars: { gold: 10 }
                flags: [ready]
                """,
            ["main.tale"] = """
                === start
                * [Pay] { var:gold -= 2; set flag:ready } -> finish
                * [Climb] skill-check athletics dc 12
                  success: { var:gold += var:gold * 2 } -> finish
                  failure: { clear flag:ready } -> END
                === finish
                -> END
                """,
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var choices = result.Story!.GetScene(new SceneId("start")).Choices;

        Assert.Equal(["CCC3E9B409F6854D", "AB84B0E5AAEDE3E0"],
            choices.Select(choice => Convert.ToHexString(choice.ReplayIdentity!)));
    }

    private static string Describe(SceneId? divert, IReadOnlyList<Effect> effects, CheckBranch? check) =>
        Encoding.UTF8.GetString(ChoiceReplayIdentity.Describe(new SceneId("start"), divert, effects, check));

    private static HashSet<string> ConcreteTypes<T>() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => assembly.GetName().Name is { } name
                && name.StartsWith("Talewright.", StringComparison.Ordinal)
                && !name.EndsWith(".Tests", StringComparison.Ordinal))
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsAbstract: false, IsClass: true } && typeof(T).IsAssignableFrom(type))
            .Select(type => type.FullName!)
            .ToHashSet();
}
