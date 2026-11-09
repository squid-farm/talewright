using Bunit;
using Talewright.Debugger;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class SetupFieldsTests : BunitContext
{
    [Fact]
    public void Lists_every_setup_field_under_its_kind()
    {
        using var model = ComponentStory.Load();

        var fields = Show(model);

        Assert.Equal("Prerequisite overrides", fields.Find("details > summary").TextContent);
        Assert.Equal(["Flags", "Integer variables, standings, base stats and inventory", "Text variables"],
            fields.FindAll("legend").Select(legend => legend.TextContent));
        Assert.Equal(["Override flag:ready"], Labels(fields, 0));
        var numbers = Labels(fields, 1);
        Assert.Equal(model.Numbers.Select(field => $"Override {field.Label}"), numbers);
        Assert.Contains("Override var:count", numbers);
        Assert.Contains("Override faction:river", numbers);
        Assert.Contains("Override player inventory:lantern", numbers);
        Assert.Equal(["Override var:weather"], Labels(fields, 2));
        Assert.Equal("sunny", fields.Find("textarea[aria-label='Value var:weather']").GetAttribute("value"));
        Assert.Equal("1", fields.Find("input[aria-label='Value player inventory:lantern']").GetAttribute("value"));
    }

    [Fact]
    public void Filter_matches_case_insensitively_across_all_kinds()
    {
        using var model = ComponentStory.Load();
        var fields = Show(model);

        fields.Find("input[aria-label='Filter setup fields']").Input("RIVER");
        Assert.Empty(Labels(fields, 0));
        Assert.Equal(["Override faction:river"], Labels(fields, 1));
        Assert.Empty(Labels(fields, 2));

        // Flags match on their flag: prefix, like the other fields' labels.
        fields.Find("input[aria-label='Filter setup fields']").Input("flag:");
        Assert.Equal(["Override flag:ready"], Labels(fields, 0));
        Assert.Empty(Labels(fields, 1));

        fields.Find("input[aria-label='Filter setup fields']").Input("var:");
        Assert.Empty(Labels(fields, 0));
        Assert.Equal(["Override var:count"], Labels(fields, 1));
        Assert.Equal(["Override var:weather"], Labels(fields, 2));

        fields.Find("input[aria-label='Filter setup fields']").Input("");
        Assert.Equal(model.Numbers.Count, Labels(fields, 1).Count);
    }

    [Fact]
    public void Inputs_write_back_to_the_fields()
    {
        using var model = ComponentStory.Load();
        var fields = Show(model);

        fields.Find("input[aria-label='Override flag:ready']").Change(true);
        fields.Find("input[aria-label='Value flag:ready']").Change(true);
        fields.Find("input[aria-label='Override var:count']").Change(true);
        fields.Find("input[aria-label='Value var:count']").Change("7");
        fields.Find("input[aria-label='Override var:weather']").Change(true);
        fields.Find("textarea[aria-label='Value var:weather']").Change("");

        var flag = Assert.Single(model.Flags);
        Assert.True(flag.Enabled);
        Assert.True(flag.Value);
        var count = model.Numbers.Single(field => field.Label == "var:count");
        Assert.True(count.Enabled);
        Assert.Equal("7", count.Value);
        var weather = Assert.Single(model.Texts);
        Assert.True(weather.Enabled);
        Assert.Equal("", weather.Value);
        Assert.All(model.Numbers.Where(field => field != count), field => Assert.False(field.Enabled));
    }

    [Fact]
    public void A_custom_prefix_relabels_the_filter_and_inputs()
    {
        using var model = ComponentStory.Start();

        var fields = Render<SetupFields>(parameters => parameters
            .Add(component => component.Numbers, model.EditNumbers)
            .Add(component => component.Flags, model.EditFlags)
            .Add(component => component.Texts, model.EditTexts)
            .Add(component => component.Title, "Live edit values")
            .Add(component => component.Prefix, "live"));

        Assert.Equal("Live edit values", fields.Find("details > summary").TextContent);
        Assert.NotNull(fields.Find("input[aria-label='Filter live fields']"));
        Assert.Equal(["Edit flag:ready"], Labels(fields, 0));
        Assert.NotNull(fields.Find("input[aria-label='Edit value flag:ready']"));
        Assert.NotNull(fields.Find("textarea[aria-label='Edit value var:weather']"));
        Assert.Empty(fields.FindAll("input[aria-label^='Override ']"));
    }

    private IRenderedComponent<SetupFields> Show(WorkbenchModel model) =>
        Render<SetupFields>(parameters => parameters
            .Add(component => component.Numbers, model.Numbers)
            .Add(component => component.Flags, model.Flags)
            .Add(component => component.Texts, model.Texts));

    /// <summary>The aria labels of the override checkboxes in fieldset <paramref name="index"/>.</summary>
    private static List<string> Labels(IRenderedComponent<SetupFields> fields, int index) =>
        fields.FindAll("fieldset")[index].QuerySelectorAll(".setup-row label:first-child input[type=checkbox]")
            .Select(input => input.GetAttribute("aria-label")!).ToList();
}
