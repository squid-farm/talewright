using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Testing;

namespace Talewright.LanguageServer.Tests;

public sealed class ConcurrencyTests
{
    private static readonly string Original = Tale("Hello.");
    private static readonly string Changed = Tale("Hello again.");
    private static readonly string Reference = string.Join('\n', SceneFaker.Header("start"), DivertFaker.To("next"), "");
    private static readonly string Target = string.Join('\n', SceneFaker.Header("next"), DivertFaker.To("END"), "");

    private static string Tale(string prose) =>
        string.Join('\n', SceneFaker.Header("start"), prose, DivertFaker.To("END"), "");

    private static object Position(InProcessClient client, int line = 2, int character = 4) => new
    {
        textDocument = new { uri = client.UriOf("start.tale") },
        position = new { line, character }
    };

    private static object InlayHint(InProcessClient client) => new
    {
        textDocument = new { uri = client.UriOf("start.tale") },
        range = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 0 } }
    };

    private static async Task PublishInitial(AnalysisControl control, InProcessClient client, string text)
    {
        await client.Open("start.tale", text, 1);
        (await control.NextDelay()).Release();
        (await control.NextAnalysis()).Release();
        await client.Diagnostics("start.tale", 1);
    }

    [Fact]
    public async Task Quiet_window_coalesces_a_burst_without_starting_analysis_per_keystroke()
    {
        var control = new AnalysisControl();
        await using var client = new InProcessClient(control);
        await client.Initialize();
        await PublishInitial(control, client, Original);

        Gate? latest = null;
        for (var version = 2; version <= 25; version++)
        {
            await client.Change("start.tale", Tale($"Edit {version}."), version);
            latest = await control.NextDelay();
            Assert.Equal(TimeSpan.FromMilliseconds(150), latest.Duration);
        }
        await client.Barrier(2);
        Assert.Equal(1, control.AnalysisCount);
        latest!.Release();
        var analysis = await control.NextAnalysis();
        Assert.Equal(Tale("Edit 25."), analysis.Source.Files["start.tale"]);
        analysis.Release();
        await client.Diagnostics("start.tale", 25);
        await client.Barrier(3);
        Assert.Equal(2, control.AnalysisCount);
        for (var version = 2; version < 25; version++)
            Assert.False(client.HasDiagnostics("start.tale", version));
    }

    [Fact]
    public async Task Edits_during_running_analysis_cancel_it_and_publish_only_the_latest()
    {
        var control = new AnalysisControl();
        await using var client = new InProcessClient(control);
        await client.Initialize();
        await PublishInitial(control, client, Original);
        await client.Change("start.tale", Changed, 2);
        (await control.NextDelay()).Release();
        var running = await control.NextAnalysis();

        Gate? latest = null;
        for (var version = 3; version <= 25; version++)
        {
            await client.Change("start.tale", Tale($"Burst {version}."), version);
            latest = await control.NextDelay();
            Assert.Equal(TimeSpan.FromMilliseconds(150), latest.Duration);
        }
        Assert.True(running.CancellationRequested);
        Assert.Equal(2, control.AnalysisCount);
        latest!.Release();
        var settled = await control.NextAnalysis();
        Assert.Equal(Tale("Burst 25."), settled.Source.Files["start.tale"]);
        settled.Release();
        await client.Diagnostics("start.tale", 25);
        await client.Barrier(2);
        Assert.Equal(3, control.AnalysisCount);
        for (var version = 2; version < 25; version++)
            Assert.False(client.HasDiagnostics("start.tale", version));
    }

    [Fact]
    public async Task Paced_typing_burst_does_not_restart_analysis_for_each_edit()
    {
        var control = new AnalysisControl(realDelays: true);
        await using var client = new InProcessClient(control);
        await client.Initialize();
        await client.Open("start.tale", Original, 1);
        (await control.NextAnalysis()).Release();
        await client.Diagnostics("start.tale", 1);

        await client.Change("start.tale", Changed, 2);
        var running = await control.NextAnalysis();
        Assert.Equal(2, control.AnalysisCount);

        for (var version = 3; version <= 8; version++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(60));
            Assert.Equal(2, control.AnalysisCount);
            await client.Change("start.tale", Tale($"Paced {version}."), version);
            await client.Barrier(version);
            Assert.Equal(2, control.AnalysisCount);
        }

        Assert.True(running.CancellationRequested);
        var settled = await control.NextAnalysis();
        Assert.Equal(Tale("Paced 8."), settled.Source.Files["start.tale"]);
        settled.Release();
        await client.Diagnostics("start.tale", 8);
        Assert.Equal(3, control.AnalysisCount);
    }

    [Fact]
    public async Task Real_analyzer_reuses_syntax_across_watch_reload_and_disk_fallback_on_close()
    {
        var control = new AnalysisControl();
        await using var client = new InProcessClient(control, realAnalyzer: true);
        await client.Initialize();
        await client.Open("start.tale", Original, 1);
        (await control.NextDelay()).Release();
        var initial = await control.NextPublication();
        await client.Diagnostics("start.tale", 1);
        Assert.Equal((1, 1), (initial.ParsedFiles, initial.CachedFiles));

        client.Write("next.tale", Target);
        await client.Watch("next.tale", 1);
        (await control.NextDelay()).Release();
        var added = await control.NextPublication();
        await client.Diagnostics("start.tale", 1);
        Assert.Equal((1, 2), (added.ParsedFiles, added.CachedFiles));

        client.Write("start.tale", Changed);
        await client.Watch("start.tale", 2);
        (await control.NextDelay()).Release();
        var overlay = await control.NextPublication();
        await client.Diagnostics("start.tale", 1);
        Assert.Equal((0, 2), (overlay.ParsedFiles, overlay.CachedFiles));

        await client.Send("textDocument/didClose", new { textDocument = new { uri = client.UriOf("start.tale") } });
        (await control.NextDelay()).Release();
        var closed = await control.NextPublication();
        Assert.Equal((1, 2), (closed.ParsedFiles, closed.CachedFiles));

        client.Delete("next.tale");
        await client.Watch("next.tale", 3);
        (await control.NextDelay()).Release();
        var deleted = await control.NextPublication();
        Assert.Equal((0, 1), (deleted.ParsedFiles, deleted.CachedFiles));
    }

    [Fact]
    public async Task Current_text_completion_and_hover_do_not_wait_for_whole_story_analysis()
    {
        var control = new AnalysisControl();
        await using var client = new InProcessClient(control);
        client.Write("next.tale", Target);
        await client.Initialize();
        await PublishInitial(control, client, Reference);

        const string partial = "=== start\n// current text\n-> ne";
        await client.Change("start.tale", partial, 2);
        var debounce = await control.NextDelay();
        await client.Request(2, "textDocument/completion", Position(client, 2, 5));
        var completion = (await client.Response(2)).GetProperty("result").GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("label").GetString() == "next");
        var completionRange = completion.GetProperty("textEdit").GetProperty("range");
        Assert.Equal(2, completionRange.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(3, completionRange.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(5, completionRange.GetProperty("end").GetProperty("character").GetInt32());
        Assert.Equal(1, control.AnalysisCount);

        const string current = "=== start\n// current text\n-> next";
        await client.Change("start.tale", current, 3);
        var latestDebounce = await control.NextDelay();
        await client.Request(3, "textDocument/hover", Position(client, 2, 5));
        var hover = (await client.Response(3)).GetProperty("result");
        Assert.Contains("Scene: next", hover.GetProperty("contents").GetProperty("value").GetString());
        Assert.Equal(2, hover.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(1, control.AnalysisCount);

        debounce.Release();
        latestDebounce.Release();
        var requestAnalysis = await control.NextAnalysis();
        Assert.Equal(current, requestAnalysis.Source.Files["start.tale"]);
        requestAnalysis.Release();
        await client.Diagnostics("start.tale", 3);
        Assert.Equal(2, control.AnalysisCount);
    }

    [Fact]
    public async Task Current_text_hover_keeps_other_file_scene_facts()
    {
        var control = new AnalysisControl();
        await using var client = new InProcessClient(control);
        client.Write("next.tale", "=== next\n@title Quiet Forest\n@tags forest, calm\n-> END\n");
        await client.Initialize();
        await PublishInitial(control, client, Reference);

        const string current = "=== start\n// current text\n-> next";
        await client.Change("start.tale", current, 2);
        var debounce = await control.NextDelay();
        await client.Request(2, "textDocument/hover", Position(client, 2, 5));
        var value = (await client.Response(2)).GetProperty("result").GetProperty("contents").GetProperty("value").GetString();
        Assert.Contains("Quiet Forest", value);
        Assert.Contains("calm, forest", value);
        Assert.Equal(1, control.AnalysisCount);

        debounce.Release();
        (await control.NextAnalysis()).Release();
        await client.Diagnostics("start.tale", 2);
    }

    [Fact]
    public async Task Cold_completion_returns_local_syntax_without_waiting_for_initialization()
    {
        var control = new AnalysisControl();
        await using var client = new InProcessClient(control);
        await client.Initialize();
        await client.Open("start.tale", "=== start\n@req", 1);
        var debounce = await control.NextDelay();

        await client.Request(2, "textDocument/completion", Position(client, 1, 4));
        var items = (await client.Response(2)).GetProperty("result").GetProperty("items").EnumerateArray();
        Assert.Contains(items, item => item.GetProperty("label").GetString() == "requires");
        Assert.Equal(0, control.AnalysisCount);

        await client.Change("start.tale", "=== start\n@requires", 2);
        var latestDebounce = await control.NextDelay();
        await client.Request(3, "textDocument/hover", Position(client, 1, 5));
        var hover = (await client.Response(3)).GetProperty("result");
        Assert.Contains("Boolean condition", hover.GetProperty("contents").GetProperty("value").GetString());
        Assert.Equal(1, hover.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(0, control.AnalysisCount);

        debounce.Release();
        latestDebounce.Release();
        (await control.NextAnalysis()).Release();
        await client.Diagnostics("start.tale", 2);
        Assert.Equal(1, control.AnalysisCount);
    }

    [Fact]
    public async Task Typing_burst_uses_each_current_buffer_without_restarting_whole_story_analysis()
    {
        var control = new AnalysisControl();
        await using var client = new InProcessClient(control);
        client.Write("next.tale", Target);
        await client.Initialize();
        await PublishInitial(control, client, Reference);

        Gate? latestDebounce = null;
        for (var version = 2; version <= 25; version++)
        {
            var current = $"=== start\n// edit {version}\n-> ne";
            await client.Change("start.tale", current, version);
            latestDebounce = await control.NextDelay();
            await client.Request(version, "textDocument/completion", Position(client, 2, 5));
            var item = (await client.Response(version)).GetProperty("result").GetProperty("items").EnumerateArray()
                .Single(candidate => candidate.GetProperty("label").GetString() == "next");
            Assert.Equal(2, item.GetProperty("textEdit").GetProperty("range").GetProperty("start")
                .GetProperty("line").GetInt32());
            Assert.Equal(1, control.AnalysisCount);
        }

        latestDebounce!.Release();
        var analysis = await control.NextAnalysis();
        Assert.Equal("=== start\n// edit 25\n-> ne", analysis.Source.Files["start.tale"]);
        analysis.Release();
        await client.Diagnostics("start.tale", 25);
        Assert.Equal(2, control.AnalysisCount);
    }

    [Fact]
    public async Task Completion_uses_published_cross_file_symbols_until_new_analysis_finishes()
    {
        var control = new AnalysisControl();
        await using var client = new InProcessClient(control);
        client.Write("next.tale", Target);
        await client.Initialize();
        await PublishInitial(control, client, Reference);

        client.Write("next.tale", "=== second\n-> END\n");
        await client.Watch("next.tale", 2);
        _ = await control.NextDelay();
        const string current = "=== start\n// current\n-> se";
        await client.Change("start.tale", current, 2);
        var debounce = await control.NextDelay();
        await client.Request(2, "textDocument/completion", Position(client, 2, 5));
        var staleItems = (await client.Response(2)).GetProperty("result").GetProperty("items").EnumerateArray();
        Assert.DoesNotContain(staleItems, item => item.GetProperty("label").GetString() == "second");
        Assert.Equal(1, control.AnalysisCount);

        debounce.Release();
        var analysis = await control.NextAnalysis();
        Assert.Equal("=== second\n-> END\n", analysis.Source.Files["next.tale"]);
        Assert.Equal(current, analysis.Source.Files["start.tale"]);
        analysis.Release();
        await client.Diagnostics("start.tale", 2);
        await client.Request(3, "textDocument/completion", Position(client, 2, 5));
        var refreshedItems = (await client.Response(3)).GetProperty("result").GetProperty("items").EnumerateArray();
        Assert.Contains(refreshedItems, item => item.GetProperty("label").GetString() == "second");
        Assert.Equal(2, control.AnalysisCount);
    }

    [Fact]
    public async Task Current_yaml_completion_uses_current_declarations_and_replacement_ranges()
    {
        var control = new AnalysisControl();
        await using var client = new InProcessClient(control);
        var originalManifest = new ManifestFaker("current-yaml", "Current YAML", "start", 42);
        originalManifest.Character("player", "Ada");
        originalManifest.Item("rope", "Rope");
        var updatedManifest = new ManifestFaker("current-yaml", "Current YAML", "start", 42);
        updatedManifest.Character("player", "Ada");
        updatedManifest.Item("lantern", "Lantern");
        var original = originalManifest.Build(trailingNewline: true);
        var current = updatedManifest.Build(trailingNewline: true)
            .Replace("first-name: Ada", "first-name: Ada\n    inventory: [lan]", StringComparison.Ordinal);
        client.Write(StorySource.ManifestFileName, original);
        client.Write("start.tale", Tale("Hello."));
        await client.Initialize();
        await client.Open(StorySource.ManifestFileName, original, 1);
        (await control.NextDelay()).Release();
        (await control.NextAnalysis()).Release();
        await client.Diagnostics(StorySource.ManifestFileName, 1);

        await client.Change(StorySource.ManifestFileName, current, 2);
        var debounce = await control.NextDelay();
        var lines = current.Split('\n');
        var inventoryLine = Array.FindIndex(lines, line => line.Contains("inventory: [lan]", StringComparison.Ordinal));
        Assert.True(inventoryLine >= 0);
        await client.Request(2, "textDocument/completion", new
        {
            textDocument = new { uri = client.UriOf(StorySource.ManifestFileName) },
            position = new { line = inventoryLine, character = lines[inventoryLine].IndexOf("lan]", StringComparison.Ordinal) + 3 }
        });
        var items = (await client.Response(2)).GetProperty("result").GetProperty("items").EnumerateArray().ToArray();
        Assert.Contains(items, item => item.GetProperty("label").GetString() == "lantern");
        Assert.DoesNotContain(items, item => item.GetProperty("label").GetString() == "rope");
        var range = Assert.Single(items, item => item.GetProperty("label").GetString() == "lantern")
            .GetProperty("textEdit").GetProperty("range");
        Assert.Equal(inventoryLine, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(inventoryLine, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(1, control.AnalysisCount);

        debounce.Release();
        (await control.NextAnalysis()).Release();
        await client.Diagnostics(StorySource.ManifestFileName, 2);
    }

    [Fact]
    public async Task Document_symbols_respond_before_whole_story_analysis_starts()
    {
        var control = new AnalysisControl();
        await using var client = new InProcessClient(control);
        await client.Initialize();
        await client.Open("start.tale", Original, 1);
        var debounce = await control.NextDelay();

        await client.Request(2, "textDocument/documentSymbol", new
        {
            textDocument = new { uri = client.UriOf("start.tale") }
        });
        var response = await client.Response(2);
        Assert.Equal("start", response.GetProperty("result")[0].GetProperty("name").GetString());
        Assert.Equal(0, control.AnalysisCount);

        debounce.Release();
        (await control.NextAnalysis()).Release();
        await client.Diagnostics("start.tale", 1);
    }

    [Fact]
    public async Task Notifications_and_other_story_diagnostics_progress_while_a_request_waits()
    {
        var control = new AnalysisControl();
        await using var client = new InProcessClient(control);
        await client.Initialize();
        await client.Open("start.tale", Original, 1);
        var firstDelay = await control.NextDelay();
        await client.Request(2, "textDocument/inlayHint", InlayHint(client));
        var first = await control.NextAnalysis();

        var otherUri = client.CreateOtherStory(Tale("Other story."));
        await client.OpenUri(otherUri, Tale("Other story."), 1);
        (await control.NextDelay()).Release();
        var other = await control.NextAnalysis();
        Assert.Equal(Tale("Other story."), other.Source.Files["start.tale"]);
        other.Release();
        await client.DiagnosticsUri(otherUri, 1);
        await client.Barrier(3);
        Assert.False(client.HasResponse(2));

        firstDelay.Release();
        first.Release();
        Assert.Equal(JsonValueKind.Array, (await client.Response(2)).GetProperty("result").ValueKind);
        await client.Diagnostics("start.tale", 1);
    }

    [Fact]
    public async Task Cancelling_number_and_string_ids_does_not_cancel_shared_analysis()
    {
        var control = new AnalysisControl();
        await using var client = new InProcessClient(control);
        await client.Initialize();
        await PublishInitial(control, client, Original);
        await client.Change("start.tale", Changed, 2);
        var debounce = await control.NextDelay();
        await client.Request(2, "textDocument/inlayHint", InlayHint(client));
        var analysis = await control.NextAnalysis();
        await client.Request("2", "textDocument/inlayHint", InlayHint(client));
        await client.Request(3, "textDocument/inlayHint", InlayHint(client));
        await client.Barrier(4);
        Assert.False(client.HasResponse(3));

        await client.Send("$/cancelRequest", new { id = 2 });
        Assert.Equal(-32800, (await client.Response(2)).GetProperty("error").GetProperty("code").GetInt32());
        await client.Send("$/cancelRequest", new { id = "2" });
        Assert.Equal(-32800, (await client.Response("2")).GetProperty("error").GetProperty("code").GetInt32());
        await client.Barrier(5);
        Assert.False(client.HasResponse(3));
        Assert.Equal(2, control.AnalysisCount);

        debounce.Release();
        analysis.Release();
        Assert.Equal(JsonValueKind.Array, (await client.Response(3)).GetProperty("result").ValueKind);
        await client.Diagnostics("start.tale", 2);
        Assert.Equal(2, control.AnalysisCount);
    }

    [Fact]
    public async Task Superseded_request_reports_content_modified_and_older_versions_are_ignored()
    {
        var control = new AnalysisControl();
        await using var client = new InProcessClient(control);
        await client.Initialize();
        await PublishInitial(control, client, Original);
        await client.Change("start.tale", Changed, 2);
        var firstDelay = await control.NextDelay();
        await client.Request(2, "textDocument/inlayHint", InlayHint(client));
        var first = await control.NextAnalysis();

        await client.Change("start.tale", Tale("Ignored."), 1);
        await client.Barrier(3);
        Assert.False(client.HasResponse(2));
        await client.Change("start.tale", Tale("Newest."), 3);
        var newestDelay = await control.NextDelay();
        Assert.Equal(-32801, (await client.Response(2)).GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(2, control.AnalysisCount);

        firstDelay.Release();
        first.Release();
        newestDelay.Release();
        var newest = await control.NextAnalysis();
        Assert.Equal(Tale("Newest."), newest.Source.Files["start.tale"]);
        newest.Release();
        await client.Diagnostics("start.tale", 3);
        await client.Barrier(4);
        Assert.False(client.HasDiagnostics("start.tale", 2));
    }

    [Fact]
    public async Task Burst_of_changes_cancels_old_analysis_and_only_latest_revision_publishes()
    {
        var control = new AnalysisControl();
        await using var client = new InProcessClient(control);
        await client.Initialize();
        await PublishInitial(control, client, Original);

        await client.Change("start.tale", Changed, 2);
        var firstDelay = await control.NextDelay();
        await client.Request(2, "textDocument/inlayHint", InlayHint(client));
        var first = await control.NextAnalysis();
        Assert.False(first.CancellationRequested);

        var newestText = Tale("Newest.");
        await client.Change("start.tale", newestText, 3);
        var newestDelay = await control.NextDelay();
        Assert.Equal(-32801, (await client.Response(2)).GetProperty("error").GetProperty("code").GetInt32());
        Assert.True(first.CancellationRequested);
        Assert.False(client.HasDiagnostics("start.tale", 2));
        Assert.Equal(2, control.AnalysisCount);

        await client.Request(3, "textDocument/inlayHint", InlayHint(client));
        var newest = await control.NextAnalysis();
        Assert.Equal(newestText, newest.Source.Files["start.tale"]);
        Assert.False(newest.CancellationRequested);
        await client.Barrier(4);
        Assert.False(client.HasResponse(3));
        Assert.False(client.HasDiagnostics("start.tale", 3));

        firstDelay.Release();
        first.Release();
        newestDelay.Release();
        newest.Release();
        Assert.Equal(JsonValueKind.Array, (await client.Response(3)).GetProperty("result").ValueKind);
        await client.Diagnostics("start.tale", 3);
        Assert.False(client.HasDiagnostics("start.tale", 2));
        Assert.Equal(3, control.AnalysisCount);
    }

    [Fact]
    public async Task Closing_a_document_supersedes_a_waiting_request()
    {
        var control = new AnalysisControl();
        await using var client = new InProcessClient(control);
        await client.Initialize();
        await client.Open("start.tale", Original, 1);
        var debounce = await control.NextDelay();
        await client.Request(2, "textDocument/inlayHint", InlayHint(client));
        var analysis = await control.NextAnalysis();
        await client.Send("textDocument/didClose", new { textDocument = new { uri = client.UriOf("start.tale") } });
        await client.Barrier(3);
        Assert.Equal(-32801, (await client.Response(2)).GetProperty("error").GetProperty("code").GetInt32());
        debounce.Release();
        analysis.Release();
    }

    [Fact]
    public async Task Concurrent_requests_and_diagnostics_produce_complete_serialized_frames()
    {
        var control = new AnalysisControl();
        await using var client = new InProcessClient(control);
        await client.Initialize();
        await client.Open("start.tale", Original, 1);
        var debounce = await control.NextDelay();
        await client.Request(2, "textDocument/inlayHint", InlayHint(client));
        var analysis = await control.NextAnalysis();
        for (var id = 3; id < 34; id++)
            await client.Request(id, "textDocument/inlayHint", InlayHint(client));
        await client.Barrier(100);
        Assert.Equal(1, control.AnalysisCount);

        debounce.Release();
        analysis.Release();
        for (var id = 2; id < 34; id++)
            Assert.Equal(JsonValueKind.Array, (await client.Response(id)).GetProperty("result").ValueKind);
        await client.Diagnostics("start.tale", 1);
        Assert.Equal(1, control.AnalysisCount);
    }

    [Fact]
    public async Task Hover_and_definition_use_previous_analysis_for_an_unchanged_document()
    {
        var control = new AnalysisControl();
        await using var client = new InProcessClient(control);
        client.Write("next.tale", Target);
        await client.Initialize();
        await PublishInitial(control, client, Reference);

        client.Write("other.tale", string.Join('\n', SceneFaker.Header("other"), DivertFaker.To("END"), ""));
        await client.Watch("other.tale", 1);
        var debounce = await control.NextDelay();
        await client.Request(2, "textDocument/inlayHint", InlayHint(client));
        var analysis = await control.NextAnalysis();
        await client.Request(3, "textDocument/hover", Position(client, 1, 4));
        await client.Request(4, "textDocument/definition", Position(client, 1, 4));

        Assert.Equal(JsonValueKind.Object, (await client.Response(3)).GetProperty("result").ValueKind);
        var definition = (await client.Response(4)).GetProperty("result");
        Assert.Equal(client.UriOf("next.tale"), definition.GetProperty("uri").GetString());
        Assert.Equal(0, definition.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        await client.Barrier(5);
        Assert.False(client.HasResponse(2));
        Assert.Equal(2, control.AnalysisCount);

        debounce.Release();
        analysis.Release();
        await client.Response(2);
        await client.Diagnostics("start.tale", 1);
    }

    [Fact]
    public async Task Inlay_hints_wait_for_current_analysis_even_when_the_document_is_unchanged()
    {
        var control = new AnalysisControl();
        await using var client = new InProcessClient(control);
        await client.Initialize();
        await PublishInitial(control, client, Original);

        client.Write("other.tale", string.Join('\n', SceneFaker.Header("other"), DivertFaker.To("END"), ""));
        await client.Watch("other.tale", 1);
        var debounce = await control.NextDelay();
        await client.Request(2, "textDocument/inlayHint", new
        {
            textDocument = new { uri = client.UriOf("start.tale") },
            range = new { start = new { line = 0, character = 0 }, end = new { line = 2, character = 6 } }
        });
        var analysis = await control.NextAnalysis();
        await client.Barrier(3);
        Assert.False(client.HasResponse(2));

        debounce.Release();
        analysis.Release();
        Assert.Equal(JsonValueKind.Array, (await client.Response(2)).GetProperty("result").ValueKind);
        await client.Diagnostics("start.tale", 1);
    }

    [Fact]
    public async Task Hover_and_definition_wait_for_analysis_when_the_requested_document_changed()
    {
        var control = new AnalysisControl();
        await using var client = new InProcessClient(control);
        client.Write("next.tale", Target);
        await client.Initialize();
        await PublishInitial(control, client, Reference);

        client.Write("other.tale", string.Join('\n', SceneFaker.Header("other"), DivertFaker.To("END"), ""));
        await client.Watch("other.tale", 1);
        var firstDelay = await control.NextDelay();
        await client.Request(2, "textDocument/inlayHint", InlayHint(client));
        var first = await control.NextAnalysis();
        await client.Change("start.tale", Reference + "\n", 2);
        var nextDelay = await control.NextDelay();
        await client.Request(3, "textDocument/hover", Position(client, 1, 4));
        await client.Request(4, "textDocument/definition", Position(client, 1, 4));
        var hover = (await client.Response(3)).GetProperty("result");
        Assert.Equal(JsonValueKind.Object, hover.ValueKind);
        Assert.Equal(1, hover.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.False(client.HasResponse(4));
        Assert.Equal(-32801, (await client.Response(2)).GetProperty("error").GetProperty("code").GetInt32());

        firstDelay.Release();
        first.Release();
        var next = await control.NextAnalysis();
        Assert.Equal(Reference + "\n", next.Source.Files["start.tale"]);
        await client.Barrier(6);
        Assert.False(client.HasResponse(4));
        nextDelay.Release();
        next.Release();
        Assert.Equal(client.UriOf("next.tale"), (await client.Response(4)).GetProperty("result").GetProperty("uri").GetString());
        await client.Diagnostics("start.tale", 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Definition_waits_if_the_previous_target_changed_or_was_removed(bool removed)
    {
        var control = new AnalysisControl();
        await using var client = new InProcessClient(control);
        client.Write("next.tale", Target);
        await client.Initialize();
        await PublishInitial(control, client, Reference);

        if (removed) client.Delete("next.tale");
        else client.Write("next.tale", "\n" + Target);
        await client.Watch("next.tale", removed ? 3 : 2);
        var debounce = await control.NextDelay();
        await client.Request(2, "textDocument/definition", Position(client, 1, 4));
        var analysis = await control.NextAnalysis();
        await client.Barrier(3);
        Assert.False(client.HasResponse(2));
        debounce.Release();
        analysis.Release();
        var result = (await client.Response(2)).GetProperty("result");
        if (removed) Assert.Equal(JsonValueKind.Null, result.ValueKind);
        else
        {
            Assert.Equal(client.UriOf("next.tale"), result.GetProperty("uri").GetString());
            Assert.Equal(1, result.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        }
        await client.Diagnostics("start.tale", 1);
    }

    [Fact]
    public async Task Shutdown_cancels_waiting_requests_and_exit_awaits_running_analysis()
    {
        var control = new AnalysisControl();
        await using var client = new InProcessClient(control);
        await client.Initialize();
        await client.Open("start.tale", Original, 1);
        var debounce = await control.NextDelay();
        await client.Request(2, "textDocument/inlayHint", InlayHint(client));
        var analysis = await control.NextAnalysis();
        await client.Request(3, "shutdown", new { });
        Assert.Equal(-32800, (await client.Response(2)).GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Null, (await client.Response(3)).GetProperty("result").ValueKind);
        await client.Send("exit", new { });
        debounce.Release();
        analysis.Release();
        Assert.Equal(0, await client.WaitForExit());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Analyzer_failure_is_reported_to_the_request_and_editor(bool invalidOperation)
    {
        var control = new AnalysisControl();
        await using var client = new InProcessClient(control);
        await client.Initialize();
        await client.Open("start.tale", Original, 1);
        var debounce = await control.NextDelay();
        await client.Request(2, "textDocument/inlayHint", InlayHint(client));
        var analysis = await control.NextAnalysis();
        debounce.Release();
        analysis.Fail(invalidOperation
            ? new InvalidOperationException("Analyzer failed in the test.")
            : new FormatException("Analyzer failed in the test."));

        var response = await client.Response(2);
        Assert.Equal(-32603, response.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("Analyzer failed in the test.", response.GetProperty("error").GetProperty("message").GetString());
        var notification = await client.Notification("window/showMessage");
        Assert.Contains("Analyzer failed in the test.", notification.GetProperty("params").GetProperty("message").GetString());
        var diagnostics = (await client.Diagnostics(StorySource.ManifestFileName, null))
            .GetProperty("params").GetProperty("diagnostics").EnumerateArray().ToArray();
        Assert.Contains(diagnostics, diagnostic => diagnostic.GetProperty("code").GetString() == "TW5002");
    }

    private sealed class AnalysisControl
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource> _pending = [];
        private readonly Channel<Gate> _delays = Channel.CreateUnbounded<Gate>();
        private readonly Channel<AnalysisCall> _analyses = Channel.CreateUnbounded<AnalysisCall>();
        private readonly Channel<LanguageServer.DiagnosticPublication> _publications =
            Channel.CreateUnbounded<LanguageServer.DiagnosticPublication>();
        private readonly bool _realDelays;
        private bool _releaseAll;
        private int _analysisCount;

        public AnalysisControl(bool realDelays = false) => _realDelays = realDelays;

        public int AnalysisCount => Volatile.Read(ref _analysisCount);

        public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            if (delay == TimeSpan.Zero) return Task.CompletedTask;
            if (_realDelays) return Task.Delay(delay, cancellationToken);
            var gate = NewGate();
            _delays.Writer.TryWrite(new Gate(gate, delay));
            return gate.Task.WaitAsync(cancellationToken);
        }

        public async Task<StoryAnalysis> Analyze(StorySource source, CompilerOptions options, CancellationToken cancellationToken)
        {
            var gate = NewGate();
            Interlocked.Increment(ref _analysisCount);
            _analyses.Writer.TryWrite(new AnalysisCall(source, gate, cancellationToken));
            await gate.Task.WaitAsync(cancellationToken);
            return StoryAnalyzer.Analyze(source, options, cancellationToken);
        }

        public Task<Gate> NextDelay() => _delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public Task<AnalysisCall> NextAnalysis() => _analyses.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public Task<LanguageServer.DiagnosticPublication> NextPublication() =>
            _publications.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public void Measure(LanguageServer.DiagnosticPublication publication) => _publications.Writer.TryWrite(publication);

        public void ReleaseAll()
        {
            lock (_gate)
            {
                _releaseAll = true;
                foreach (var gate in _pending) gate.TrySetResult();
            }
        }

        private TaskCompletionSource NewGate()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (_releaseAll) gate.SetResult();
                else _pending.Add(gate);
            }
            return gate;
        }
    }

    private class Gate(TaskCompletionSource completion, TimeSpan? duration = null)
    {
        public TimeSpan? Duration { get; } = duration;
        public void Release() => completion.TrySetResult();
        public void Fail(Exception exception) => completion.TrySetException(exception);
    }

    private sealed class AnalysisCall(StorySource source, TaskCompletionSource completion, CancellationToken cancellationToken)
        : Gate(completion)
    {
        public StorySource Source { get; } = source;
        public bool CancellationRequested => cancellationToken.IsCancellationRequested;
    }

    private sealed class InProcessClient : IAsyncDisposable
    {
        private readonly AnonymousPipeServerStream _toServer = new(PipeDirection.Out, HandleInheritability.None);
        private readonly AnonymousPipeServerStream _fromServer = new(PipeDirection.In, HandleInheritability.None);
        private readonly AnonymousPipeClientStream _serverInput;
        private readonly AnonymousPipeClientStream _serverOutput;
        private readonly Protocol _protocol;
        private readonly StringWriter _errors = new();
        private readonly AnalysisControl _control;
        private readonly Task<int> _server;
        private readonly List<JsonElement> _backlog = [];
        private readonly List<string> _otherRoots = [];

        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"talewright-lsp-concurrency-{Guid.NewGuid():N}");

        public InProcessClient(AnalysisControl control, bool realAnalyzer = false)
        {
            _control = control;
            Directory.CreateDirectory(Root);
            var manifest = new ManifestFaker("lsp-concurrency", "LSP concurrency", "start");
            File.WriteAllText(PathOf(StorySource.ManifestFileName), manifest.Build(trailingNewline: true));
            File.WriteAllText(PathOf("start.tale"), Original);
            _serverInput = new AnonymousPipeClientStream(PipeDirection.In, _toServer.ClientSafePipeHandle);
            _serverOutput = new AnonymousPipeClientStream(PipeDirection.Out, _fromServer.ClientSafePipeHandle);
            _protocol = new Protocol(_fromServer, _toServer);
            _server = new LanguageServer(_serverInput, _serverOutput, _errors,
                realAnalyzer ? null : control.Analyze, control.Delay, realAnalyzer ? control.Measure : null).RunAsync();
        }

        private string PathOf(string name) => Path.Combine(Root, name);
        public string UriOf(string name) => new Uri(PathOf(name)).AbsoluteUri;
        public void Write(string name, string text) => File.WriteAllText(PathOf(name), text);
        public void Delete(string name) => File.Delete(PathOf(name));

        public string CreateOtherStory(string text)
        {
            var root = Path.Combine(Path.GetTempPath(), $"talewright-lsp-concurrency-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            _otherRoots.Add(root);
            var manifest = new ManifestFaker("other-lsp", "Other LSP", "start");
            File.WriteAllText(Path.Combine(root, StorySource.ManifestFileName), manifest.Build(trailingNewline: true));
            var path = Path.Combine(root, "start.tale");
            File.WriteAllText(path, text);
            return new Uri(path).AbsoluteUri;
        }

        public async Task Initialize()
        {
            await Request(1, "initialize", new { processId = Environment.ProcessId, rootUri = new Uri(Root).AbsoluteUri, capabilities = new { } });
            await Response(1);
        }

        public Task Open(string name, string text, int version) => Send("textDocument/didOpen",
            new { textDocument = new { uri = UriOf(name), languageId = "tale", version, text } });

        public Task OpenUri(string uri, string text, int version) => Send("textDocument/didOpen",
            new { textDocument = new { uri, languageId = "tale", version, text } });

        public Task Change(string name, string text, int version) => Send("textDocument/didChange",
            new { textDocument = new { uri = UriOf(name), version }, contentChanges = new[] { new { text } } });

        public Task Watch(string name, int type) => Send("workspace/didChangeWatchedFiles",
            new { changes = new[] { new { uri = UriOf(name), type } } });

        public Task Request(int id, string method, object parameters) =>
            _protocol.WriteAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, CancellationToken.None);

        public Task Request(string id, string method, object parameters) =>
            _protocol.WriteAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, CancellationToken.None);

        public Task Send(string method, object parameters) =>
            _protocol.WriteAsync(new { jsonrpc = "2.0", method, @params = parameters }, CancellationToken.None);

        public Task<JsonElement> Response(int id) => Next(message =>
            message.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.Number && value.GetInt32() == id);

        public Task<JsonElement> Response(string id) => Next(message =>
            message.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String && value.GetString() == id);

        public async Task Barrier(int id)
        {
            await Request(id, "unknown", new { });
            Assert.Equal(-32601, (await Response(id)).GetProperty("error").GetProperty("code").GetInt32());
        }

        public bool HasResponse(int id) => _backlog.Any(message =>
            message.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.Number && value.GetInt32() == id);

        public bool HasDiagnostics(string name, int version) => _backlog.Any(message =>
            message.TryGetProperty("method", out var method) && method.GetString() == "textDocument/publishDiagnostics" &&
            message.GetProperty("params").GetProperty("uri").GetString() == UriOf(name) &&
            message.GetProperty("params").GetProperty("version").ToString() == version.ToString());

        public Task<JsonElement> Diagnostics(string name, int? version) => DiagnosticsUri(UriOf(name), version);

        public Task<JsonElement> DiagnosticsUri(string uri, int? version) => Next(message =>
            message.TryGetProperty("method", out var method) && method.GetString() == "textDocument/publishDiagnostics" &&
            message.GetProperty("params").GetProperty("uri").GetString() == uri &&
            message.GetProperty("params").GetProperty("version").ToString() == (version?.ToString() ?? ""));

        public Task<JsonElement> Notification(string name) => Next(message =>
            message.TryGetProperty("method", out var method) && method.GetString() == name);

        public Task<int> WaitForExit() => _server.WaitAsync(TimeSpan.FromSeconds(10));

        private async Task<JsonElement> Next(Func<JsonElement, bool> predicate)
        {
            var index = _backlog.FindIndex(message => predicate(message));
            if (index >= 0)
            {
                var result = _backlog[index];
                _backlog.RemoveAt(index);
                return result;
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (await _protocol.ReadAsync(timeout.Token) is { } message)
            {
                if (predicate(message)) return message;
                _backlog.Add(message);
            }
            throw new EndOfStreamException("Language server stopped before sending the expected message.");
        }

        public async ValueTask DisposeAsync()
        {
            _control.ReleaseAll();
            try
            {
                if (!_server.IsCompleted)
                {
                    await Request(999, "shutdown", new { });
                    await Response(999);
                    await Send("exit", new { });
                }
                Assert.Equal(0, await _server.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.DoesNotContain("Unhandled exception", _errors.ToString());
            }
            finally
            {
                _serverInput.Dispose();
                _serverOutput.Dispose();
                _toServer.Dispose();
                _fromServer.Dispose();
                _errors.Dispose();
                Directory.Delete(Root, recursive: true);
                foreach (var root in _otherRoots) Directory.Delete(root, recursive: true);
            }
        }
    }
}
