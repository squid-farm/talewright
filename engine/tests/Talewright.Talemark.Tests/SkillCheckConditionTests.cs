using Talewright.Core.Identity;
using Talewright.Rules.Effects;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;
using Talewright.Testing;

namespace Talewright.Talemark.Tests;

public sealed class SkillCheckConditionTests
{
    private static readonly string Yaml = BuildYaml("true");

    private static string BuildYaml(string triggerCondition)
    {
        var manifest = new ManifestFaker("checks", "Checks", "start", 42) { BaseRuleset = "rpg" };
        manifest.Character("player", "Ada", stats: new Dictionary<string, int>
        {
            ["athletics"] = 2,
            ["perception"] = 1,
        });
        manifest.Flag("triggered");
        manifest.Trigger("invalid-condition", triggerCondition, EffectFaker.Set(new TaleFlagRef("triggered")));
        return manifest.Build();
    }

    private static CompilationResult Compile(string tale, string? yaml = null) =>
        StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = yaml ?? Yaml,
            ["main.tale"] = tale,
        }));

    [Fact]
    public void Parses_hidden_choice_entry_and_block_conditional_checks()
    {
        var result = Compile("""
            === start
            ::: skill-check perception dc 12, hidden
            success:
            failure:
            :::
            {if skill-check perception dc 12, hidden}
            You notice the mark.
            {else}
            The wall looks ordinary.
            {/if}
            * [Try the passage] skill-check perception dc 12, hidden
              success: -> success
              failure: -> failure
            === success
            -> END
            === failure
            -> END
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var scene = result.Story!.GetScene(new SceneId("start"));
        Assert.True(Assert.Single(scene.Steps.OfType<CheckStep>()).Check.Check.IsHidden);
        var conditional = Assert.IsType<CheckConditionalStep>(scene.Steps[1]);
        Assert.True(conditional.Check.IsHidden);
        Assert.IsType<ProseStep>(Assert.Single(conditional.Then));
        Assert.IsType<ProseStep>(Assert.Single(conditional.Else));
        Assert.True(Assert.Single(scene.Steps.OfType<ChoiceStep>()).Choice.Check!.Check.IsHidden);
    }

    [Fact]
    public void Skill_check_condition_can_enclose_choices_after_the_initial_choice()
    {
        var result = Compile("""
            === start
            * [Always available] -> END
            {if skill-check athletics dc 12}
            * [On success] -> END
            {else}
            * [On failure] -> END
            {/if}
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var diverts = StoryDiverts.From(result.Story!).Diverts.Where(divert => divert.From.Value == "start").ToArray();
        Assert.Equal(3, diverts.Length);
        Assert.Empty(diverts[0].CheckBlockConditions);
        Assert.Equal(SkillCheckOutcome.Success, Assert.Single(diverts[1].CheckBlockConditions).Outcome);
        Assert.Equal(SkillCheckOutcome.Failure, Assert.Single(diverts[2].CheckBlockConditions).Outcome);
    }

    [Fact]
    public void Skill_check_condition_allows_nested_conditions_and_an_omitted_else()
    {
        var result = Compile("""
            === start
            {if skill-check perception dc 12}
            {if true}
            You notice the mark.
            {/if}
            {/if}
            * [Continue] -> END
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var scene = result.Story!.GetScene(new SceneId("start"));
        var check = Assert.IsType<CheckConditionalStep>(scene.Steps[0]);
        Assert.Empty(check.Else);
        Assert.IsType<ConditionalStep>(Assert.Single(check.Then));
    }

    [Theory]
    [InlineData("and true")]
    [InlineData("or true")]
    [InlineData("not true")]
    public void Skill_check_conditions_cannot_be_combined_with_boolean_operators(string suffix)
    {
        var result = Compile($$"""
            === start
            {if skill-check perception dc 12 {{suffix}}}
            Detected.
            {/if}
            * [Continue] -> END
            """);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
    }

    [Theory]
    [InlineData("""
        === start
        Text {if skill-check perception dc 12}detected{/if}.
        * [Continue] -> END
        """)]
    [InlineData("""
        === start
        * [Continue] (requires skill-check perception dc 12) -> END
        """)]
    [InlineData("""
        === start
        @requires skill-check perception dc 12
        * [Continue] -> END
        """)]
    public void Skill_check_conditions_are_rejected_outside_block_if(string tale)
    {
        var result = Compile(tale);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Trigger_when_rejects_skill_check_conditions()
    {
        var result = Compile("""
            === start
            * [Continue] -> END
            """, BuildYaml("skill-check perception dc 12"));

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
    }
}
