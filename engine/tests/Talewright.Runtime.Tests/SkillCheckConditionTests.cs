using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Narrative.Rendering;
using Talewright.Runtime.Saves;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;
using Talewright.Testing;

namespace Talewright.Runtime.Tests;

public sealed class SkillCheckConditionTests
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
            new Dictionary<string, int> { ["athletics"] = 2 }, "she");
        manifest.Item("rope", "coil of rope");
        return manifest.Build();
    }

    private static StoryDefinition Compile(string tale)
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = Yaml,
            ["main.tale"] = tale,
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.Story!;
    }

    [Theory]
    [InlineData(", hidden", true)]
    [InlineData("", false)]
    public void Block_check_conditional_chooses_a_branch_and_keeps_other_effect_notes(
        string modifier, bool hidden)
    {
        var story = Compile($$"""
            === start
            {if skill-check athletics dc 12{{modifier}}}
            ~ give player rope
            You find the passage.
            {else}
            The wall is solid.
            {/if}
            * [Continue] -> END
            """);

        var session = new StorySession(story);
        var frame = session.Start();
        var check = Assert.Single(session.Events.OfType<SkillCheckResolved>());

        Assert.Equal(hidden, check.IsHidden);
        Assert.True(check.Success);
        Assert.Contains("You find the passage.", frame.Document.ToPlainText());
        Assert.DoesNotContain("The wall is solid.", frame.Document.ToPlainText());
        Assert.Contains(frame.Document.Blocks.OfType<RenderedNote>(),
            note => note.Kind == NoteKind.Change && note.Text.Contains("Gained: coil of rope", StringComparison.Ordinal));
        Assert.Equal(hidden, !frame.Document.Blocks.OfType<RenderedNote>().Any(note => note.Kind == NoteKind.Check));
    }

    [Fact]
    public void Block_check_conditional_runs_the_failure_branch()
    {
        var story = Compile("""
            === start
            {if skill-check athletics dc 13}
            You find the passage.
            {else}
            The wall is solid.
            {/if}
            * [Continue] -> END
            """);
        var session = new StorySession(story);

        var frame = session.Start();

        Assert.False(Assert.Single(session.Events.OfType<SkillCheckResolved>()).Success);
        Assert.Contains("The wall is solid.", frame.Document.ToPlainText());
        Assert.DoesNotContain("You find the passage.", frame.Document.ToPlainText());
    }

    [Fact]
    public void Block_check_condition_replays_across_save_load_and_back()
    {
        var story = Compile("""
            === start
            {if skill-check athletics dc 12, hidden}
            You find the passage.
            {else}
            The wall is solid.
            {/if}
            * [Continue] -> END
            """);
        var history = new StoryHistory(story, 42);
        var check = Assert.Single(history.Events.OfType<SkillCheckResolved>());
        Assert.True(check.IsHidden);
        Assert.True(check.Success);

        var initialSave = history.Save();
        var restored = StoryHistory.Restore(story, initialSave);
        Assert.Equal(initialSave, restored.Save());

        history.Choose("c0");
        var saved = history.Save();
        restored = StoryHistory.Restore(story, saved);
        Assert.Equal(saved, restored.Save());
        Assert.Equal(history.Events, restored.Events);

        restored.Back();
        restored.Forward();
        Assert.Equal(check, Assert.Single(restored.Events.OfType<SkillCheckResolved>()));
        Assert.Equal(saved, restored.Save());
    }

    [Fact]
    public void Hidden_entry_check_does_not_render_a_check_note()
    {
        var session = new StorySession(Compile("""
            === start
            ::: skill-check athletics dc 12, hidden
            success:
            failure:
            :::
            * [Continue] -> END
            """));

        var frame = session.Start();

        Assert.True(Assert.Single(session.Events.OfType<SkillCheckResolved>()).IsHidden);
        Assert.DoesNotContain(frame.Document.Blocks.OfType<RenderedNote>(), note => note.Kind == NoteKind.Check);
    }

    [Fact]
    public void Hidden_choice_check_replays_and_survives_save_restore()
    {
        var story = Compile("""
            === start
            * [Try the passage] skill-check athletics dc 12, hidden
              success: -> found
              failure: -> missed
            === found
            -> END
            === missed
            -> END
            """);
        var history = new StoryHistory(story, 42);
        history.Choose("c0");

        var check = Assert.Single(history.Events.OfType<SkillCheckResolved>());
        Assert.True(check.IsHidden);
        Assert.True(check.Success);
        Assert.DoesNotContain(history.Current.Document.Blocks.OfType<RenderedNote>(), note => note.Kind == NoteKind.Check);

        var saved = history.Save();
        var restored = StoryHistory.Restore(story, saved);
        Assert.Equal(saved, restored.Save());
        Assert.Equal(history.Events, restored.Events);

        restored.Back();
        restored.Forward();
        Assert.Equal(check, Assert.Single(restored.Events.OfType<SkillCheckResolved>()));
        Assert.DoesNotContain(restored.Current.Document.Blocks.OfType<RenderedNote>(), note => note.Kind == NoteKind.Check);
    }
}
