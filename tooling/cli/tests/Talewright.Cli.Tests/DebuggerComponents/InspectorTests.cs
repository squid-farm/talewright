using Bunit;
using Microsoft.AspNetCore.Components;
using Talewright.Core.Identity;
using Talewright.Debugger;
using Talewright.Rules.Diagnostics;
using Talewright.Runtime;
using Talewright.Talemark.Compilation;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class InspectorTests : BunitContext
{
    [Fact]
    public void Scene_inspector_shows_source_location_visit_count_and_requirement_trace()
    {
        var files = ComponentStory.Files();
        files["main.tale"] = files["main.tale"].Replace("@title Great Hall", "@title Great Hall\n@requires flag:ready", StringComparison.Ordinal);
        using var model = ComponentStory.Load(files);
        model.Start();
        var compilation = StoryCompiler.Compile(new StorySource(files));
        var scene = model.Story!.GetScene(new SceneId("hall"));
        var inspector = Render<SceneInspector>(parameters => parameters
            .Add(component => component.Story, model.Story)
            .Add(component => component.Scene, scene)
            .Add(component => component.SourceMap, compilation.SourceMap)
            .Add(component => component.Session, model.Session)
            .Add(component => component.Inspection, model.Inspection));

        Assert.Contains("hall", inspector.Markup);
        Assert.Contains("main.tale lines", inspector.Markup);
        Assert.Contains("Visit count", inspector.Markup);
        Assert.Contains("requires", inspector.Markup);
        Assert.Contains("flag:ready", inspector.Markup);
    }

    [Fact]
    public void Scene_inspector_shows_the_storylet_requirement_evaluated_at_the_hub()
    {
        var files = ComponentStory.Files();
        files["main.tale"] = """
            === hall
            @hub adventure
            * [Leave] -> END
            === cave
            @title Cave
            @tags adventure
            @requires flag:ready
            * [Leave] -> END
            """;
        using var model = ComponentStory.Load(files);
        var ready = model.Flags.Single(flag => flag.Id.Value == "ready");
        ready.Enabled = true;
        ready.Value = true;
        model.Start();
        ComponentStory.Choose(model, "Cave");

        var decision = Assert.Single(model.Inspection!.Decisions,
            item => item.Kind == DecisionKind.Storylet && item.Subject == "cave" && item.Expression is not null);
        Assert.Equal("hall", decision.Scene?.Value);
        var inspector = Render<SceneInspector>(parameters => parameters
            .Add(component => component.Story, model.Story)
            .Add(component => component.Scene, model.Story!.GetScene(new SceneId("cave")))
            .Add(component => component.Inspection, model.Inspection));

        Assert.Contains("flag:ready", inspector.Markup);
        Assert.Equal("true", inspector.Find("strong").TextContent);
        Assert.DoesNotContain("Not evaluated in this run.", inspector.Markup);
    }

    [Fact]
    public void Choice_inspector_shows_key_label_target_decision_trace_and_chosen_state()
    {
        using var model = ComponentStory.Load();
        var ready = model.Flags.Single(flag => flag.Id.Value == "ready");
        ready.Enabled = true;
        ready.Value = true;
        model.Start();

        var scene = model.Story!.GetScene(new SceneId("hall"));
        var selected = model.Session!.Current.Choices.Single(choice => choice.Text == "Prepared");
        var inspector = Render<ChoiceInspector>(parameters => parameters
            .Add(component => component.Story, model.Story)
            .Add(component => component.Scene, scene)
            .Add(component => component.ChoiceKey, selected.Key)
            .Add(component => component.Choice, selected)
            .Add(component => component.Session, model.Session)
            .Add(component => component.Inspection, model.Inspection));

        Assert.Contains(selected.Key, inspector.Markup);
        Assert.Contains("Prepared", inspector.Markup);
        Assert.Contains("cave", inspector.Markup);
        Assert.Contains("flag:ready", inspector.Markup);
        Assert.Contains("Chosen in this run", inspector.Markup);
        Assert.Contains("No</dd>", inspector.Markup);

        ComponentStory.Choose(model, "Prepared");
        var chosen = Render<ChoiceInspector>(parameters => parameters
            .Add(component => component.Story, model.Story)
            .Add(component => component.Scene, scene)
            .Add(component => component.ChoiceKey, selected.Key)
            .Add(component => component.Choice, null)
            .Add(component => component.Inspection, model.Inspection)
            .Add(component => component.Session, model.Session));
        Assert.Contains("Yes</dd>", chosen.Markup);
    }

    [Fact]
    public void Choice_inspector_distinguishes_available_shown_hidden_and_unreached_choices()
    {
        using var model = DebuggerDecisionStory.Start();

        var available = ShowChoice(model, "c1");
        Assert.Equal("Available", Status(available));
        Assert.Equal("Alternate 0", Value(available, "shown text"));

        var shown = ShowChoice(model, "c2");
        Assert.Equal("Shown, unavailable", Status(shown));
        Assert.Equal("Need 0 keys", Value(shown, "shown text"));
        Assert.Contains("flag:ready", shown.Markup);

        var hidden = ShowChoice(model, "c3");
        Assert.Equal("Hidden", Status(hidden));
        Assert.Contains("flag:ready", hidden.Markup);

        var unreached = ShowChoice(model, "c0");
        Assert.Equal("Not reached", Status(unreached));
        Assert.Contains("Not evaluated in this run.", unreached.Markup);
    }

    [Fact]
    public void Inspector_pane_renders_the_selected_choice()
    {
        using var model = ComponentStory.Load();
        var ready = model.Flags.Single(flag => flag.Id.Value == "ready");
        ready.Enabled = true;
        ready.Value = true;
        model.Start();

        var scene = model.Story!.GetScene(new SceneId("hall"));
        var choice = model.Session!.Current.Choices.Single(item => item.Text == "Prepared");
        var inspector = Render<InspectorPane>(parameters => parameters
            .Add(component => component.Story, model.Story)
            .Add(component => component.Selection, new InspectorSelection(scene.Id, choice.Key))
            .Add(component => component.Session, model.Session)
            .Add(component => component.Inspection, model.Inspection));

        Assert.Contains($"Choice {choice.Key}", inspector.Markup);
        Assert.Contains("Prepared", inspector.Markup);
        Assert.DoesNotContain("Scene inspector", inspector.Markup);
    }

    [Fact]
    public async Task Choice_inspector_links_to_its_source_and_recorded_branch_explanation()
    {
        using var model = ComponentStory.Start();
        var scene = model.Story!.GetScene(new SceneId("hall"));
        var choice = scene.Choices.Single(item => item.Key == "c0");
        InspectorSelection? sourceSelection = null;
        RuleDecision? branchDecision = null;
        var inspector = Render<ChoiceInspector>(parameters => parameters
            .Add(component => component.Story, model.Story)
            .Add(component => component.Scene, scene)
            .Add(component => component.ChoiceKey, choice.Key)
            .Add(component => component.Session, model.Session)
            .Add(component => component.Inspection, model.Inspection)
            .Add(component => component.OnShowSource,
                EventCallback.Factory.Create<InspectorSelection>(this, selection => sourceSelection = selection))
            .Add(component => component.OnShowBranchExplanation,
                EventCallback.Factory.Create<RuleDecision>(this, decision => branchDecision = decision)));

        var originalScene = model.Session!.Current.Scene;
        var originalEvents = model.Inspection!.Events.Count;
        await inspector.FindAll("button").Single(button => button.TextContent.Trim() == "Show source")
            .ClickAsync(new());
        await inspector.FindAll("button").Single(button => button.TextContent.Trim() == "Show branch explanation")
            .ClickAsync(new());

        Assert.Equal(new InspectorSelection(new SceneId("hall"), choice.Key), sourceSelection);
        Assert.Equal(model.Inspection.Decisions.Single(decision =>
            decision.Kind == DecisionKind.Choice && decision.Subject == "step:4:1"), branchDecision);
        Assert.Equal(originalScene, model.Session.Current.Scene);
        Assert.Equal(originalEvents, model.Inspection.Events.Count);
    }

    private IRenderedComponent<ChoiceInspector> ShowChoice(WorkbenchModel model, string key)
    {
        var scene = model.Story!.GetScene(new SceneId("hall"));
        var choice = model.Session!.Current.Choices.FirstOrDefault(item => item.Key == key);
        return Render<ChoiceInspector>(parameters => parameters
            .Add(component => component.Story, model.Story)
            .Add(component => component.Scene, scene)
            .Add(component => component.ChoiceKey, key)
            .Add(component => component.Choice, choice)
            .Add(component => component.Session, model.Session)
            .Add(component => component.Inspection, model.Inspection)
            .Add(component => component.CurrentScene, model.Session.Current.Scene));
    }

    private static string Status(IRenderedComponent<ChoiceInspector> inspector) =>
        Value(inspector, "Status");

    private static string Value(IRenderedComponent<ChoiceInspector> inspector, string label)
    {
        var term = inspector.FindAll("dt").Single(item => item.TextContent == label);
        return term.NextElementSibling!.TextContent;
    }
}
