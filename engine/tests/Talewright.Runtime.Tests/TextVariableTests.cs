using System.Text;
using System.Text.Json.Nodes;
using Talewright.Core.Events;
using Talewright.Narrative.Rendering;
using Talewright.Tale.Compilation;

namespace Talewright.Runtime.Tests;

public class TextVariableTests
{
    [Fact]
    public void Text_history_preserves_values_events_narrative_redo_and_failed_restore_isolation()
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = """
                id: text-history
                title: Text history
                start: a
                characters:
                  player: { first-name: Tester }
                vars:
                  weather: ""
                  copy: ""
                """,
            ["main.tale"] = """
                === a
                Initial [{var:weather}].
                * [Storm] { var:weather = "雪"; var:copy = var:weather } -> b
                === b
                Weather: {var:weather}. Copy: {var:copy}.
                * [Clear] { var:weather = "" } -> c
                === c
                Weather: [{var:weather}]. Copy: {var:copy}.
                -> END
                """,
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
        var json = JsonNode.Parse(before)!;
        json["choices"]![1] = "missing";
        Assert.Throws<InvalidDataException>(() =>
            StoryHistory.Restore(result.Story!, Encoding.UTF8.GetBytes(json.ToJsonString())));
        Assert.Equal(before, history.Save());
        Assert.Equal("", history.State.GetTextVariable(new("weather")));
    }
}
