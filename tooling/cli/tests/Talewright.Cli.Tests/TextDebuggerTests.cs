using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;
using Talewright.Debugger;
using Talewright.Narrative.Rendering;
using Talewright.Talemark.Compilation;

namespace Talewright.Cli.Tests;

public class TextDebuggerTests
{
    private static SourceCompilation Compile(bool implicitDeclarations = false, bool number = false)
    {
        var source = new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = """
                game:
                  id: text-debugger
                  title: Text debugger
                  start: a
                characters:
                  player: { first-name: Ada }
                vars:
                  count: 0
                """ + (implicitDeclarations ? "" : "\n  weather: " + (number ? "0" : "\"sunny\"")),
            ["main.tale"] = """
                === a
                Weather [{var:weather}].
                * [Storm] { var:weather = "stormy" } -> b
                === b
                Weather [{var:weather}].
                * [Return] -> a
                """.Replace("var:weather = \"stormy\"", number ? "var:weather = 1" : "var:weather = \"stormy\""),
        });
        return new(source, StoryCompiler.Compile(source), null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Text_setup_live_edits_navigation_and_restarts_preserve_exact_values(bool implicitDeclarations)
    {
        var model = new WorkbenchModel("unused");
        Assert.True(model.ApplySource(Compile(implicitDeclarations), restart: true));
        var field = Assert.Single(model.Texts);
        field.Enabled = true;
        field.Value = "";
        model.Start();
        Assert.Null(model.Error);
        Assert.Equal("", model.Session!.Setup.TextVariables[new("weather")]);
        Assert.Contains("Weather [].", model.Session.Current.Document.ToPlainText());
        Assert.Equal("", Assert.Single(model.Inspection!.TextVariables).Value);
        var edit = Assert.Single(model.EditTexts);
        edit.Enabled = true;
        edit.Value = "雪\n \"quoted\" \\ path";
        model.ApplyEdits();
        Assert.Null(model.Error);
        Assert.Equal(edit.Value, model.Session.State.GetTextVariable(new("weather")));
        Assert.False(Assert.Single(model.EditTexts).Enabled);
        model.Back();
        Assert.Equal("", Assert.Single(model.EditTexts).Value);
        model.Forward();
        Assert.Equal(edit.Value, Assert.Single(model.EditTexts).Value);
        var session = model.Session;
        var invalid = Assert.Single(model.EditTexts);
        invalid.Enabled = true;
        invalid.Value = null!;
        model.ApplyEdits();
        Assert.Contains("non-null", model.Error);
        Assert.Same(session, model.Session);
        Assert.Equal(edit.Value, session.State.GetTextVariable(new("weather")));
        Assert.False(model.ApplySource(Compile(number: true), restart: true));
        Assert.Contains("Setup IDs changed/removed", model.Error);
        Assert.Same(session, model.Session);
        field.Enabled = false;
        Assert.True(model.ApplySource(Compile(number: true), restart: true));
        Assert.Empty(model.Texts);
        Assert.Contains(model.Numbers, number => number.Label == "var:weather");
        Assert.Null(model.Session);
    }

    [Fact]
    public async Task Text_controls_and_inspection_render_types_empty_values_and_encoded_text()
    {
        var model = new WorkbenchModel("unused");
        Assert.True(model.ApplySource(Compile(), restart: true));
        var field = Assert.Single(model.Texts);
        field.Enabled = true;
        field.Value = "";
        model.Start();
        var setup = await Render<SetupFields>(new()
        {
            [nameof(SetupFields.Numbers)] = model.Numbers,
            [nameof(SetupFields.Flags)] = model.Flags,
            [nameof(SetupFields.Texts)] = model.Texts,
        });
        Assert.Contains("Text variables", setup);
        Assert.Contains("<textarea", setup);
        Assert.Contains("Override var:weather", setup);
        Assert.Contains("empty field", setup);
        var inspection = await Render<InspectionPanel>(new()
        {
            [nameof(InspectionPanel.Inspection)] = model.Inspection,
        });
        Assert.Contains("var:weather (text)", inspection);
        Assert.Contains("var:count (integer)", inspection);
        Assert.Contains("&quot;&quot;", inspection);
        var edit = Assert.Single(model.EditTexts);
        edit.Enabled = true;
        edit.Value = "<script>alert('not markup')</script>\n";
        model.ApplyEdits();
        var encoded = await Render<InspectionPanel>(new()
        {
            [nameof(InspectionPanel.Inspection)] = model.Inspection,
        });
        Assert.DoesNotContain("<script>", encoded);
        Assert.Contains("\\u003Cscript\\u003E", encoded);
        Assert.Contains("\\n", encoded);
    }

    private static async Task<string> Render<T>(Dictionary<string, object?> parameters) where T : IComponent
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters));
            return component.ToHtmlString();
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scenario_import_restores_text_controls_and_failed_import_keeps_the_run(bool implicitDeclarations)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"text-debugger-{Guid.NewGuid():N}");
        var root = Path.Combine(directory, "story");
        var path = Path.Combine(directory, "text.twscenario");
        var invalidPath = Path.Combine(directory, "invalid.twscenario");
        Directory.CreateDirectory(root);
        try
        {
            var source = Compile(implicitDeclarations);
            var model = new WorkbenchModel(root) { ScenarioPath = path };
            Assert.True(model.ApplySource(source, restart: true));
            model.Texts[0].Enabled = true;
            model.Texts[0].Value = "";
            model.Start();
            model.EditTexts[0].Enabled = true;
            model.EditTexts[0].Value = "雪";
            model.ApplyEdits();
            model.Choose("c0");
            model.Back();
            await model.ExportScenario();
            Assert.Null(model.Error);
            var restored = new WorkbenchModel(root) { ScenarioPath = path };
            Assert.True(restored.ApplySource(source, restart: true));
            await restored.ImportScenario();
            Assert.Null(restored.Error);
            Assert.True(restored.Texts[0].Enabled);
            Assert.Equal("", restored.Texts[0].Value);
            Assert.Equal("雪", restored.EditTexts[0].Value);
            Assert.True(restored.Session!.CanForward);
            Assert.Equal(model.Session!.Events, restored.Session.Events);
            var bytes = await File.ReadAllBytesAsync(path);
            var json = JsonNode.Parse(bytes)!;
            json["setup"]!["textVariables"]!["weather"] = 3;
            await File.WriteAllTextAsync(invalidPath, json.ToJsonString());
            var active = restored.Session;
            restored.ScenarioPath = invalidPath;
            await restored.ImportScenario();
            Assert.Contains("Scenario operation failed", restored.Error);
            Assert.Same(active, restored.Session);
            Assert.Equal("雪", restored.Session.State.GetTextVariable(new("weather")));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(invalidPath)) File.Delete(invalidPath);
            Directory.Delete(root);
            Directory.Delete(directory);
        }
    }
}
