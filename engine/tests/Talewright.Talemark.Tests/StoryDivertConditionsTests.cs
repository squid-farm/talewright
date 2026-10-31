using Talewright.Rules.Expressions;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;
using Talewright.Testing;

namespace Talewright.Talemark.Tests;

public sealed class StoryDivertConditionsTests
{
    private const string Manifest = """
        game: { id: conditions, title: Conditions, start: start }
        characters:
          player: { first-name: Mara, last-name: Burns, pronouns: she }
        factions:
          river: { name: River Guild, standing: 12 }
        items:
          lantern: { name: Lantern }
        flags: [ready]
        vars:
          coins: 5
          weather: "rain"
        """;

    private static readonly CompilerOptions Options = new()
    {
        Predicates = new NamedPredicateRegistry().Register("CanGo", _ => true),
    };

    private static CompilationResult Compile(string tale)
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = Manifest,
            ["main.tale"] = tale,
        }), Options);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result;
    }

    private static Expr ParseCondition(string text)
    {
        var result = Compile($"""
            === start
            @requires {text}
            -> END
            === destination
            -> END
            """);
        return result.Story!.Scenes[0].Requires!;
    }

    [Fact]
    public void Divert_conditions_keep_nested_parts_negate_else_and_append_choice_requires()
    {
        var result = Compile("""
            === start
            {if flag:ready}
            {if var:coins > 1}
            ::: skill-check athletics dc 5
            success: -> destination
            failure: { set flag:ready }
            :::
            {else}
            -> elsewhere
            {/if}
            {else}
            -> elsewhere
            {/if}
            === choose
            {if flag:ready or var:coins > 1}
            * [Climb] (requires: player:dex >= 10) skill-check athletics dc 10
            success: -> destination
            failure: -> elsewhere
            {/if}
            === destination
            -> END
            === elsewhere
            -> END
            """);

        var diverts = StoryDiverts.From(result.Story!).Diverts;
        var entryOutcome = Assert.Single(diverts, divert => divert.Kind == DivertKind.EntryCheck);
        Assert.Equal(["flag:ready", "var:coins > 1"], entryOutcome.Condition.Parts);
        Assert.Equal("flag:ready and var:coins > 1", entryOutcome.Condition.Text);
        Assert.Equal(["flag:ready", "var:coins"], entryOutcome.Condition.Reads.Select(read => read.Reference));

        var nestedElse = Assert.Single(diverts, divert =>
            divert.From.Value == "start" && divert.Target.Scene?.Value == "elsewhere" &&
            divert.BlockConditions.Count == 2);
        Assert.Equal(["flag:ready", "not var:coins > 1"], nestedElse.Condition.Parts);
        Assert.Equal("flag:ready and not var:coins > 1", nestedElse.Condition.Text);

        var outerElse = Assert.Single(diverts, divert =>
            divert.From.Value == "start" && divert.Target.Scene?.Value == "elsewhere" &&
            divert.BlockConditions.Count == 1);
        Assert.Equal(["not flag:ready"], outerElse.Condition.Parts);

        var choiceOutcomes = diverts.Where(divert => divert.Kind == DivertKind.ChoiceSkillCheck).ToArray();
        Assert.Equal(2, choiceOutcomes.Length);
        Assert.All(choiceOutcomes, divert =>
        {
            Assert.Equal(["flag:ready or var:coins > 1", "player:dex >= 10"], divert.Condition.Parts);
            Assert.Equal("(flag:ready or var:coins > 1) and player:dex >= 10", divert.Condition.Text);
        });

        foreach (var divert in diverts.Where(divert => divert.Condition.Parts.Count > 0))
        {
            var expectedParts = divert.BlockConditions.Select(block => block.InElse
                    ? new UnaryExpr(UnaryOp.Not, block.Condition)
                    : block.Condition)
                .Concat(divert.ChoiceRequires is { } requires ? [requires] : []);
            Assert.Equal(expectedParts, divert.Condition.Parts.Select(ParseCondition));

            var combined = expectedParts.Skip(1).Aggregate(expectedParts.First(),
                (left, right) => (Expr)new BinaryExpr(BinaryOp.And, left, right));
            Assert.Equal(combined, ParseCondition(divert.Condition.Text));
        }
    }

    [Fact]
    public void Hub_offers_expose_the_offered_scenes_requirement_once_flag_and_reads()
    {
        var result = Compile("""
            === start
            @hub offer
            * -> END
            === second-hub
            @tags offer
            @hub offer
            * -> END
            === destination
            @tags offer
            @requires flag:ready and @CanGo
            @once
            -> END
            """);

        var offers = StoryDiverts.From(result.Story!).HubOffers;
        var destinationOffers = offers.Where(offer => offer.Scene.Value == "destination").ToArray();
        var secondHubOffer = Assert.Single(offers, offer => offer.Scene.Value == "second-hub");

        Assert.Equal(2, destinationOffers.Length);
        Assert.All(destinationOffers, offer =>
        {
            Assert.Equal(["flag:ready and @CanGo"], offer.Condition.Parts);
            Assert.Equal("flag:ready and @CanGo", offer.Condition.Text);
            Assert.True(offer.Once);
            Assert.Equal(["flag:ready", "@CanGo"], offer.Condition.Reads.Select(read => read.Reference));
            Assert.Equal(result.Story!.GetScene(offer.Scene).Requires, ParseCondition(offer.Condition.Parts[0]));
        });
        Assert.Empty(secondHubOffer.Condition.Parts);
        Assert.False(secondHubOffer.Once);
        Assert.Same(destinationOffers[0].Condition, destinationOffers[1].Condition);
        Assert.Equal(StoryStateReadKind.NamedPredicate,
            Assert.Single(destinationOffers[0].Condition.Reads, read => read.IsOpaque).Kind);
    }

    [Fact]
    public void Every_expression_read_kind_is_reported_once_in_first_occurrence_order()
    {
        var result = Compile("""
            === start
            @hub offer
            * -> END
            === destination
            @tags offer
            @requires player:dex >= 10 and faction:river >= 1 and faction:river:tier == faction:river:tier and flag:ready and var:coins > 0 and var:weather == "rain" and player:first-name == "Mara" and player:pronoun:object == "her" and faction:river:name == "River Guild" and item:lantern == "Lantern" and player has lantern and visited(destination) and @CanGo and flag:ready
            @once
            -> END
            """);

        var offer = Assert.Single(StoryDiverts.From(result.Story!).HubOffers);
        Assert.Equal(result.Story!.GetScene(offer.Scene).Requires, ParseCondition(Assert.Single(offer.Condition.Parts)));
        Assert.Equal(
            [
                (StoryStateReadKind.Stat, "player:dex"),
                (StoryStateReadKind.Standing, "faction:river"),
                (StoryStateReadKind.StandingTier, "faction:river:tier"),
                (StoryStateReadKind.Flag, "flag:ready"),
                (StoryStateReadKind.NumberVariable, "var:coins"),
                (StoryStateReadKind.TextVariable, "var:weather"),
                (StoryStateReadKind.CharacterName, "player:first-name"),
                (StoryStateReadKind.CharacterPronoun, "player:pronoun:object"),
                (StoryStateReadKind.FactionName, "faction:river:name"),
                (StoryStateReadKind.ItemName, "item:lantern"),
                (StoryStateReadKind.ItemsHeld, "player has lantern"),
                (StoryStateReadKind.VisitedScene, "visited(destination)"),
                (StoryStateReadKind.NamedPredicate, "@CanGo"),
            ],
            offer.Condition.Reads.Select(read => (read.Kind, read.Reference)));
        Assert.Equal(1, offer.Condition.Reads.Count(read => read.Reference == "flag:ready"));
        Assert.All(offer.Condition.Reads.Where(read => read.Kind != StoryStateReadKind.NamedPredicate),
            read => Assert.False(read.IsOpaque));
        Assert.All(offer.Condition.Reads.Where(read => read.Kind == StoryStateReadKind.NamedPredicate),
            read => Assert.True(read.IsOpaque));
    }
}
