using System.Text;
using Talewright.Core;
using Talewright.Core.Identity;
using Talewright.Core.Randomness;
using Talewright.Core.Rulesets;
using Talewright.Core.State;
using Talewright.Core.World;
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
        { "held item property", new ChangeItemProperty(Player, new ItemId("sword"), "sharpness",
            AssignOp.Add, new NumberLiteral(2)),
            """["item-property","player","sword","sharpness","Add",["number",2]]""" },
        { "give", new GiveItem(Player, Torch, new NumberLiteral(2)), """["give-item","player","torch",["number",2]]""" },
        { "take", new TakeItem(Player, Rope, new NumberLiteral(1)), """["take-item","player","rope",["number",1]]""" },
    };

    public static TheoryData<string, Expr, string> Expressions => new()
    {
        { "number", new NumberLiteral(-4), """["number",-4]""" },
        { "fractional number", new NumberLiteral(ExactNumber.FromFraction(1, 4)),
            """["number","1/4"]""" },
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
        { "item variable comparison", new ItemComparison(new TextVarRef(new VarId("chosen")), BinaryOp.Equal, Rope),
            """["item-comparison",["resolved-item",["text-variable-ref","chosen"]],"Equal","rope"]""" },
        { "item variable property", new ItemReferencePropertyRef(new TextVarRef(new VarId("chosen")), "weight",
            ItemPropertyValueKind.Number),
            """["item-reference-property-ref",["resolved-item",["text-variable-ref","chosen"]],"weight","number"]""" },
        { "item property", new ItemPropertyRef(Rope, "weight",
            new ItemPropertyValue.Number(ExactNumber.FromFraction(5, 4))),
            """["item-property-ref","rope","weight",["number","5/4"]]""" },
        { "has item", new HasItem(Player, Rope), """["has-item","player","rope"]""" },
        { "visited", new Visited(new SceneId("cave")), """["visited","cave"]""" },
        { "current scene visits", new SceneVisitCountRef(null), """["scene-visit-count-ref",null]""" },
        { "bound scene alias", new SceneVisitCountRef(new SceneId("cave"), true),
            """["scene-visit-count-ref",null]""" },
        { "scene visits", new SceneVisitCountRef(new SceneId("cave")), """["scene-visit-count-ref","cave"]""" },
        { "inventory count", new InventoryCountRef(Player, Torch),
            """["inventory-count-ref","player","torch"]""" },
        { "held item property", new InventoryItemPropertyRef(Player, new ItemId("sword"), "sharpness",
            new ItemPropertyValue.Number(ExactNumber.FromInteger(5))),
            """["inventory-item-property-ref","player","sword","sharpness",["number",5]]""" },
        { "predicate", new NamedPredicate("is-night"), """["predicate","is-night"]""" },
        { "skill check", new SkillCheckExpr(Player, new StatId("athletics"),
            new SkillCheckIdentity(new SceneId("start"), Player, new StatId("athletics"), 0),
            new NumberLiteral(14), IsHidden: true),
            """["skill-check-expr","player","athletics","start",0,true,["number",14]]""" },
        { "unary", new UnaryExpr(UnaryOp.Negate, new VarRef(Gold)), """["unary","Negate",["variable-ref","gold"]]""" },
        { "round", new UnaryExpr(UnaryOp.Round, new VarRef(Gold)), """["unary","Round",["variable-ref","gold"]]""" },
        { "round up", new UnaryExpr(UnaryOp.RoundUp, new VarRef(Gold)), """["unary","RoundUp",["variable-ref","gold"]]""" },
        { "round down", new UnaryExpr(UnaryOp.RoundDown, new VarRef(Gold)), """["unary","RoundDown",["variable-ref","gold"]]""" },
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
                new DivertTarget(null)),
            new(new SceneId("start"), Player, new StatId("athletics"), 0));

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
        Assert.Equal(["Not", "Negate", "Round", "RoundUp", "RoundDown"],
            Enum.GetValues<UnaryOp>().Select(ChoiceReplayIdentity.Token));
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
                game: { id: replay, title: Replay, start: start, ruleset: rpg }
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

    [Fact]
    public void Item_property_values_participate_in_choice_quantity_and_check_difficulty_identity()
    {
        static string[] Compile(int price, int difficulty)
        {
            var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
            {
                [StorySource.ManifestFileName] = $$"""
                    game: { id: replay, title: Replay, start: start, talemark: 2, ruleset: rpg }
                    characters:
                      player: { first-name: Ada }
                    items:
                      rope:
                        price: {{price}}
                        difficulty: {{difficulty}}
                    """,
                ["main.tale"] = """
                    === start
                    * [Buy] {take player rope item:rope:price} -> finish
                    * [Check] skill-check athletics dc item:rope:difficulty
                      success: -> finish
                      failure: -> END
                    === finish
                    -> END
                    """,
            }));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            return result.Story!.GetScene(new SceneId("start")).Choices
                .Select(choice => Convert.ToHexString(choice.ReplayIdentity!)).ToArray();
        }

        var baseline = Compile(price: 2, difficulty: 12);
        var changedPrice = Compile(price: 3, difficulty: 12);
        var changedDifficulty = Compile(price: 2, difficulty: 13);

        Assert.NotEqual(baseline[0], changedPrice[0]);
        Assert.Equal(baseline[1], changedPrice[1]);
        Assert.Equal(baseline[0], changedDifficulty[0]);
        Assert.NotEqual(baseline[1], changedDifficulty[1]);
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
