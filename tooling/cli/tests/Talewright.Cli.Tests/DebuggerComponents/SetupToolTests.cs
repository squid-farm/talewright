using AngleSharp.Dom;
using Bunit;
using Bunit.TestDoubles;
using Talewright.Core.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Talewright.Debugger;
using Talewright.Player.Components;
using Talewright.Player.Library;
using Talewright.Player.Preferences;
using Talewright.Rules.Expressions;
using Talewright.Runtime;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class SetupToolTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    [Fact]
    public void Picking_a_scene_updates_the_target_field()
    {
        using var model = ComponentStory.Load();
        var tool = Show(model);
        Assert.Equal("hall", Target(tool).GetAttribute("value"));

        tool.FindAll("button").Single(button => button.TextContent == "Select cave Cave").Click();

        Assert.Equal("cave", model.Target);
        Assert.Equal("cave", Target(tool).GetAttribute("value"));
    }

    [Fact]
    public void Target_and_seed_inputs_write_back_to_the_model()
    {
        using var model = ComponentStory.Load();
        var tool = Show(model);

        Target(tool).Change("cave");
        tool.Find("input[aria-label='Seed']").Change("42");

        Assert.Equal("cave", model.Target);
        Assert.Equal("42", model.Seed);
        Assert.Same(model.Numbers, tool.FindComponent<SetupFields>().Instance.Numbers);
    }

    [Fact]
    public void Additive_reload_shows_new_setup_fields_without_enabling_them()
    {
        using var model = ComponentStory.Start();
        var files = ComponentStory.Files();
        files[StorySource.ManifestFileName] = files[StorySource.ManifestFileName]
            .Replace("  lantern: {name: Lantern}", "  lantern: {name: Lantern}\n  rope: {name: Rope}");
        files["main.tale"] = files["main.tale"]
            .Replace("The cave.", "~ var:newcount = 3\n~ set flag:newflag\nThe cave.");
        var source = new StorySource(files);

        Assert.True(model.ApplySource(new(source, StoryCompiler.Compile(source), null), yamlChanged: true));

        var tool = Show(model);
        foreach (var label in new[] { "var:newcount", "flag:newflag", "player inventory:rope" })
        {
            Assert.False(tool.Find($"input[aria-label='Override {label}']").HasAttribute("checked"));
        }
    }

    [Fact]
    public void Start_raises_its_action()
    {
        using var model = ComponentStory.Load();
        var starts = 0;
        var tool = Show(model, start: () => starts++);

        Button(tool, "Start selected scene").Click();

        Assert.Equal(1, starts);
    }

    [Fact]
    public void A_story_without_character_creation_has_no_creator()
    {
        using var model = ComponentStory.Load();

        var tool = Show(model);

        Assert.Empty(tool.FindComponents<CharacterCreator>());
        Assert.DoesNotContain(tool.FindAll("button"), button => button.TextContent == "Clear character selection");
        Assert.Empty(tool.FindAll("p[role=status]"));
    }

    [Fact]
    public async Task Character_creation_asks_for_a_character_until_one_is_selected()
    {
        using var model = ComponentStory.Load(creation: true);
        var clears = 0;
        var tool = Show(model, clear: () => clears++);

        var creator = tool.FindComponent<CharacterCreator>().Instance;
        Assert.Same(model.Story, creator.Story);
        Assert.True(creator.Compact);
        Assert.NotNull(tool.Find(".tw-creation--compact"));
        Assert.Null(creator.InitialSelection);
        Assert.Equal("Create a character before starting the selected scene. Start story above confirms the character only.",
            tool.Find("p[role=status]").TextContent);

        Button(tool, "Clear character selection").Click();
        Assert.Equal(1, clears);

        var character = new CharacterSelection { FirstName = "Bo", LastName = "", Stats = new Dictionary<string, int> { ["str"] = 12 } };
        await tool.InvokeAsync(() => creator.OnCreated.InvokeAsync(character));

        Assert.Null(model.Error);
        Assert.Equal(character.FirstName, model.Character!.FirstName);
        Assert.Equal(character.LastName, model.Character.LastName);
        Assert.Equal(character.Stats, model.Character.Stats);

        Assert.Equal("Character selected. Start the selected scene below.", tool.Find("p[role=status]").TextContent);
        Assert.Equal(model.Character, tool.FindComponent<CharacterCreator>().Instance.InitialSelection);
    }

    [Fact]
    public void Shared_chrome_scope_stops_at_the_player_component_boundary()
    {
        using var model = ComponentStory.Load(creation: true);
        var tool = Show(model);

        Assert.True(Target(tool).HasAttribute("debugger-chrome"));
        Assert.True(tool.Find("input[aria-label='Override var:count']").HasAttribute("debugger-chrome"));
        var creator = tool.FindComponent<CharacterCreator>();
        Assert.All(creator.FindAll("*"), element => Assert.False(element.HasAttribute("debugger-chrome")));
    }

    [Fact]
    public void Standalone_character_creation_does_not_opt_in_to_compact_chrome()
    {
        using var model = ComponentStory.Load(creation: true);
        var creator = Render<CharacterCreator>(parameters => parameters
            .Add(component => component.Story, model.Story!));

        Assert.False(creator.Instance.Compact);
        Assert.Empty(creator.FindAll(".tw-creation--compact"));
    }

    [Fact]
    public void Character_creator_omits_stats_hidden_in_the_initial_state()
    {
        var files = ComponentStory.Files(creation: true);
        files[StorySource.ManifestFileName] = files[StorySource.ManifestFileName].Replace(
            "  creation:", "  character-sheet:\n    stats:\n      str: flag:ready\n  creation:");
        using var model = ComponentStory.Load(files);
        var creator = Render<CharacterCreator>(parameters => parameters
            .Add(component => component.Story, model.Story!));

        Assert.Empty(creator.FindAll("select[aria-label='Strength']"));
        Assert.NotEmpty(creator.FindAll("input[aria-label='First name']"));
    }

    [Fact]
    public void Seed_changes_reset_the_debugger_character_and_update_creation_options()
    {
        var source = SeedSensitiveSource();
        var compilation = StoryCompiler.Compile(source, SeedSensitiveOptions());
        Assert.True(compilation.Success, string.Join("\n", compilation.Diagnostics));
        using var model = new WorkbenchModel("unused");
        Assert.True(model.ApplySource(new SourceCompilation(source, compilation, null), restart: true));
        model.Seed = "2";
        var tool = Show(model, start: model.Start);
        Assert.NotEmpty(tool.FindAll("select[aria-label='Strength']"));
        Button(tool, "Start story").Click();
        Assert.NotNull(model.Character);

        tool.Find("input[aria-label='Seed']").Change("1");
        Assert.Null(model.Character);
        Assert.Empty(tool.FindAll("select[aria-label='Strength']"));
        Button(tool, "Start story").Click();
        Button(tool, "Start selected scene").Click();
        Assert.Null(model.Error);
        Assert.Equal(1UL, model.Session!.Seed);
        Assert.Empty(model.Session.Character!.Stats);

        tool.Find("input[aria-label='Seed']").Change("");
        var randomSeed = model.CreationSeed;
        Assert.Null(model.Character);
        Assert.Equal(randomSeed % 2 == 0, tool.FindAll("select[aria-label='Strength']").Count > 0);
        Button(tool, "Start story").Click();
        Button(tool, "Start selected scene").Click();
        Assert.Null(model.Error);
        Assert.Equal(randomSeed, model.Session!.Seed);
    }

    [Fact]
    public async Task Player_passes_the_creation_seed_and_selection_to_the_playthrough()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        await using var preferences = new PlayerPreferencesStore(JSInterop.JSRuntime, NullLogger<PlayerPreferencesStore>.Instance);
        Services.AddSingleton(preferences);
        Services.AddSingleton<IStoryLibrary>(new SeedStoryLibrary(SeedSensitiveSource()));
        ComponentFactories.AddStub<StoryPlayer>();
        var app = Render<TalewrightApp>(parameters => parameters
            .Add(component => component.AutoStartStoryId, "seed")
            .Add(component => component.LibraryEnabled, false)
            .Add(component => component.Options, SeedSensitiveOptions()));

        var creator = app.FindComponent<CharacterCreator>().Instance;
        var seed = creator.Seed!.Value;
        Assert.Equal(seed % 2 == 0, app.FindAll("select[aria-label='Strength']").Count > 0);
        app.FindAll("button").Single(button => button.TextContent == "Start story").Click();
        var player = app.FindComponent<Stub<StoryPlayer>>().Instance.Parameters;
        Assert.Equal(seed, player.Get(component => component.Seed));
        Assert.Equal(seed % 2 == 0, player.Get(component => component.Character)!.Stats.ContainsKey("str"));
    }

    [Fact]
    public async Task Restarting_a_resumed_character_uses_the_saved_seed()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        await using var preferences = new PlayerPreferencesStore(JSInterop.JSRuntime, NullLogger<PlayerPreferencesStore>.Instance);
        Services.AddSingleton(preferences);
        var result = StoryCompiler.Compile(SeedSensitiveSource(), SeedSensitiveOptions());
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var story = result.Story!;
        var character = story.GetCreationOptions(1)!.Default(story.World.Characters[EntityId.Player]);
        var history = new StoryHistory(story, 1, character);
        var player = Render<StoryPlayer>(parameters => parameters
            .Add(component => component.Story, story)
            .Add(component => component.Seed, 2UL)
            .Add(component => component.Character, character)
            .Add(component => component.ResumeHistory, history)
            .Add(component => component.AutosaveResumeChecked, true));

        await player.InvokeAsync(() => player.FindComponent<StoryStage>().Instance.OnRestart.InvokeAsync());

        var restarted = Assert.IsType<StoryHistory>(player.FindComponent<StoryStage>().Instance.Identity);
        Assert.Equal(1UL, restarted.Seed);
        Assert.Empty(restarted.Character!.Stats);
    }

    private static StorySource SeedSensitiveSource()
    {
        var files = ComponentStory.Files(creation: true);
        files[StorySource.ManifestFileName] = files[StorySource.ManifestFileName].Replace(
            "  creation:", "  character-sheet:\n    stats:\n      str: \"@EvenSeed\"\n  creation:");
        return new StorySource(files);
    }

    private static CompilerOptions SeedSensitiveOptions() => new()
    {
        CompatibilityId = "creation-seed-components-v1",
        Predicates = new NamedPredicateRegistry().Register("EvenSeed", state => state.Seed % 2 == 0),
    };

    private sealed class SeedStoryLibrary(StorySource source) : IStoryLibrary
    {
        public Task<IReadOnlyList<StoryEntry>> ListAsync() =>
            Task.FromResult<IReadOnlyList<StoryEntry>>([new("seed", "Seed", "Test")]);
        public Task<StorySource> LoadAsync(string id) => Task.FromResult(source);
    }

    [Fact]
    public void Clearing_the_character_replaces_the_creator()
    {
        using var model = ComponentStory.Load(creation: true);
        model.SelectCharacter(new CharacterSelection { FirstName = "Bo", LastName = "", Stats = new Dictionary<string, int> { ["str"] = 12 } });
        Assert.Null(model.Error);
        var tool = Show(model, clear: model.ClearCharacter);
        var creator = tool.FindComponent<CharacterCreator>().Instance;

        Button(tool, "Clear character selection").Click();
        // The owning Workbench rerenders after its clear handler; stand in for that here.
        tool.Render();

        // CreationRevision keys the creator, so stale form state cannot survive a clear.
        Assert.NotSame(creator, tool.FindComponent<CharacterCreator>().Instance);
        Assert.Null(tool.FindComponent<CharacterCreator>().Instance.InitialSelection);
        Assert.StartsWith("Create a character before starting", tool.Find("p[role=status]").TextContent);
    }

    private IRenderedComponent<SetupTool> Show(WorkbenchModel model, Action? start = null, Action? clear = null) =>
        Render<SetupTool>(parameters => parameters
            .Add(component => component.Model, model)
            .Add(component => component.Story, model.Story!)
            .Add(component => component.OnStart, start ?? (() => { }))
            .Add(component => component.OnClearCharacter, clear ?? (() => { })));

    private static IElement Target(IRenderedComponent<SetupTool> tool) => tool.Find("input[aria-label='Target scene ID']");

    private static IElement Button(IRenderedComponent<SetupTool> tool, string text) =>
        tool.FindAll("button").Single(button => button.TextContent == text);
}
