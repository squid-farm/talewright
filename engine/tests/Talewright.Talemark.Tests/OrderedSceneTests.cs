using Talewright.Rules.Expressions;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;

namespace Talewright.Talemark.Tests;

public sealed class OrderedSceneTests
{
    private static CompilationResult Compile(string tale) => StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
    {
        ["story.talewright.yaml"] = """
            game: {id: order, title: Order, start: start}
            characters:
              player: {first-name: Ada}
            vars: {gold: 10}
            flags: [ready]
            """,
        ["chapter.tale"] = tale,
    }));

    [Fact]
    public void Model_preserves_order_batches_nested_branches_and_source_spans()
    {
        const string tale = """
            === start
            Before.
            ~ var:gold -= 10; var:gold += 7
            {if not flag:ready}
            ~ set flag:ready
            Inside.
            {else}
            Otherwise.
            {/if}
            ::: skill-check athletics dc 1
            success: { var:gold += 1 }
            failure: { var:gold -= 1 }
            :::
            * [Leave] -> END
            """;
        var result = Compile(tale);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var steps = result.Story!.Scenes[0].Steps;
        Assert.Collection(steps,
            step => Assert.IsType<ProseStep>(step),
            step => Assert.Equal(2, Assert.IsType<EffectStep>(step).Effects.Count),
            step =>
            {
                var branch = Assert.IsType<ConditionalStep>(step);
                Assert.IsType<UnaryExpr>(branch.Condition);
                Assert.Collection(branch.Then, child => Assert.IsType<EffectStep>(child), child => Assert.IsType<ProseStep>(child));
                Assert.IsType<ProseStep>(Assert.Single(branch.Else));
                Assert.Equal(9, branch.Source.End.Line);
            },
            step =>
            {
                var check = Assert.IsType<CheckStep>(step);
                Assert.Equal(10, check.Source.Start.Line);
                Assert.Equal(13, check.Source.End.Line);
            },
            step => Assert.Equal("c0", Assert.IsType<ChoiceStep>(step).Choice.Key));
        var flattened = SceneSteps.Walk(steps).ToArray();
        Assert.Equal(flattened.Length, flattened.Select(step => step.Id).Distinct().Count());
        Assert.All(flattened, step =>
        {
            Assert.Equal("chapter.tale", step.Source.Start.File);
            Assert.Equal($"step:{step.Source.Start.Line}:{step.Source.Start.Column}", step.Id);
            Assert.Equal("chapter.tale", step.Source.End.File);
        });
        Assert.Equal(flattened.Select(step => step.Id),
            SceneSteps.Walk(Compile(tale).Story!.Scenes[0].Steps).Select(step => step.Id));
    }

    [Theory]
    [InlineData("Prose.")]
    [InlineData("~ var:gold += 1")]
    [InlineData("-> END")]
    [InlineData("::: skill-check athletics dc 1\nsuccess: -> END\nfailure: -> END\n:::")]
    [InlineData("{if true}\nProse.\n{/if}")]
    public void Non_choice_steps_after_the_first_choice_are_errors(string following)
    {
        var result = Compile("=== start\n* [Leave] -> END\n" + following);
        Assert.Contains(result.Errors, diagnostic => diagnostic.Code == "TW3011" && diagnostic.Location.Line >= 3);
    }

    [Theory]
    [InlineData("Prose.")]
    [InlineData("~ var:gold += 1")]
    [InlineData("-> END")]
    public void Else_branches_cannot_hide_steps_after_an_earlier_choice(string following)
    {
        var result = Compile($"=== start\n{{if flag:ready}}\n* [Leave] -> END\n{{else}}\n{following}\n{{/if}}");
        Assert.Contains(result.Errors, diagnostic => diagnostic.Code == "TW3011" && diagnostic.Location.Line == 5);
    }

    [Theory]
    [InlineData("* [Leave]")]
    [InlineData("* [Leave] { var:gold += 1 }")]
    [InlineData("* [Try] skill-check athletics dc 1\nsuccess: -> END\nfailure: { var:gold += 1 }")]
    public void Choices_require_their_own_targets_even_if_a_scene_divert_exists(string choice)
    {
        var result = Compile("=== start\n-> END\n" + choice);
        Assert.Contains(result.Errors, diagnostic => diagnostic.Code == "TW3003" && diagnostic.Location.Line == 3);
    }

    [Fact]
    public void Nested_choice_keys_remain_source_ordered_without_flattening_conditions()
    {
        var result = Compile("""
            === start
            {if flag:ready}
            * [One] -> END
            {if true}
            * [Two] -> END
            {/if}
            {else}
            * [Three] -> END
            {/if}
            // A comment does not interrupt the final block.
            * [Four] -> END
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Equal(["c0", "c1", "c2", "c3"], result.Story!.Scenes[0].Choices.Select(choice => choice.Key));
        Assert.All(result.Story.Scenes[0].Choices, choice => Assert.Null(choice.Requires));
    }

    [Fact]
    public void Conditional_and_multiple_entry_checks_can_continue_to_later_steps()
    {
        var result = Compile("""
            === start
            ::: skill-check athletics dc 1
            success: { set flag:ready }
            failure: { clear flag:ready }
            :::
            {if flag:ready}
            ::: skill-check athletics dc 2
            success: { var:gold += 1 }
            failure: { var:gold -= 1 }
            :::
            {/if}
            After both.
            -> END
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Equal(2, SceneSteps.Walk(result.Story!.Scenes[0].Steps).OfType<CheckStep>().Count());
    }

    [Fact]
    public void Check_without_a_target_and_no_later_exit_is_a_dead_end()
    {
        var result = Compile("=== start\n::: skill-check athletics dc 1\nsuccess:\nfailure:\n:::");
        Assert.Contains(result.Errors, diagnostic => diagnostic.Code == "TW3002");
    }

    [Fact]
    public void Shared_flow_walk_excludes_unreachable_targets_and_preserves_possible_branches()
    {
        var result = Compile("""
            === start
            {if flag:ready}
            -> left
            -> impossible
            {else}
            -> right
            {/if}
            -> impossible
            === left
            -> END
            === right
            -> END
            === impossible
            -> END
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Equal(["left", "right"], SceneSteps.Targets(result.Story!.Scenes[0].Steps).Select(target => target.ToString()));
        Assert.Equal([4, 8], result.Warnings.Where(diagnostic => diagnostic.Code == "TW3012").Select(diagnostic => diagnostic.Location.Line));
        Assert.Contains(result.Warnings, diagnostic => diagnostic.Code == "TW3009" && diagnostic.Message.Contains("'impossible'"));
        Assert.DoesNotContain(result.Warnings, diagnostic => diagnostic.Code == "TW3009" &&
            (diagnostic.Message.Contains("'left'") || diagnostic.Message.Contains("'right'")));
    }
}
