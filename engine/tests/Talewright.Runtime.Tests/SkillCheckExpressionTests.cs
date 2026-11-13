using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Narrative.Rendering;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Rules.Diagnostics;
using Talewright.Runtime;
using Talewright.Runtime.Saves;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Model;
using Talewright.Testing;

namespace Talewright.Runtime.Tests;

public sealed class SkillCheckExpressionTests
{
    private static readonly string Yaml = BuildYaml();

    private static string BuildYaml()
    {
        var manifest = new ManifestFaker("checks", "Checks", "start", 42)
        {
            Randomness = "off",
            BaseRuleset = "rpg",
        };
        manifest.Character("player", "Mara", "Burns",
            new Dictionary<string, int> { ["athletics"] = 2, ["perception"] = 1, ["dex"] = 3 }, "she");
        manifest.Character("guard", "Ivo", "Gray",
            new Dictionary<string, int> { ["athletics"] = 1, ["perception"] = 2, ["dex"] = 2 }, "he");
        manifest.Number("refresh", 0);
        return manifest.Build();
    }

    private static StoryDefinition Compile(string tale, string? yaml = null)
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = yaml ?? Yaml,
            ["main.tale"] = tale,
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.Story!;
    }

    [Fact]
    public void Compiles_checks_in_conditions_requirements_and_assignments_with_hidden_modifiers()
    {
        var story = Compile("""
            === start
            @requires skill-check perception dc 1, hidden, shown "Need the clue."
            ::: skill-check perception dc 1, hidden
            success:
            failure:
            :::
            {if skill-check perception dc 1, hidden}
            You notice the mark.
            {else}
            The wall looks ordinary.
            {/if}
            {if not false or false and skill-check perception dc 1, hidden}Operator combination compiles.{/if}
            Text {if skill-check guard athletics dc 1}by the guard{/if}.
            ~ var:roll = skill-check dex, hidden; var:guard-roll = skill-check guard perception
            * [Try the passage] (requires skill-check perception dc 1, hidden, shown "Need the clue.") { var:stored = skill-check dex, hidden } skill-check perception dc 1, hidden
              success: -> found
              failure: -> missed
            === found
            -> END
            === missed
            -> END
            """);

        var scene = story.GetScene(new SceneId("start"));
        Assert.True(Assert.IsType<SkillCheckExpr>(scene.Requires).IsHidden);
        Assert.NotNull(scene.Shown);
        Assert.True(Assert.Single(scene.Steps.OfType<CheckStep>()).Check.Check.IsHidden);

        var condition = Assert.IsType<ConditionalStep>(scene.Steps[1]);
        Assert.True(Assert.IsType<SkillCheckExpr>(condition.Condition).IsHidden);
        Assert.Contains("depends on a perception check",
            StoryMap.SceneDetails(scene).Requires.Reads.Single().Reference);
        var choice = Assert.Single(scene.Choices);
        Assert.True(Assert.IsType<SkillCheckExpr>(choice.Requires).IsHidden);
        Assert.NotNull(choice.Shown);
        Assert.True(choice.Check!.Check.IsHidden);

        var effects = scene.Steps.OfType<EffectStep>().Single().Effects;
        Assert.Contains(effects, effect => effect is ChangeVariable
        {
            Variable.Value: "roll",
            Amount: SkillCheckExpr { IsHidden: true, Type: ExprType.Number },
        });
        Assert.Contains(effects, effect => effect is ChangeVariable
        {
            Variable.Value: "guard-roll",
            Amount: SkillCheckExpr { Actor.Value: "guard", Type: ExprType.Number },
        });
        Assert.Contains(choice.Effects, effect => effect is ChangeVariable
        {
            Variable.Value: "stored",
            Amount: SkillCheckExpr { Type: ExprType.Number },
        });
    }

    [Fact]
    public void Evaluates_number_and_boolean_checks_lazily_and_emits_notes_only_for_visible_checks()
    {
        var story = Compile("""
            === start
            {if false and skill-check perception dc 1}
            This should not appear.
            {/if}
            {if true or skill-check dex dc 1}
            The other side was not evaluated.
            {/if}
            ~ var:total = skill-check perception, hidden
            {if var:total >= 1 and skill-check athletics dc 1}
            The check passed.
            {else}
            The check failed.
            {/if}
            * [Continue] -> END
            """);
        var session = new StorySession(story);

        var frame = session.Start();
        var checks = session.Events.OfType<SkillCheckResolved>().ToArray();

        Assert.Equal(2, checks.Length);
        Assert.Null(checks[0].Difficulty);
        Assert.Null(checks[0].Success);
        Assert.True(checks[0].IsHidden);
        Assert.Equal(1, checks[1].Difficulty);
        Assert.True(checks[1].Success);
        Assert.False(checks[1].IsHidden);
        Assert.Contains("The check passed.", frame.Document.ToPlainText());
        Assert.Contains("The other side was not evaluated.", frame.Document.ToPlainText());
        Assert.DoesNotContain("This should not appear.", frame.Document.ToPlainText());
        Assert.Single(frame.Document.Blocks.OfType<RenderedNote>(), note => note.Kind == NoteKind.Check);
        Assert.Equal(checks[0].Total, session.State.GetVariable(new VarId("total")));
    }

    [Fact]
    public void Nested_same_skill_checks_are_distinct_but_repeated_evaluation_is_cached_per_visit()
    {
        var story = Compile("""
            === start
            {if skill-check perception dc 1}
            {if skill-check perception dc 1}
            Both checks passed.
            {/if}
            {/if}
            * [Continue {if skill-check perception dc 1}with the clue{else}without the clue{/if}] -> END
            """);
        var author = AuthorSession.Create(story, new SceneId("start"), seed: 42);
        var checks = author.Events.OfType<SkillCheckResolved>().ToArray();

        Assert.Equal(3, checks.Length);
        Assert.NotEqual(checks[0].Identity, checks[1].Identity);
        author.Edit(new AuthorSetup
        {
            Variables = new Dictionary<VarId, int> { [new VarId("refresh")] = 1 },
        });
        Assert.Equal(3, author.Events.OfType<SkillCheckResolved>().Count());

        author.Jump(new SceneId("start"));
        var revisited = author.Events.OfType<SkillCheckResolved>().ToArray();
        Assert.Equal(6, revisited.Length);
        Assert.Equal(checks[0].Identity, revisited[3].Identity);
        Assert.Equal(checks[1].Identity, revisited[4].Identity);
        Assert.Equal(checks[0].Visit + 1, revisited[3].Visit);
        Assert.Equal(checks[2].Identity, revisited[5].Identity);
    }

    [Fact]
    public void Trigger_effect_assignments_can_use_keyed_checks_but_trigger_conditions_cannot()
    {
        var manifest = new ManifestFaker("trigger-checks", "Trigger checks", "start", 42)
        {
            Randomness = "off",
            BaseRuleset = "rpg",
        };
        manifest.Character("player", "Mara", stats: new Dictionary<string, int> { ["perception"] = 1 });
        manifest.Flag("ready");
        manifest.Number("total", 0);
        var yaml = manifest.Build() + """

            triggers:
              - id: capture-roll
                once: true
                when: flag:ready
                do: "var:total = skill-check perception"
            """;
        var story = Compile("""
            === start
            ~ set flag:ready
            * [Continue] -> END
            """, yaml);
        var session = new StorySession(story);
        var frame = session.Start();
        var resolved = Assert.Single(session.Events.OfType<SkillCheckResolved>());

        Assert.Equal("trigger-capture-roll", resolved.Identity.Scene.Value);
        Assert.Equal(resolved.Total, session.State.GetVariable(new VarId("total")));
        Assert.Contains(frame.Document.Blocks.OfType<RenderedNote>(), note => note.Kind == NoteKind.Check);

        var invalidYaml = yaml.Replace("when: flag:ready", "when: skill-check perception dc 12",
            StringComparison.Ordinal);
        var invalid = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = invalidYaml,
            ["main.tale"] = "=== start\n-> END",
        }));
        Assert.False(invalid.Success);
        Assert.Contains(invalid.Diagnostics, diagnostic =>
            diagnostic.Code == CompilerDiagnosticCodes.InvalidExpression &&
            diagnostic.Message.Contains("not allowed", StringComparison.Ordinal));
    }

    [Fact]
    public void Hub_limit_evaluates_only_candidates_until_full_and_revisiting_hub_resolves_a_new_check()
    {
        var story = Compile("""
            === start
            -> hub
            === hub
            @hub candidates
            @hub-limit 1
            * [Exit] -> END
            === first
            @tags candidates
            @priority 10
            @requires skill-check perception dc 1
            * [Return] -> hub
            === below-cutoff
            @tags candidates
            @priority 1
            @requires skill-check athletics dc 1
            -> END
            """);
        var author = AuthorSession.Create(story, new SceneId("hub"), captureDecisions: true, seed: 42);
        var firstCheck = Assert.Single(author.Events.OfType<SkillCheckResolved>());

        Assert.Equal("perception", firstCheck.Skill.Value);
        Assert.Contains(author.Decisions, decision => decision.Subject == "below-cutoff" &&
            decision.Reason == DecisionReason.HubLimit);

        author.Choose("storylet:first");
        author.Choose(author.Current.Choices.Single().Key);

        var checks = author.Events.OfType<SkillCheckResolved>().ToArray();
        Assert.Equal(2, checks.Length);
        Assert.Equal(firstCheck.Identity, checks[1].Identity);
        Assert.Equal(firstCheck.Visit + 1, checks[1].Visit);
        Assert.Equal(new SceneId("hub"), author.Current.Scene);
        Assert.DoesNotContain(checks, check => check.Skill.Value == "athletics");
    }

    [Fact]
    public void Expression_check_results_replay_through_save_restore_and_history_navigation()
    {
        var story = Compile("""
            === start
            {if skill-check perception dc 1}
            You notice the mark.
            {else}
            You miss the mark.
            {/if}
            * [Continue] -> END
            """);
        var history = new StoryHistory(story, 42);
        var originalCheck = Assert.Single(history.Events.OfType<SkillCheckResolved>());

        var initialSave = history.Save();
        var restored = StoryHistory.Restore(story, initialSave);
        Assert.Equal(initialSave, restored.Save());
        Assert.Equal(history.Events, restored.Events);

        history.Choose(history.Current.Choices.Single().Key);
        var saved = history.Save();
        restored = StoryHistory.Restore(story, saved);
        Assert.Equal(saved, restored.Save());
        Assert.Equal(history.Events, restored.Events);

        restored.Back();
        restored.Forward();
        Assert.Equal(originalCheck, Assert.Single(restored.Events.OfType<SkillCheckResolved>()));
        Assert.Equal(saved, restored.Save());
    }
}
