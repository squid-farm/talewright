using System.Xml.Linq;

namespace Talewright.Player.E2E.Tests;

[Trait("Host", "Linux")]
[Collection(BrowserCollection.Name)]
public sealed class DebuggerTests
{
    [Fact]
    public async Task Native_map_tab_exposes_scenes_diverts_and_hub_offers_to_the_keyboard()
    {
        var executable = Environment.GetEnvironmentVariable("TALEWRIGHT_DEBUGGER_EXECUTABLE")
            ?? throw new InvalidOperationException("Set TALEWRIGHT_DEBUGGER_EXECUTABLE.");
        var root = Path.Combine(Path.GetTempPath(), "talewright-map-debug-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var artifacts = Path.Combine(Environment.GetEnvironmentVariable("TALEWRIGHT_TEST_RESULTS") ?? root,
            "debugger-map", Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "story.talewright.yaml"),
                "game:\n  id: map-debug\n  title: Map Debug\n  start: start\nflags: [ready, lit]\ncharacters:\n  player: {first-name: Tester}\n");
            await File.WriteAllTextAsync(Path.Combine(root, "main.tale"), """
                === start
                @title The gate
                {if flag:ready}
                -> camp
                {/if}
                * [Wait] -> END
                === camp
                @title Camp
                @hub fire
                * [Leave] -> END
                === well
                @title Well
                @tags fire
                @requires flag:lit
                @once
                -> camp
                === orphan
                @title Orphan
                -> camp
                """);
            await using var driver = new NativePlayerDriver(artifacts, executable);
            await driver.StartAsync(root);
            var snapshot = await WaitFor(driver, "Scene setup");
            Assert.Contains(snapshot.Descendants(), node => node.Role == "page tab" && node.Name == "Map");
            Assert.DoesNotContain("Scenes on the map", snapshot.Content);

            await driver.ActivateAsync("Play");
            await driver.PressAsync("Right");
            snapshot = await WaitFor(driver, "4 scenes, 5 diverts, 1 hub offer, 1 unreachable.");
            Assert.DoesNotContain("Scene setup", snapshot.Content);
            Assert.Contains(snapshot.Descendants(), node => node.Role == "push button" && node.Name == "Export SVG");
            Assert.Contains(snapshot.Descendants(), node => node.Role == "push button" && node.Name == "Export PNG");
            Assert.Equal("Map", Focused(snapshot).Name);

            await driver.PressAsync("Tab");
            await driver.PressAsync("Tab");
            snapshot = await WaitFor(driver, "Diverts: 2");
            Assert.Equal("The gate (start), start", Focused(snapshot).Name);
            Assert.Contains("Divert to camp, if flag:ready, main.tale line 4", snapshot.Content);
            Assert.Contains("Choice c0 “Wait” to END, main.tale line 6", snapshot.Content);

            await driver.PressAsync("Down");
            snapshot = await WaitFor(driver, "Hub offers: 1");
            Assert.Equal("Camp (camp), hub", Focused(snapshot).Name);
            const string hubOffer = "Hub offer of Well (well) via #fire, once, if flag:lit";
            Assert.Contains(hubOffer, snapshot.Content);
            await driver.PressAsync("Tab");
            await driver.PressAsync("Tab");
            snapshot = await WaitFor(driver, hubOffer);
            Assert.Equal(hubOffer, Focused(snapshot).Name);

            var tale = Path.Combine(root, "main.tale");
            var source = await File.ReadAllTextAsync(tale);
            await File.WriteAllTextAsync(tale, source.Replace("@title Orphan", "@title Updated orphan"));
            snapshot = await WaitFor(driver, "Updated orphan (orphan), unreachable");
            Assert.Equal(hubOffer, Focused(snapshot).Name);
            await File.WriteAllTextAsync(tale, source);
            snapshot = await WaitFor(driver, "Orphan (orphan), unreachable");
            Assert.Equal(hubOffer, Focused(snapshot).Name);
            await driver.ActivateAsync(hubOffer);
            snapshot = await WaitFor(driver, "Well (well), once, requires flag:lit");
            Assert.Equal("Well (well), once, requires flag:lit", Focused(snapshot).Name);

            await driver.PressAsync("End");
            snapshot = await WaitFor(driver, "Diverts: 0");
            Assert.Equal("END", Focused(snapshot).Name);
            await driver.PressAsync("Up");
            snapshot = await WaitFor(driver, "Orphan (orphan), unreachable");
            Assert.Equal("Orphan (orphan), unreachable", Focused(snapshot).Name);
            Assert.Contains("Divert to camp, main.tale line 19", snapshot.Content);

            await driver.PressAsync("Home");
            await WaitFor(driver, "Diverts: 2");
            await driver.ActivateAsync("Divert to camp, if flag:ready, main.tale line 4");
            snapshot = await WaitFor(driver, "Hub offers: 1");
            Assert.Equal("Camp (camp), hub", Focused(snapshot).Name);
            await driver.ActivateAsync("Hub offer of Well (well) via #fire, once, if flag:lit");
            snapshot = await WaitFor(driver, "Well (well), once, requires flag:lit");
            Assert.Equal("Well (well), once, requires flag:lit", Focused(snapshot).Name);

            await driver.PressAsync("Up");
            snapshot = await WaitFor(driver, "Hub offers: 1");
            Assert.Equal("Camp (camp), hub", Focused(snapshot).Name);
            await driver.PressAsync("Tab");
            await driver.PressAsync("Tab");
            snapshot = await WaitFor(driver, hubOffer);
            Assert.Equal(hubOffer, Focused(snapshot).Name);
            await File.WriteAllTextAsync(tale, source.Replace("@tags fire", "@tags frost"));
            snapshot = await WaitFor(driver, "4 scenes, 5 diverts, 0 hub offers, 2 unreachable.");
            Assert.Equal("Camp (camp), hub", Focused(snapshot).Name);

            await driver.ActivateAsync("Play");
            snapshot = await WaitFor(driver, "Scene setup");
            Assert.DoesNotContain("Scenes on the map", snapshot.Content);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Native_map_previews_setup_and_current_run_state_and_updates_its_route()
    {
        var executable = Environment.GetEnvironmentVariable("TALEWRIGHT_DEBUGGER_EXECUTABLE")
            ?? throw new InvalidOperationException("Set TALEWRIGHT_DEBUGGER_EXECUTABLE.");
        var root = Path.Combine(Path.GetTempPath(), "talewright-map-route-debug-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var artifacts = Path.Combine(Environment.GetEnvironmentVariable("TALEWRIGHT_TEST_RESULTS") ?? root,
            "debugger-map-route", Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "story.talewright.yaml"),
                "game:\n  id: map-route-debug\n  title: Map Route Debug\n  start: start\nvars: {visits: 0}\ncharacters:\n  player: {first-name: Tester}\n");
            await File.WriteAllTextAsync(Path.Combine(root, "main.tale"), """
                === start
                @title The gate
                ~ var:visits += 1
                Visits {var:visits}.
                * [Continue] -> next
                === next
                @title The road
                Visits {var:visits}.
                * [Finish] -> END
                """);

            await using var driver = new NativePlayerDriver(artifacts, executable);
            await driver.StartAsync(root);
            await WaitFor(driver, "Scene setup");
            await driver.ActivateAsync("Map");
            await WaitFor(driver, "2 scenes, 2 diverts, 0 hub offers, 0 unreachable.");
            await driver.PressAsync("Tab");
            await driver.PressAsync("Tab");
            var snapshot = await WaitFor(driver, "Diverts: 1");
            Assert.Contains("Scene preview: The gate", snapshot.Content);
            Assert.Contains("Preview only, using scene setup values.", snapshot.Content);
            Assert.Contains("Visits 0.", snapshot.Content);

            await driver.ActivateAsync("Play");
            await driver.ClickAsync("Start selected scene");
            await WaitFor(driver, "Visits 1.");
            await driver.ActivateAsync("Map");
            snapshot = await WaitFor(driver, "Current scene: start.");
            Assert.Contains("Preview only, using the current playtest state.", snapshot.Content);
            Assert.Contains("Visits 1.", snapshot.Content);
            Assert.Contains("Started at start", snapshot.Content);

            await driver.ActivateAsync("Play");
            await driver.ChooseAsync("Continue");
            await WaitFor(driver, "The road");
            await driver.ActivateAsync("Map");
            snapshot = await WaitFor(driver, "Current scene: next.");
            Assert.Contains("Continue", snapshot.Content);
            Assert.Contains("next", snapshot.Content);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Native_map_clears_preview_when_reload_removes_selected_scene()
    {
        var executable = Environment.GetEnvironmentVariable("TALEWRIGHT_DEBUGGER_EXECUTABLE")
            ?? throw new InvalidOperationException("Set TALEWRIGHT_DEBUGGER_EXECUTABLE.");
        var root = Path.Combine(Path.GetTempPath(), "talewright-map-reload-debug-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var artifacts = Path.Combine(Environment.GetEnvironmentVariable("TALEWRIGHT_TEST_RESULTS") ?? root,
            "debugger-map-reload", Guid.NewGuid().ToString("N"));
        var tale = Path.Combine(root, "main.tale");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "story.talewright.yaml"),
                "game:\n  id: map-reload-debug\n  title: Map Reload Debug\n  start: start\ncharacters:\n  player: {first-name: Tester}\n");
            await File.WriteAllTextAsync(tale, """
                === start
                @title The start
                * [Continue] -> next
                === next
                @title The old scene
                Old scene body.
                * [Finish] -> END
                """);

            await using var driver = new NativePlayerDriver(artifacts, executable);
            await driver.StartAsync(root);
            await WaitFor(driver, "Scene setup");
            await driver.ActivateAsync("Map");
            await WaitFor(driver, "2 scenes, 2 diverts, 0 hub offers, 0 unreachable.");
            await driver.PressAsync("Tab");
            await driver.PressAsync("Tab");
            await driver.PressAsync("Home");
            await driver.PressAsync("Right");
            var snapshot = await WaitFor(driver, "Scene preview: The old scene", "Old scene body.");

            await File.WriteAllTextAsync(tale, """
                === start
                @title The start
                The updated story.
                * [Finish] -> END
                """);
            snapshot = await WaitFor(driver, "1 scene, 1 divert, 0 hub offers, 0 unreachable.");
            Assert.DoesNotContain("Scene preview:", snapshot.Content);
            Assert.DoesNotContain("Old scene body.", snapshot.Content);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AccessibleNode Focused(AccessibleNode snapshot) =>
        snapshot.Descendants().Last(node => node.Focused);

    [Fact]
    public async Task Native_workbench_shows_textless_choices_as_continue()
    {
        var executable = Environment.GetEnvironmentVariable("TALEWRIGHT_DEBUGGER_EXECUTABLE")
            ?? throw new InvalidOperationException("Set TALEWRIGHT_DEBUGGER_EXECUTABLE.");
        var root = Path.Combine(Path.GetTempPath(), "talewright-textless-debug-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var artifacts = Path.Combine(Environment.GetEnvironmentVariable("TALEWRIGHT_TEST_RESULTS") ?? root,
            "debugger-textless", Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "story.talewright.yaml"),
                "game:\n  id: textless-debug\n  title: Textless Debug\n  start: start\nflags: [ready]\ncharacters:\n  player: {first-name: Tester}\n");
            await File.WriteAllTextAsync(Path.Combine(root, "main.tale"), """
                === start
                Textless start.
                * [Locked] (requires: flag:ready) -> END
                * -> next
                === next
                Textless destination.
                * -> END
                """);
            await using var driver = new NativePlayerDriver(artifacts, executable);
            await driver.StartAsync(root);
            await WaitFor(driver, "Scene setup");
            await driver.ClickAsync("Start selected scene");
            var snapshot = await WaitFor(driver, "Textless start.");
            Assert.Contains("Continue", snapshot.Content);
            await driver.ChooseAsync("Continue");
            snapshot = await WaitFor(driver, "Textless destination.");
            Assert.Contains("Continue", snapshot.Content);
            await driver.ChooseAsync("Continue");
            await WaitFor(driver, "Play again");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Native_workbench_jumps_with_setup_inspects_branches_and_recovers_from_invalid_source()
    {
        var executable = Environment.GetEnvironmentVariable("TALEWRIGHT_DEBUGGER_EXECUTABLE")
            ?? throw new InvalidOperationException("Set TALEWRIGHT_DEBUGGER_EXECUTABLE to the built or installed debugger DLL.");
        var root = Path.Combine(Path.GetTempPath(), "talewright-debug-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var artifacts = Path.Combine(Environment.GetEnvironmentVariable("TALEWRIGHT_TEST_RESULTS") ?? root,
            "debugger", Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "story.talewright.yaml"),
                "game:\n  id: native-debug\n  title: Native Debug\n  start: scene1\nflags: [ready]\ncharacters:\n  player: {first-name: Tester}\n");
            var tale = Path.Combine(root, "main.tale");
            await File.WriteAllTextAsync(tale, "=== scene1\n-> missing");
            await using var driver = new NativePlayerDriver(artifacts, executable);
            await driver.StartAsync(root);
            await WaitFor(driver, "Story has compiler errors");
            await File.WriteAllTextAsync(tale, string.Join("\n", Enumerable.Range(1, 1000).Select(i => $$"""
                === scene{{i}}
                @title Chapter {{i}}
                Late scene {{i}}.
                * [Prepared] (requires: flag:ready) -> END
                * [Wait] -> END
                """)));
            await WaitFor(driver, "Scene setup");
            await driver.FillAsync("Search scenes", "Chapter 1000");
            await WaitFor(driver, "Select scene1000 Chapter 1000");
            await driver.ClickAsync("Select scene1000 Chapter 1000");
            await driver.FillAsync("Seed", "42");
            await driver.ActivateAsync("Prerequisite overrides");
            await driver.ActivateAsync("Override flag:ready");
            await driver.ActivateAsync("Value flag:ready");
            await driver.ClickAsync("Start selected scene");
            var snapshot = await WaitFor(driver, "Late scene 1000.");
            Assert.Contains("Synthetic history", snapshot.Content);
            Assert.Contains("Seed: 42", snapshot.Content);
            await driver.FillAsync("Filter decisions", "step:4999:1");
            snapshot = await WaitFor(driver, "ConditionTrue");
            Assert.Contains("Offered", snapshot.Content);
            await driver.ActivateAsync("Choice scene1000 step:4999:1: Offered (ConditionTrue)");
            snapshot = await WaitFor(driver, "Events [");
            Assert.Contains("ready", snapshot.Content);
            Assert.Contains("Source: main.tale:4999:1", snapshot.Content);
            await driver.ChooseAsync("Prepared");
            await WaitFor(driver, "Play again");
            await File.WriteAllTextAsync(tale, "=== scene1\n-> missing");
            snapshot = await WaitFor(driver, "Source not adopted", "Play again");
            Assert.Contains("Play again", snapshot.Content);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Native_watch_reload_preserves_entry_state_and_gates_yaml_until_explicit_restart()
    {
        var executable = Environment.GetEnvironmentVariable("TALEWRIGHT_DEBUGGER_EXECUTABLE")
            ?? throw new InvalidOperationException("Set TALEWRIGHT_DEBUGGER_EXECUTABLE.");
        var root = Path.Combine(Path.GetTempPath(), "talewright-watch-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var artifacts = Path.Combine(Environment.GetEnvironmentVariable("TALEWRIGHT_TEST_RESULTS") ?? root,
            "debugger-reload", Guid.NewGuid().ToString("N"));
        const string yaml = "game:\n  id: reload\n  title: Native reload\n  start: start\n  creation:\n    name: true\nvars: {count: 0}\ncharacters:\n  player: {first-name: Tester}\n";
        const string original = """
                === start
                * [Go] -> current
                === current
                Before reload. {player:first-name}.
                ~ var:count += 1
                * [Finish] -> END
                """;
        var tale = Path.Combine(root, "main.tale");
        var manifest = Path.Combine(root, "story.talewright.yaml");
        try
        {
            await File.WriteAllTextAsync(manifest, yaml);
            await File.WriteAllTextAsync(tale, original);
            await using var driver = new NativePlayerDriver(artifacts, executable);
            await driver.StartAsync(root);
            await WaitFor(driver, "Scene setup");
            await driver.FillAsync("Seed", "42");
            await driver.FillAsync("First name", "Alex");
            await driver.ClickAsync("Start story");
            await driver.ClickAsync("Start selected scene");
            await driver.ChooseAsync("Go");
            await WaitFor(driver, "Before reload.");
            await File.WriteAllTextAsync(tale, "=== start\n-> missing");
            var snapshot = await WaitFor(driver, "Story has compiler errors", "Before reload.");
            Assert.Contains("Before reload.", snapshot.Content);
            var temp = Path.Combine(root, "editor.tmp");
            var updated = original.Replace("Before reload.", "After reload.").Replace("count += 1", "count += 2");
            await File.WriteAllTextAsync(temp, updated);
            File.Move(temp, tale, overwrite: true);
            snapshot = await WaitFor(driver, "After reload. Alex.", "Reload boundary: current", "Seed: 42");
            Assert.Contains("Reload boundary: current", snapshot.Content);
            Assert.Contains("Seed: 42", snapshot.Content);
            await driver.ActivateAsync("Flags, variables, factions, visits and triggers");
            snapshot = await WaitFor(driver, "var:count (integer) = 2");
            Assert.Contains("current visits: 1", snapshot.Content);
            await File.WriteAllTextAsync(manifest, yaml.Replace("Native reload", "Restarted title"));
            snapshot = await WaitFor(driver, "Restart required.", "After reload.");
            Assert.Contains("After reload.", snapshot.Content);
            await File.WriteAllTextAsync(tale, updated.Replace("After reload.", "After YAML restart."));
            snapshot = await WaitFor(driver, "Source compiled, but explicit Restart from source is required");
            Assert.DoesNotContain("After YAML restart.", snapshot.Content);
            await driver.ClickAsync("Restart from source");
            await WaitFor(driver, "Restarted title");
            await driver.ClickAsync("Start story");
            await driver.ClickAsync("Start selected scene");
            await driver.ChooseAsync("Go");
            snapshot = await WaitFor(driver, "After YAML restart. Alex.");
            Assert.Contains("Seed: 42", snapshot.Content);
            await File.WriteAllTextAsync(tale, updated.Replace("[Go]", "[Changed positional meaning]")
                .Replace("After reload.", "Incompatible progress."));
            snapshot = await WaitFor(driver, "previously consumed scene/choice meaning changed", "After YAML restart. Alex.");
            Assert.Contains("After YAML restart.", snapshot.Content);
            Assert.DoesNotContain("Incompatible progress.", snapshot.Content);
            Assert.Equal(0, await driver.CloseWindowAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
    [Fact]
    public async Task Native_live_edits_jumps_navigation_and_reload_keep_a_synthetic_timeline()
    {
        var executable = Environment.GetEnvironmentVariable("TALEWRIGHT_DEBUGGER_EXECUTABLE")
            ?? throw new InvalidOperationException("Set TALEWRIGHT_DEBUGGER_EXECUTABLE.");
        var root = Path.Combine(Path.GetTempPath(), "talewright-edit-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var artifacts = Path.Combine(Environment.GetEnvironmentVariable("TALEWRIGHT_TEST_RESULTS") ?? root,
            "debugger-edits", Guid.NewGuid().ToString("N"));
        const string tale = """
            === start
            ~ var:count += 1
            Live count {var:count}.
            * [Prepared] (requires: flag:ready) -> target
            * [Wait] -> start
            === target
            @requires not flag:ready
            ~ var:count += 2
            Target count {var:count}.
            * [Return] -> start
            """;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "story.talewright.yaml"), """
                game:
                  id: live-native
                  title: Native live edits
                  start: start
                flags: [ready]
                vars: {count: 0}
                characters:
                  player: {first-name: Tester}
                triggers:
                  - id: reward
                    when: flag:ready
                    do: var:count += 10
                    once: false
                """);
            var path = Path.Combine(root, "main.tale");
            await File.WriteAllTextAsync(path, tale);
            await using var driver = new NativePlayerDriver(artifacts, executable);
            await driver.StartAsync(root);
            await WaitFor(driver, "Scene setup");
            await driver.FillAsync("Seed", "42");
            await driver.ClickAsync("Start selected scene");
            await WaitFor(driver, "Live count 1.");
            await driver.ActivateAsync("Live edit values");
            await driver.ActivateAsync("Edit flag:ready");
            await driver.ActivateAsync("Edit value flag:ready");
            await driver.ActivateAsync("Edit var:count");
            await driver.FillAsync("Edit value var:count", "7");
            await driver.ClickAsync("Apply live edit batch");
            var snapshot = await WaitFor(driver, "Live count 17.", "Position 1 / 1.");
            Assert.Contains("Prepared", snapshot.Content);
            await driver.ActivateAsync("Command history and consequences");
            snapshot = await WaitFor(driver, "var:count: 1 to 17", "TriggerFired");
            Assert.Contains("reward", snapshot.Content);
            await driver.FillAsync("Target scene ID", "target");
            await driver.ClickAsync("Jump retaining state");
            await WaitFor(driver, "Target count 19.", "Position 2 / 2.");
            await driver.ClickAsync("Back command");
            await WaitFor(driver, "Live count 17.", "Position 1 / 2.");
            await driver.ClickAsync("Forward command");
            await WaitFor(driver, "Target count 19.", "Position 2 / 2.");
            await File.WriteAllTextAsync(path, tale.Replace("Target count", "Reloaded target count"));
            snapshot = await WaitFor(driver, "Reloaded target count 19.", "Verified command prefix retained");
            Assert.Contains("Position 2 / 2.", snapshot.Content);
            Assert.Contains("eligibility bypassed", snapshot.Content);
            Assert.Equal(0, await driver.CloseWindowAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Native_source_view_follows_ranges_and_keeps_accepted_text_when_disk_reload_is_rejected()
    {
        var executable = Environment.GetEnvironmentVariable("TALEWRIGHT_DEBUGGER_EXECUTABLE")
            ?? throw new InvalidOperationException("Set TALEWRIGHT_DEBUGGER_EXECUTABLE.");
        var root = Path.Combine(Path.GetTempPath(), "talewright-source-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var artifacts = Path.Combine(Environment.GetEnvironmentVariable("TALEWRIGHT_TEST_RESULTS") ?? root,
            "debugger-source", Guid.NewGuid().ToString("N"));
        const string first = "// FIRST FILE SOURCE\n=== first\n* [Go] -> late";
        var late = "=== other\n* [Wait] -> other\n"
            + string.Join("\n", Enumerable.Range(1, 205).Select(i => $"// padding {i}"))
            + "\n=== late\n// OLD ACCEPTED SOURCE\n* [Return] -> first\n";
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "story.talewright.yaml"),
                "game:\n  id: source-native\n  title: Native Source\n  start: first\ncharacters:\n  player: {first-name: Tester}\n");
            var firstPath = Path.Combine(root, "first.tale");
            var latePath = Path.Combine(root, "late.tale");
            await File.WriteAllTextAsync(firstPath, first);
            await File.WriteAllTextAsync(latePath, late);
            await using var driver = new NativePlayerDriver(artifacts, executable);
            await driver.StartAsync(root);
            await WaitFor(driver, "Scene setup");
            await driver.ClickAsync("Start selected scene");
            await WaitFor(driver, "Accepted file: first.tale", "lines 2 to 3 (inclusive)", "// FIRST FILE SOURCE");
            await driver.ChooseAsync("Go");
            await WaitFor(driver, "Accepted file: late.tale", "lines 208 to 211 (inclusive)", "// OLD ACCEPTED SOURCE");
            await driver.ClickAsync("Previous source lines");
            var snapshot = await WaitFor(driver, "Showing accepted lines 105 to 204 of 211");
            Assert.DoesNotContain("// OLD ACCEPTED SOURCE", snapshot.Content);
            await driver.ClickAsync("Go to current scene source");
            await WaitFor(driver, "Showing accepted lines 205 to 211 of 211", "// OLD ACCEPTED SOURCE");
            await driver.ClickAsync("Back command");
            await WaitFor(driver, "Accepted file: first.tale", "// FIRST FILE SOURCE");
            await driver.ClickAsync("Forward command");
            await WaitFor(driver, "Accepted file: late.tale", "// OLD ACCEPTED SOURCE");

            await File.WriteAllTextAsync(latePath, "// inserted line\n" + late.Replace("OLD ACCEPTED SOURCE", "NEW ACCEPTED SOURCE"));
            await WaitFor(driver, "lines 209 to 212 (inclusive)", "// NEW ACCEPTED SOURCE", "Verified command prefix retained");
            await File.WriteAllTextAsync(latePath, "=== late\n// INVALID DISK SOURCE\n-> missing");
            snapshot = await WaitFor(driver, "Story has compiler errors", "// NEW ACCEPTED SOURCE", "not adopted");
            Assert.DoesNotContain("// INVALID DISK SOURCE", snapshot.Content);
            await File.WriteAllTextAsync(firstPath, first.Replace("[Go]", "[Changed meaning]"));
            await File.WriteAllTextAsync(latePath, late.Replace("OLD ACCEPTED SOURCE", "REJECTED DISK SOURCE"));
            snapshot = await WaitFor(driver, "previously consumed scene/choice meaning changed", "// NEW ACCEPTED SOURCE");
            Assert.DoesNotContain("// REJECTED DISK SOURCE", snapshot.Content);
            Assert.Equal(0, await driver.CloseWindowAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Native_return_to_same_source_window_resets_its_scrolled_viewport()
    {
        var executable = Environment.GetEnvironmentVariable("TALEWRIGHT_DEBUGGER_EXECUTABLE")
            ?? throw new InvalidOperationException("Set TALEWRIGHT_DEBUGGER_EXECUTABLE.");
        var root = Path.Combine(Path.GetTempPath(), "talewright-source-scroll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var artifacts = Path.Combine(Environment.GetEnvironmentVariable("TALEWRIGHT_TEST_RESULTS") ?? root,
            "debugger-source-scroll", Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "story.talewright.yaml"),
                "game:\n  id: scroll\n  title: Source Scroll\n  start: first\ncharacters:\n  player: {first-name: Tester}\n");
            await File.WriteAllTextAsync(Path.Combine(root, "main.tale"), "=== first\n"
                + string.Join("\n", Enumerable.Range(1, 150).Select(i => $"// source line {i}"))
                + "\n* [Stay] -> first");
            await using var driver = new NativePlayerDriver(artifacts, executable);
            await driver.StartAsync(root);
            await WaitFor(driver, "Scene setup");
            await driver.ClickAsync("Start selected scene");
            await WaitFor(driver, "Showing accepted lines 1 to 100 of 152");
            await driver.ActivateAsync("Read-only accepted source lines");
            await driver.PressAsync("End");
            await WaitForDeclaration(scrolledOut: true);
            await driver.ClickAsync("Go to current scene source");
            await WaitForDeclaration(scrolledOut: false);
            await driver.ClickAsync("Next source lines");
            await WaitFor(driver, "Showing accepted lines 101 to 152 of 152");
            await driver.ClickAsync("Previous source lines");
            await WaitForDeclaration(scrolledOut: false);
            Assert.Equal(0, await driver.CloseWindowAsync());

            async Task WaitForDeclaration(bool scrolledOut)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (true)
                {
                    var snapshot = await driver.SnapshotAsync();
                    var viewport = snapshot.Descendants().First(node => node.Name == "Read-only accepted source lines");
                    var declaration = viewport.Descendants().First(node => node.Text == "=== first" || node.Name == "=== first");
                    var aboveViewport = declaration.Bounds.Y < viewport.Bounds.Y;
                    var withinViewport = declaration.Bounds.Y >= viewport.Bounds.Y
                        && declaration.Bounds.Y + declaration.Bounds.Height <= viewport.Bounds.Y + viewport.Bounds.Height;
                    if (scrolledOut ? aboveViewport : withinViewport)
                    {
                        return;
                    }
                    await Task.Delay(100, timeout.Token);
                }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Native_portable_scenario_replays_in_another_process_after_setup_reload_edit_and_dice()
    {
        var executable = Environment.GetEnvironmentVariable("TALEWRIGHT_DEBUGGER_EXECUTABLE")
            ?? throw new InvalidOperationException("Set TALEWRIGHT_DEBUGGER_EXECUTABLE.");
        var root = Path.Combine(Path.GetTempPath(), "talewright-scenario-native-" + Guid.NewGuid().ToString("N"));
        var story = Path.Combine(root, "story");
        Directory.CreateDirectory(story);
        var artifacts = Path.Combine(Environment.GetEnvironmentVariable("TALEWRIGHT_TEST_RESULTS") ?? root,
            "debugger-scenarios", Guid.NewGuid().ToString("N"));
        var scenario = Path.Combine(root, "portable.twscenario");
        var secondExport = Path.Combine(root, "second.twscenario");
        var tale = Path.Combine(story, "main.tale");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(story, "story.talewright.yaml"), """
                game:
                  id: native-portable
                  title: Portable Native
                  start: scene1
                  creation:
                    name: true
                flags: [ready]
                vars: {count: 0}
                characters:
                  player: {first-name: Tester}
                """);
            var source = string.Join("\n", Enumerable.Range(1, 1000).Select(i => $$"""
                === scene{{i}}
                @title Chapter {{i}}
                Before scene{{i}}. Count {var:count}. {player:first-name}.
                * [Prepared] (requires: flag:ready) -> check
                * [Wait] -> END
                """)) + """

                === check
                Check passage.
                ::: skill-check athletics dc 10
                success: -> finish
                failure: -> finish
                :::
                === finish
                * [Return] -> scene1
                """;
            await File.WriteAllTextAsync(tale, source);
            await using (var driver = new NativePlayerDriver(artifacts, executable))
            {
                await driver.StartAsync(story);
                await WaitFor(driver, "Scene setup");
                await driver.FillAsync("Search scenes", "Chapter 1000");
                await WaitFor(driver, "Select scene1000 Chapter 1000");
                await driver.ClickAsync("Select scene1000 Chapter 1000");
                await driver.FillAsync("Seed", "42");
                await driver.FillAsync("First name", "Robin");
                await driver.ClickAsync("Start story");
                await driver.ActivateAsync("Prerequisite overrides");
                await driver.ActivateAsync("Override flag:ready");
                await driver.ActivateAsync("Value flag:ready");
                await driver.ActivateAsync("Override var:count");
                await driver.FillAsync("Value var:count", "7");
                await driver.ClickAsync("Start selected scene");
                await WaitFor(driver, "Before scene1000. Count 7. Robin.");
                await driver.FillAsync("Filter decisions", "step:4999:1");
                await WaitFor(driver, "ConditionTrue", "Offered");
                source = source.Replace("Before scene1000.", "After scene1000.");
                await File.WriteAllTextAsync(tale, source);
                await WaitFor(driver, "After scene1000. Count 7. Robin.", "Reload boundary: scene1000");
                await driver.ActivateAsync("Live edit values");
                await driver.ActivateAsync("Edit var:count");
                await driver.FillAsync("Edit value var:count", "19");
                await driver.ClickAsync("Apply live edit batch");
                await WaitFor(driver, "After scene1000. Count 19. Robin.", "Position 1 / 1.");
                await driver.ChooseAsync("Prepared");
                await WaitFor(driver, "Check passage.", "Rolls: 1", "Position 2 / 2.");
                await driver.ClickAsync("Jump retaining state");
                await WaitFor(driver, "After scene1000. Count 19. Robin.", "Position 3 / 3.", "Rolls: 1");
                await driver.ClickAsync("Back command");
                await WaitFor(driver, "Check passage.", "Position 2 / 3.", "Rolls: 1");
                await driver.FillAsync("Scenario file path", scenario);
                await driver.ClickAsync("Export synthetic scenario");
                await WaitFor(driver, "Synthetic scenario exported:");
                await driver.ClickAsync("Export synthetic scenario");
                await WaitFor(driver, "Scenario operation failed:");
                Assert.Equal(0, await driver.CloseWindowAsync());
            }
            var bytes = await File.ReadAllBytesAsync(scenario);
            await using (var driver = new NativePlayerDriver(artifacts + "-reopened", executable))
            {
                await driver.StartAsync(story);
                await WaitFor(driver, "Scene setup");
                await driver.FillAsync("Scenario file path", scenario);
                await driver.ClickAsync("Import exact scenario");
                await WaitFor(driver, "Exact synthetic scenario imported", "Check passage.", "Position 2 / 3.", "Rolls: 1", "Seed: 42");
                await driver.FillAsync("Scenario file path", secondExport);
                await driver.ClickAsync("Export synthetic scenario");
                await WaitFor(driver, "Synthetic scenario exported:");
                Assert.Equal(bytes, await File.ReadAllBytesAsync(secondExport));
                await driver.ClickAsync("Forward command");
                await WaitFor(driver, "After scene1000. Count 19. Robin.", "Position 3 / 3.", "Rolls: 1");
                Assert.Equal(0, await driver.CloseWindowAsync());
            }
            Assert.Equal(bytes, await File.ReadAllBytesAsync(scenario));
            Assert.Equal(source, await File.ReadAllTextAsync(tale));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // Test-only deviation: exports bypass the native save dialog. See MapExportDialog.TestExportDirectoryVariable.
    private static string MapExportPath(string extension) =>
        Path.Combine(Environment.GetEnvironmentVariable("TALEWRIGHT_TEST_ONLY_MAP_EXPORT_DIRECTORY")
            ?? throw new InvalidOperationException("Run native tests through player/tests/run_e2e.py."),
            $"story-map.{extension}");

    [Fact]
    public async Task Native_map_exports_full_svg_and_png_with_the_current_run()
    {
        var executable = Environment.GetEnvironmentVariable("TALEWRIGHT_DEBUGGER_EXECUTABLE")
            ?? throw new InvalidOperationException("Set TALEWRIGHT_DEBUGGER_EXECUTABLE.");
        var root = Path.Combine(Path.GetTempPath(), "talewright-map-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var artifacts = Path.Combine(Environment.GetEnvironmentVariable("TALEWRIGHT_TEST_RESULTS") ?? root,
            "debugger-map-export", Guid.NewGuid().ToString("N"));
        var svgPath = MapExportPath("svg");
        var pngPath = MapExportPath("png");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "story.talewright.yaml"),
                "game:\n  id: map-export\n  title: Map Export\n  start: start\ncharacters:\n  player: {first-name: Tester}\n");
            await File.WriteAllTextAsync(Path.Combine(root, "main.tale"), """
                === start
                @title Beginning
                * [Continue] -> next
                === next
                @title Ending
                @hub fire
                * [Finish] -> END
                === camp
                @title Offered camp
                @tags fire
                -> next
                === orphan
                @title Unreachable scene
                -> END
                """);
            await using var driver = new NativePlayerDriver(artifacts, executable);
            await driver.StartAsync(root);
            await WaitFor(driver, "Scene setup");
            await driver.ClickAsync("Start selected scene");
            await WaitFor(driver, "Beginning");
            await driver.ChooseAsync("Continue");
            await WaitFor(driver, "Ending");
            await driver.ActivateAsync("Map");
            await WaitFor(driver, "Current scene: next");

            await driver.ClickAsync("Export SVG");
            await WaitFor(driver, $"Saved map to {svgPath}");
            var svg = System.Xml.Linq.XDocument.Load(svgPath);
            XNamespace svgNamespace = "http://www.w3.org/2000/svg";
            var labels = svg.Descendants(svgNamespace + "text").Select(node => node.Value).ToArray();
            Assert.Contains("start", labels);
            Assert.Contains("next", labels);
            Assert.Contains("camp", labels);
            Assert.Contains("orphan", labels);
            Assert.Contains("END", labels);
            Assert.Contains(labels, label => label.Contains("CURRENT", StringComparison.Ordinal));
            Assert.Contains(labels, label => label.Contains("#fire", StringComparison.Ordinal));

            await driver.ClickAsync("Export PNG");
            await WaitFor(driver, $"Saved map to {pngPath}");
            var png = await File.ReadAllBytesAsync(pngPath);
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
            var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
            var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
            Assert.Equal(int.Parse(svg.Root!.Attribute("width")!.Value), width);
            Assert.Equal(int.Parse(svg.Root.Attribute("height")!.Value), height);
            Assert.InRange(width, 1, 8192);
            Assert.InRange(height, 1, 8192);
            Assert.True((long)width * height <= 16_777_216);
        }
        finally
        {
            File.Delete(svgPath);
            File.Delete(pngPath);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Native_map_rejects_oversized_png_and_can_export_the_same_map_as_svg()
    {
        var executable = Environment.GetEnvironmentVariable("TALEWRIGHT_DEBUGGER_EXECUTABLE")
            ?? throw new InvalidOperationException("Set TALEWRIGHT_DEBUGGER_EXECUTABLE.");
        var root = Path.Combine(Path.GetTempPath(), "talewright-map-large-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var artifacts = Path.Combine(Environment.GetEnvironmentVariable("TALEWRIGHT_TEST_RESULTS") ?? root,
            "debugger-map-large", Guid.NewGuid().ToString("N"));
        var pngPath = MapExportPath("png");
        var svgPath = MapExportPath("svg");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "story.talewright.yaml"),
                "game:\n  id: map-large\n  title: Large Map\n  start: scene0\ncharacters:\n  player: {first-name: Tester}\n");
            await File.WriteAllTextAsync(Path.Combine(root, "main.tale"),
                string.Join("\n", Enumerable.Range(0, 120).Select(index =>
                    $"=== scene{index}\n* [Continue] -> {(index == 119 ? "END" : $"scene{index + 1}")}\n")));
            await using var driver = new NativePlayerDriver(artifacts, executable);
            await driver.StartAsync(root);
            await WaitFor(driver, "Scene setup");
            await driver.ActivateAsync("Map");
            await WaitFor(driver, "120 scenes, 120 diverts");
            await driver.ClickAsync("Export PNG");
            await WaitFor(driver, "Map export failed:", "Use SVG export");
            Assert.False(File.Exists(pngPath));
            await driver.ClickAsync("Export SVG");
            await WaitFor(driver, $"Saved map to {svgPath}");
            var labels = XDocument.Load(svgPath).Descendants(XName.Get("text", "http://www.w3.org/2000/svg"))
                .Select(node => node.Value).ToArray();
            Assert.Contains("scene0", labels);
            Assert.Contains("scene119", labels);
        }
        finally
        {
            File.Delete(pngPath);
            File.Delete(svgPath);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<AccessibleNode> WaitFor(NativePlayerDriver driver, params string[] texts)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var snapshot = await driver.SnapshotAsync();
            if (texts.All(text => snapshot.Content.Contains(text, StringComparison.Ordinal)))
            {
                return snapshot;
            }
            await Task.Delay(100, timeout.Token);
        }
    }
}
