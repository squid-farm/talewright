using Talewright.Debugger;
using Talewright.Talemark.Compilation;

namespace Talewright.Cli.Tests.DebuggerComponents;

/// <summary>A small story that gives every debugger component something to show.</summary>
internal static class ComponentStory
{
    public const string Yaml = """
        game:
          id: debugger-components
          title: Debugger components
          start: hall
          ruleset: {base: rpg}
        flags: [ready]
        vars:
          count: 0
          weather: "sunny"
        items:
          lantern: {name: Lantern}
        factions:
          river: {name: River Folk, standing: 1}
        characters:
          player: {first-name: Ada, last-name: Vale, inventory: [lantern]}
        triggers:
          - id: counted
            when: var:count >= 1
            do: set flag:ready
            once: true
        """;

    public const string Tale = """
        === hall
        @title Great Hall
        Count {var:count}.
        * [Prepared] (requires flag:ready) -> cave
        * [Explore] -> cave
        * [Wait] -> hall
        === cave
        @title Cave
        ~ var:count += 1
        The cave.
        * [Return] -> hall
        * [Leave] -> END
        """;

    private const string Creation = """
          ruleset: {base: rpg}
          creation:
            name: true
            stats:
              str: [12, 16]

        """;

    public static Dictionary<string, string> Files(bool creation = false) => new()
    {
        [StorySource.ManifestFileName] = creation ? Yaml.Replace("  ruleset: {base: rpg}\n", Creation) : Yaml,
        ["main.tale"] = Tale,
    };

    /// <summary>A model with the story compiled but no playtest started.</summary>
    public static WorkbenchModel Load(bool creation = false) => Load(Files(creation));

    /// <summary>A model with <paramref name="files"/> compiled but no playtest started.</summary>
    public static WorkbenchModel Load(Dictionary<string, string> files)
    {
        var source = new StorySource(files);
        var compilation = StoryCompiler.Compile(source);
        Assert.True(compilation.Success, string.Join("\n", compilation.Diagnostics));
        var model = new WorkbenchModel("unused");
        Assert.True(model.ApplySource(new SourceCompilation(source, compilation, null), restart: true));
        Assert.Null(model.Error);
        return model;
    }

    /// <summary>A story of <paramref name="count"/> untitled scenes, room001 onwards, that each loop to themselves.</summary>
    public static Dictionary<string, string> Rooms(int count) => new()
    {
        [StorySource.ManifestFileName] = "game:\n  id: rooms\n  title: Rooms\n  start: room001\ncharacters:\n  player: {first-name: Tester}\n",
        ["rooms.tale"] = string.Join("\n", Enumerable.Range(1, count).Select(i => $"=== room{i:000}\nRoom {i}.\n* [Stay] -> room{i:000}")),
    };

    /// <summary>A model with a playtest started at the hall.</summary>
    public static WorkbenchModel Start()
    {
        var model = Load();
        model.Start();
        Assert.Null(model.Error);
        Assert.NotNull(model.Session);
        return model;
    }

    /// <summary>Takes the current passage's choice whose label is <paramref name="text"/>.</summary>
    public static void Choose(WorkbenchModel model, string text)
    {
        model.Choose(model.Session!.Current.Choices.Single(choice => choice.Text == text).Key);
        Assert.Null(model.Error);
    }

    /// <summary>Applies a live edit that sets var:count, which also fires the counted trigger.</summary>
    public static void EditCount(WorkbenchModel model, int value)
    {
        var field = model.EditNumbers.Single(field => field.Label == "var:count");
        field.Enabled = true;
        field.Value = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        model.ApplyEdits();
        Assert.Null(model.Error);
    }

    /// <summary>Writes the story to <paramref name="root"/> for components that load it from disk.</summary>
    public static void WriteTo(string root)
    {
        foreach (var (path, text) in Files())
            File.WriteAllText(Path.Combine(root, path), text);
    }
}
