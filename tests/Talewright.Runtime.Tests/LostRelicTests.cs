using Talewright.Core.Identity;
using Talewright.Tale.Compilation;
using Talewright.Tale.Model;

namespace Talewright.Runtime.Tests;

/// <summary>End-to-end: compile the shipped sample story from disk and play it.</summary>
public class LostRelicTests
{
    private static readonly Lazy<CompilationResult> Compiled = new(() =>
        StoryCompiler.Compile(StorySource.FromDirectory(Path.Combine(RepoRoot(), "samples", "LostRelic"))));

    private static StoryDefinition Story
    {
        get
        {
            Assert.True(Compiled.Value.Success, string.Join("\n", Compiled.Value.Diagnostics));
            return Compiled.Value.Story!;
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Talewright.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }

    private static StoryFrame Pick(StorySession session, string labelStart)
    {
        var choice = session.Current!.Choices.FirstOrDefault(c => c.Text.StartsWith(labelStart, StringComparison.Ordinal));
        Assert.True(choice is not null, $"No choice starting '{labelStart}' in {session.Current.Scene}. Have: {string.Join(" | ", session.Current.Choices.Select(c => c.Text))}");
        return session.Choose(choice.Key);
    }

    [Fact]
    public void Sample_compiles_without_errors_or_warnings()
    {
        Assert.Empty(Compiled.Value.Diagnostics);
        Assert.Equal("The Lost Relic", Story.Title);
    }

    [Fact]
    public void Scripted_playthrough_reaches_the_guild_ending()
    {
        var session = new StorySession(Story, seed: 7);
        var frame = session.Start();
        Assert.Contains("Burns's reputation for punctuality", frame.Document.ToPlainText());
        Assert.Contains("Tomas: Ms Burns.", frame.Document.ToPlainText());

        frame = Pick(session, "Continue");
        Assert.Equal(new SceneId("camp.hub"), frame.Scene);
        Assert.Equal(["Set out for the temple", "Study your journal", "Haggle for supplies", "Ask Tomas about the temple"], frame.Choices.Select(c => c.Text));

        Pick(session, "Haggle");
        frame = Pick(session, "Continue");
        Assert.Contains("eyeing the supplies", frame.Document.ToPlainText());
        Pick(session, "Ask Tomas");
        Pick(session, "Continue");
        Pick(session, "Set out");

        frame = Pick(session, "Tie off the rope");
        Assert.Equal(new SceneId("trail.far-bank"), frame.Scene);
        Pick(session, "Continue");
        Pick(session, "Look for another way in");
        frame = Pick(session, "Squeeze through");
        Assert.Equal(new SceneId("temple.hall"), frame.Scene);
        Assert.Contains(frame.Document.Blocks, b => b.ToPlainText().Contains("Perception check"));

        frame = Pick(session, "Continue");
        if (frame.Scene == new SceneId("temple.niche"))
        {
            frame = Pick(session, "Take the idol");
        }

        Assert.Equal(new SceneId("temple.altar"), frame.Scene);
        Pick(session, "Take the relic");
        frame = Pick(session, "Hand the relic");

        Assert.True(frame.IsEnded);
        Assert.Equal(new SceneId("ending.guild"), frame.Scene);
        Assert.Contains("hangs Burns's portrait", frame.Document.ToPlainText());
        Assert.Contains("now friendly", frame.Document.ToPlainText());
        Assert.Equal(0, session.State.GetItemCount(EntityId.Player, new ItemId("relic")));
        Assert.True(session.State.GetStat(EntityId.Player, new StatId("level")) >= 2);
    }

    [Fact]
    public void Random_playthroughs_always_finish_and_reach_every_ending()
    {
        var endings = new HashSet<string>();
        var keeperOutcomes = new HashSet<string>();
        for (var seed = 1UL; seed <= 300; seed++)
        {
            var session = new StorySession(Story, seed);
            var random = new Random((int)seed);
            var frame = session.Start();
            var steps = 0;
            while (!frame.IsEnded)
            {
                Assert.True(++steps < 200, $"Seed {seed} did not finish.");
                frame = session.Choose(frame.Choices[random.Next(frame.Choices.Count)].Key);
            }

            endings.Add(frame.Scene!.Value.Value);
            var text = frame.Document.ToPlainText();
            foreach (var outcome in new[] { "rested and unhurried", "idol watching", "under your pillow" })
            {
                if (text.Contains(outcome, StringComparison.Ordinal))
                {
                    keeperOutcomes.Add(outcome);
                }
            }
        }

        Assert.Equal(["ending.empty-handed", "ending.guild", "ending.keep"], endings.Order());

        // Every branch of the nested {if} in ending.keep is reachable.
        Assert.Equal(["idol watching", "rested and unhurried", "under your pillow"], keeperOutcomes.Order());
    }
}
