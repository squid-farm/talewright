using Talewright.Talemark.Compilation;
using Talewright.Testing;

namespace Talewright.Talemark.Tests;

public sealed class ChoiceAvailabilityTests
{
    private static CompilationResult Compile(string body) => StoryCompiler.Compile(new StorySource(
        new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = """
                game: {id: availability, title: Availability, start: start, ruleset: rpg}
                characters:
                  player: {first-name: Ada}
                vars: {gold: 10}
                flags: [ready]
                """,
            ["chapter.tale"] = $"=== start\n{body}",
        }));

    public static TheoryData<string, string> ConditionalStories => new()
    {
        { "all choices conditional", """
            * [Ready] (requires flag:ready) -> END
            * [Gold] (requires var:gold > 5) -> END
            """ },
        { "complementary requirements", """
            * [More] (requires var:gold > 5) -> END
            * [Less] (requires var:gold <= 5) -> END
            """ },
        { "conditional textless choice", """
            {if flag:ready}
            * -> END
            {/if}
            """ },
        { "if without else", """
            {if flag:ready}
            * [Ready] -> END
            {/if}
            """ },
        { "one conditional branch", """
            {if flag:ready}
            * [Ready] -> END
            {else}
            * [Gold] (requires var:gold > 5) -> END
            {/if}
            """ },
        { "nested branch without an option", """
            {if flag:ready}
            {if var:gold > 5}
            * [Gold] -> END
            {/if}
            {else}
            * [Not ready] -> END
            {/if}
            """ },
        { "separate complementary blocks", """
            {if flag:ready}
            * [Ready] -> END
            {/if}
            {if not flag:ready}
            * [Not ready] -> END
            {/if}
            """ },
        { "conditional divert before conditional choices", """
            {if flag:ready}
            -> END
            {/if}
            * [Gold] (requires var:gold > 5) -> END
            """ },
        { "hub with no authored choices", """
            @hub offer
            === offer
            @tags offer
            @requires false
            -> END
            """ },
        { "hub with only conditional choices", """
            @hub offer
            * [Ready] (requires flag:ready) -> END
            === offer
            @tags offer
            @requires false
            -> END
            """ },
        { "entry check with one continuing outcome", """
            ::: skill-check athletics dc 1
            success: -> END
            failure:
            :::
            * [Ready] (requires flag:ready) -> END
            """ },
        { "unreachable safe literal branch", """
            {if false}
            * [Unreachable] -> END
            {else}
            * [Ready] (requires flag:ready) -> END
            {/if}
            """ },
    };

    // Whether requirements hold depends on play; validation trusts the author's conditions.
    [Theory]
    [MemberData(nameof(ConditionalStories))]
    public void Choices_that_depend_on_story_state_do_not_warn(string scenario, string body)
    {
        var result = Compile(body);

        Assert.True(result.Success, $"{scenario}: {string.Join("\n", result.Diagnostics)}");
        Assert.Empty(result.Diagnostics);
    }

    public static TheoryData<string, string> SafeStories => new()
    {
        { "one unconditional choice", """
            * [Ready] (requires flag:ready) -> END
            * [Leave] -> END
            """ },
        { "unconditional textless choice", """
            * [Ready] (requires flag:ready) -> END
            * -> END
            """ },
        { "unconditional skill-check choice", """
            * [Try] skill-check athletics dc 1
            success: -> END
            failure: -> END
            """ },
        { "both branches offer a choice", """
            {if flag:ready}
            * [Ready] -> END
            {else}
            * [Not ready] -> END
            {/if}
            """ },
        { "every nested branch offers a choice", """
            {if flag:ready}
            {if var:gold > 5}
            * [Gold] -> END
            {else}
            * -> END
            {/if}
            {else}
            * [Not ready] -> END
            {/if}
            """ },
        { "unconditional choice after partial branch", """
            {if flag:ready}
            * [Ready] -> END
            {/if}
            * [Leave] -> END
            """ },
        { "unconditional choice before partial branch", """
            * [Leave] -> END
            {if flag:ready}
            * [Ready] -> END
            {/if}
            """ },
        { "hub with explicit exit", """
            @hub offer
            * [Leave] -> END
            === offer
            @tags offer
            @requires false
            -> END
            """ },
        { "conditional divert before unconditional choice", """
            {if flag:ready}
            -> END
            {/if}
            * [Leave] -> END
            """ },
        { "branch that diverts cannot reach the choices", """
            {if flag:ready}
            -> END
            {else}
            * [Leave] -> END
            {/if}
            """ },
        { "entry check followed by an unconditional choice", """
            ::: skill-check athletics dc 1
            success: -> END
            failure:
            :::
            * [Leave] -> END
            """ },
        { "literal true branch needs no else", """
            {if true}
            * [Leave] -> END
            {/if}
            """ },
        { "literal false selects the else branch", """
            {if false}
            * [Unreachable] (requires flag:ready) -> END
            {else}
            * [Leave] -> END
            {/if}
            """ },
        { "scene with a divert and no choices", """
            -> END
            """ },
    };

    [Theory]
    [MemberData(nameof(SafeStories))]
    public void Guaranteed_choice_or_earlier_exit_does_not_warn(string scenario, string body)
    {
        var result = Compile(body);

        Assert.True(result.Success, $"{scenario}: {string.Join("\n", result.Diagnostics)}");
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Unreachable_choices_and_hub_offers_report_only_reachability_warnings()
    {
        var choices = Compile("""
            -> END
            * [Unreachable] (requires flag:ready) -> END
            """);
        Assert.True(choices.Success, string.Join("\n", choices.Diagnostics));
        Assert.Equal("TW3012", Assert.Single(choices.Warnings).Code);

        var hub = Compile("""
            @hub offer
            -> END
            === offer
            @tags offer
            -> END
            """);
        Assert.True(hub.Success, string.Join("\n", hub.Diagnostics));
        Assert.Equal("TW3009", Assert.Single(hub.Warnings).Code);
    }

    [Fact]
    public void Intentionally_stranded_runtime_fixture_compiles_without_warnings()
    {
        var result = StoryCompiler.Compile(TestStories.NoRoute.Source());

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
        TestStories.NoRoute.Compile();
    }
}
