using Talewright.Debugger;
using Talewright.Narrative.Rendering;
using Talewright.Tale.Compilation;

namespace Talewright.Cli.Tests;

public sealed class AuthorReloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "talewright-reload-" + Guid.NewGuid().ToString("N"));
    private const string Yaml = """
        id: watched
        title: Watched
        start: start
        flags: [ready]
        vars: {count: 0}
        characters:
          player: {first-name: Tester}
        """;
    private const string Tale = """
        === start
        * [Go] -> current
        === current
        Old passage.
        ~ var:count += 1
        * [Finish] -> END
        """;

    public AuthorReloadTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "story.yaml"), Yaml);
        Write(Tale);
    }

    private void Write(string text) => File.WriteAllText(Path.Combine(_root, "main.tale"), text);
    private SourceCompilation Load() => SourceCompilation.Load(_root);
    private static SourceCompilation Source(string tale, string yaml = Yaml)
    {
        var source = new StorySource(new Dictionary<string, string> { ["story.yaml"] = yaml, ["main.tale"] = tale });
        return new(source, StoryCompiler.Compile(source), null);
    }

    [Fact]
    public void Tale_adoption_preserves_current_entry_and_invalid_source_is_repairable()
    {
        var model = new WorkbenchModel(_root);
        model.Reload();
        model.Start();
        model.Choose("c0");
        Write(Tale.Replace("Old passage.", "New passage."));
        Assert.True(model.ApplySource(Load()));
        Assert.Contains("New passage.", model.Session!.Current.Document.ToPlainText());
        Assert.Equal(1, model.Session.State.GetVariable(new("count")));
        Assert.Contains("Reload boundary: current", model.ReloadStatus);
        var good = model.Session;
        Write("=== start\n-> missing");
        Assert.False(model.ApplySource(Load()));
        Assert.Same(good, model.Session);
        Assert.True(model.SourceRejected);
        Write(Tale.Replace("Old passage.", "Repaired passage."));
        Assert.True(model.ApplySource(Load()));
        Assert.False(model.SourceRejected);
    }

    [Fact]
    public void Yaml_change_is_latched_across_invalid_and_subsequent_tale_saves_until_explicit_restart()
    {
        var model = new WorkbenchModel(_root);
        model.Reload();
        model.Start();
        var original = model.Session;
        var flag = Assert.Single(model.Flags);
        flag.Enabled = true;
        flag.Value = true;
        File.WriteAllText(Path.Combine(_root, "story.yaml"), Yaml + "\n# Edited YAML");
        Write("=== start\n-> missing");
        Assert.False(model.ApplySource(Load()));
        Assert.True(model.RestartRequired);
        Write(Tale.Replace("Old passage.", "After YAML."));
        Assert.False(model.ApplySource(Load()));
        Assert.Same(original, model.Session);
        File.WriteAllText(Path.Combine(_root, "story.yaml"), Yaml);
        Assert.False(model.ApplySource(Load()));
        model.Reload();
        Assert.Null(model.Error);
        Assert.False(model.RestartRequired);
        Assert.Null(model.Session);
        Assert.True(Assert.Single(model.Flags).Enabled);
        Assert.True(Assert.Single(model.Flags).Value);
    }

    [Fact]
    public void Removed_setup_ids_are_named_not_silently_dropped_and_restart_can_be_corrected()
    {
        var model = new WorkbenchModel(_root);
        model.Reload();
        model.Start();
        var original = model.Session;
        Assert.Single(model.Flags).Enabled = true;
        var field = model.Numbers.Single(field => field.Label == "var:count");
        field.Enabled = true;
        field.Value = "7";
        File.WriteAllText(Path.Combine(_root, "story.yaml"), Yaml.Replace("flags: [ready]", "flags: []")
            .Replace("vars: {count: 0}", "vars: {}"));
        Write(Tale.Replace("~ var:count += 1", ""));
        model.Reload();
        Assert.Same(original, model.Session);
        Assert.Contains("var:count", model.Error);
        Assert.Contains("flag:ready", model.Error);
        field.Enabled = false;
        Assert.Single(model.Flags).Enabled = false;
        model.Reload();
        Assert.Null(model.Error);
        Assert.Null(model.Session);
        Assert.Empty(model.Flags);
    }

    [Fact]
    public void New_tale_declarations_require_restart_and_candidate_runtime_errors_preserve_the_run()
    {
        var model = new WorkbenchModel(_root);
        model.Reload();
        model.Start();
        model.Choose("c0");
        var original = model.Session;
        Write(Tale.Replace("Old passage.", "~ set flag:newflag"));
        Assert.False(model.ApplySource(Load()));
        Assert.True(model.RestartRequired);
        Assert.Same(original, model.Session);
        Write(Tale);
        var triggers = string.Join("\n", Enumerable.Range(0, 40).Reverse().Select(i => $"""
              - id: chain-{i}
                when: flag:chain-{i}
                do: set flag:chain-{i + 1}
                once: true
            """));
        File.WriteAllText(Path.Combine(_root, "story.yaml"), Yaml.Replace("flags: [ready]", "flags: [ready, chain-0]")
            + "\ntriggers:\n" + triggers);
        model.Reload();
        model.Start();
        model.Choose("c0");
        original = model.Session;
        Write(Tale.Replace("~ var:count += 1", "~ set flag:chain-0"));
        Assert.False(model.ApplySource(Load()));
        Assert.Same(original, model.Session);
        Assert.Contains("cascade", model.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(model.RestartRequired);
        Write(Tale);
        Assert.True(model.ApplySource(Load()));
        Assert.False(model.SourceRejected);
    }

    [Fact]
    public void Superseded_commit_cannot_replace_story_session_or_diagnostics()
    {
        var model = new WorkbenchModel(_root);
        model.Reload();
        model.Start();
        var original = model.Session;
        Assert.False(model.ApplySource(Source(Tale.Replace("Old passage.", "Stale.")), commit: _ => false));
        Assert.Same(original, model.Session);
        Assert.Null(model.Error);
        Assert.False(model.ApplySource(Source(Tale, Yaml + "\n# stale YAML"), commit: _ => false));
        Assert.False(model.RestartRequired);
    }

    [Fact]
    public void Creation_is_retained_for_prose_reload_and_incompatible_options_are_named_on_restart()
    {
        var yaml = Yaml + "\ncreation:\n  name: true\n  stats:\n    str: [12, 16]\n";
        File.WriteAllText(Path.Combine(_root, "story.yaml"), yaml);
        var model = new WorkbenchModel(_root);
        model.Reload();
        var selection = model.Story!.Creation!.Default(model.Story.World.Characters[new("player")]) with
        {
            FirstName = "Alex", Stats = new Dictionary<string, int> { ["str"] = 16 },
        };
        model.SelectCharacter(selection);
        model.Start();
        var revision = model.CreationRevision;
        Assert.True(model.ApplySource(Source(Tale.Replace("Old passage.", "Revised."), yaml)));
        Assert.Equal(revision, model.CreationRevision);
        Assert.Equal("Alex", model.Character!.FirstName);
        File.WriteAllText(Path.Combine(_root, "story.yaml"), yaml.Replace("[12, 16]", "[12]"));
        model.Reload();
        Assert.Contains("creation stat:str=16", model.Error);
        Assert.NotNull(model.Session);
        model.ClearCharacter();
        model.Reload();
        Assert.Null(model.Error);
        Assert.Null(model.Character);
        Assert.Null(model.Session);
        model.Start();
        Assert.Contains("Create a character", model.Error);
    }

    [Fact]
    public async Task Watcher_repairs_initial_errors_and_handles_atomic_rename_delete_and_rapid_saves()
    {
        Write("=== start\n-> missing");
        var model = new WorkbenchModel(_root);
        using var ui = new SemaphoreSlim(1);
        async Task Dispatch(Action action)
        {
            await ui.WaitAsync();
            try { action(); } finally { ui.Release(); }
        }
        await using var watcher = new StoryReloadWatcher(model, Dispatch, () => { });
        await watcher.RescanAsync(restart: true);
        Assert.Null(model.Story);
        var temp = Path.Combine(_root, "editor.tmp");
        File.WriteAllText(temp, Tale);
        File.Move(temp, Path.Combine(_root, "main.tale"), overwrite: true);
        await Until(Dispatch, () => model.Story is not null);
        await Dispatch(() => { model.Start(); model.Choose("c0"); });
        for (var i = 0; i < 8; i++)
        {
            Write(Tale.Replace("Old passage.", $"Save {i}."));
        }
        await Until(Dispatch, () => model.Session!.Current.Document.ToPlainText().Contains("Save 7."));
        await Dispatch(() => Assert.Equal(1, model.Session!.State.GetVariable(new("count"))));
        File.Move(Path.Combine(_root, "main.tale"), Path.Combine(_root, "renamed.tale"));
        await Task.Delay(400);
        File.Delete(Path.Combine(_root, "renamed.tale"));
        await Until(Dispatch, () => model.SourceRejected);
        File.WriteAllText(Path.Combine(_root, "renamed.tale"), Tale.Replace("Old passage.", "Recovered."));
        await Until(Dispatch, () => !model.SourceRejected && model.Session!.Current.Document.ToPlainText().Contains("Recovered."));
    }

    [Fact]
    public async Task Late_compile_cannot_overwrite_newer_generation_and_play_during_compile_is_not_rolled_back()
    {
        var model = new WorkbenchModel(_root);
        model.Reload();
        model.Start();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var slow = Source(Tale.Replace("Old passage.", "Slow stale."));
        var fast = Source(Tale.Replace("Old passage.", "Latest."));
        SourceCompilation Compile(string _)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                return slow;
            }
            return fast;
        }
        using var ui = new SemaphoreSlim(1);
        async Task Dispatch(Action action)
        {
            await ui.WaitAsync();
            try { action(); } finally { ui.Release(); }
        }
        await using var watcher = new StoryReloadWatcher(model, Dispatch, () => { }, Compile);
        var first = watcher.RescanAsync();
        Task second = Task.CompletedTask;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            await Dispatch(() => model.Choose("c0"));
            second = watcher.RescanAsync();
        }
        finally
        {
            release.Set();
        }
        await Task.WhenAll(first, second);
        Assert.Contains("Latest.", model.Session!.Current.Document.ToPlainText());
        Assert.Equal("current", model.Session.Current.Scene!.Value.Value);
        Assert.Equal(1, model.Session.State.GetVariable(new("count")));
    }

    [Fact]
    public async Task Watcher_failure_is_visible_and_retry_rearms_it()
    {
        var model = new WorkbenchModel(_root);
        await using var watcher = new StoryReloadWatcher(model, action => { action(); return Task.CompletedTask; }, () => { });
        await watcher.RescanAsync(restart: true);
        Directory.Delete(_root, recursive: true);
        await watcher.RescanAsync();
        Assert.Contains("watcher failed", model.Error);
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "story.yaml"), Yaml);
        Write(Tale);
        await watcher.RescanAsync();
        Assert.True(model.RestartRequired);
        await watcher.RescanAsync(restart: true);
        Assert.Null(model.Error);
    }

    [Fact]
    public async Task Closing_cancels_pending_generations_and_prevents_adoption()
    {
        var model = new WorkbenchModel(_root);
        model.Reload();
        var original = model.Story;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var watcher = new StoryReloadWatcher(model, action => { action(); return Task.CompletedTask; }, () => { }, _ =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return Source(Tale.Replace("Old passage.", "After close."));
        });
        var pending = watcher.RescanAsync();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var closing = watcher.DisposeAsync().AsTask();
        release.Set();
        await closing;
        await pending;
        Assert.Same(original, model.Story);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => watcher.RescanAsync());
    }

    private static async Task Until(Func<Action, Task> dispatch, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var done = false;
            await dispatch(() => done = condition());
            if (done) { return; }
            await Task.Delay(25);
        }
        Assert.Fail("Watcher did not reach the expected state.");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
