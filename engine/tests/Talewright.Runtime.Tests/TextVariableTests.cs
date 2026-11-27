using Talewright.Core.Events;
using Talewright.Narrative.Rendering;
using Talewright.Runtime.Saves;
using Talewright.Talemark.Compilation;
using Talewright.Testing;

namespace Talewright.Runtime.Tests;

public class TextVariableTests
{
    [Fact]
    public void Initially_declared_item_survives_replay_and_checkpoint_continuation()
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = """
                game: { id: initial-item, title: Initial item, start: start, talemark: 2 }
                characters: { player: { first-name: Ada } }
                items: { sword: Sword }
                vars: { chosen: item:sword }
                """,
            ["main.tale"] = """
                === start
                Chosen: {var:chosen}.
                * [Take] (requires var:chosen == sword) { give player var:chosen } -> next
                === next
                {if player has var:chosen}Holding {var:chosen}.{/if}
                * [Finish] { take player var:chosen } -> END
                """,
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var history = new StoryHistory(result.Story!, 42);
        Assert.Contains("Chosen: Sword.", history.Current.Document.ToPlainText());
        history.Choose("c0");
        var bytes = history.Save();
        var replay = StoryHistory.Restore(result.Story!, bytes);
        var replayOnly = StoryHistory.Restore(result.Story!, SaveFormat.Write(SaveFormat.Read(bytes) with { Continuation = null }));
        var checkpoint = StoryHistory.RestoreFromCheckpoint(result.Story!, bytes);
        foreach (var restored in new[] { replay, replayOnly, checkpoint })
        {
            Assert.Equal("sword", restored.State.GetTextVariable(new("chosen")));
            Assert.Equal(1, restored.State.GetItemCount(new("player"), new("sword")));
            Assert.Contains("Holding Sword.", restored.Current.Document.ToPlainText());
            restored.Choose("c0");
            Assert.Equal(0, restored.State.GetItemCount(new("player"), new("sword")));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Text_history_preserves_values_events_narrative_redo_and_failed_restore_isolation(bool implicitDeclarations)
    {
        var weather = new TaleVariableRef("weather");
        var copy = new TaleVariableRef("copy");
        var manifest = new ManifestFaker("text-history", "Text history", "a");
        manifest.Character("player", "Tester");
        if (!implicitDeclarations)
        {
            manifest.Text(weather.Id, "");
            manifest.Text(copy.Id, "");
        }

        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = manifest.Build(),
            ["main.tale"] = string.Join('\n',
                SceneFaker.Header("a"),
                $"Initial [{SubstitutionFaker.Variable(weather)}].",
                ChoiceFaker.Create("b", "Storm", effect: string.Join("; ",
                    EffectFaker.Assign(weather, "\"雪\""),
                    EffectFaker.Assign(copy, weather.ToString()))),
                SceneFaker.Header("b"),
                $"Weather: {SubstitutionFaker.Variable(weather)}. Copy: {SubstitutionFaker.Variable(copy)}.",
                ChoiceFaker.Create("c", "Clear", effect: EffectFaker.Assign(weather, "\"\"")),
                SceneFaker.Header("c"),
                $"Weather: [{SubstitutionFaker.Variable(weather)}]. Copy: {SubstitutionFaker.Variable(copy)}.",
                DivertFaker.To("END")),
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var history = new StoryHistory(result.Story!, 42);
        history.Choose("c0");
        history.Choose("c0");
        Assert.Equal("", history.State.GetTextVariable(new("weather")));
        Assert.Equal("雪", history.State.GetTextVariable(new("copy")));
        history.Back();
        Assert.Equal("雪", history.State.GetTextVariable(new("weather")));
        var restored = StoryHistory.Restore(result.Story!, history.Save());
        Assert.Equal(history.Events, restored.Events);
        Assert.Equal(history.Save(), restored.Save());
        Assert.True(restored.CanForward);
        history.Forward();
        restored.Forward();
        Assert.Equal(history.Events, restored.Events);
        Assert.Equal(history.Current.Document.ToPlainText(), restored.Current.Document.ToPlainText());
        Assert.Contains(restored.Events, e => e is TextVariableChanged { OldValue: "雪", NewValue: "" });
        var before = history.Save();
        var saved = SaveFormat.Read(before);
        var damagedIdentities = saved.ReplayChoiceIdentities!.Select(identity => identity.ToArray()).ToArray();
        damagedIdentities[1][0] ^= 1;
        var damaged = SaveFormat.Write(saved with { ReplayChoiceIdentities = damagedIdentities });
        Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(result.Story!, damaged));
        Assert.Equal(before, history.Save());
        Assert.Equal("", history.State.GetTextVariable(new("weather")));

        var scenario = AuthorScenario.Export(history.ForkAuthor(), "text-history");
        Assert.Equal(history.Events, AuthorScenario.Import(result.Story!, scenario, "text-history").Session.Events);
    }
}
