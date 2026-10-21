using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Tale.Compilation;
using Talewright.Testing;

namespace Talewright.Runtime.Tests;

public class PlayerFixtureTests
{
    public static IEnumerable<object[]> Scenarios => TestStories.Scenarios.Select(s => new object[] { s.Id });
    public static IEnumerable<object[]> Fixtures => TestStories.All.Select(f => new object[] { f.Id });

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Explicit_ui_scenarios_have_valid_runtime_expectations(string id)
    {
        var scenario = TestStories.Scenarios.Single(s => s.Id == id);
        var session = new StorySession(TestStories.Get(scenario.Fixture).Compile());
        var frame = session.Start();
        foreach (var step in scenario.Steps)
        {
            if (step.Choose is not null)
            {
                var choice = Assert.Single(frame.Choices, c => c.Text == step.Choose);
                frame = session.Choose(choice.Key);
            }

            Assert.Contains(step.Contains, frame.Document.ToPlainText().Length == 0 ? frame.Title ?? "" :
                (frame.Title ?? "") + "\n" + frame.Document.ToPlainText());
            if (step.Choices is not null)
            {
                Assert.Equal(step.Choices, frame.Choices.Select(c => c.Text));
            }

            Assert.Equal(step.Ended, frame.IsEnded);
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Dictionary_zip_wrapped_zip_and_folder_have_identical_sources(string id)
    {
        var fixture = TestStories.Get(id);
        fixture.Compile();
        foreach (var prefix in new[] { "", "wrapped story/" })
        {
            using var zip = new MemoryStream(fixture.Zip(prefix));
            Assert.Equal(fixture.Files.OrderBy(p => p.Key), StorySource.FromZip(zip).Files.OrderBy(p => p.Key));
        }

        var folder = Path.Combine(Path.GetTempPath(), "talewright-fixture-" + Guid.NewGuid().ToString("N"));
        try
        {
            fixture.WriteDirectory(folder);
            Assert.Equal(fixture.Files.OrderBy(p => p.Key), StorySource.FromDirectory(folder).Files.OrderBy(p => p.Key));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Stable_fixture_covers_donation_level_up_and_item_removal()
    {
        var session = new StorySession(TestStories.Progression.Compile());
        session.Start();
        session.Choose("c0");
        Assert.Equal(2, session.State.GetStat(EntityId.Player, new StatId("level")));
        Assert.Equal(12, session.State.GetStat(EntityId.Player, new StatId("cha")));
        var frame = session.Choose("c0");
        Assert.True(frame.IsEnded);
        Assert.Equal(0, session.State.GetItemCount(EntityId.Player, new ItemId("relic")));
        Assert.Equal(10, session.State.GetStat(EntityId.Player, new StatId("cha")));
        Assert.Equal(20, session.State.GetStanding(new FactionId("guild")));
    }

    [Fact]
    public void Stable_fixture_covers_faction_trigger_and_nested_ending()
    {
        var session = new StorySession(TestStories.Progression.Compile());
        session.Start();
        session.Choose("c0");
        var frame = session.Choose("c1");
        Assert.True(frame.IsEnded);
        Assert.Contains("Ready, relic watching.", frame.Document.ToPlainText());
        Assert.True(session.State.GetFlag(new FlagId("wary")));
        Assert.Contains(session.Events, e => e is TriggerFired { TriggerId: "wary" });
    }
}
