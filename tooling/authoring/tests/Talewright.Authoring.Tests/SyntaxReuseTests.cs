using System.Runtime.CompilerServices;
using System.Text.Json;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;
using Talewright.Talemark.Parsing;
using Talewright.Testing;
using Talewright.Rules.Expressions;

namespace Talewright.Authoring.Tests;

public sealed class SyntaxReuseTests
{
    private static string Manifest(bool number = false)
    {
        var manifest = new ManifestFaker("syntax-reuse", "Syntax reuse", "a");
        manifest.Character("player", "Ada");
        if (number) manifest.Number("weather", 0);
        return manifest.Build();
    }

    private static StorySource Source() => new(new Dictionary<string, string>
    {
        [StorySource.ManifestFileName] = Manifest(),
        ["a.tale"] = "=== a\n~ var:copy = var:weather\n{var:weather}\n-> b",
        ["b.tale"] = "=== b\n~ var:weather = \"sunny\"\n-> END",
    });

    [Fact]
    public void A_warm_realistic_edit_stays_within_its_allocation_ceiling()
    {
        var source = RealisticStory.Create(new RealisticStoryProfile { Chapters = 20 }).ToSource();
        var files = source.Files.ToDictionary();
        var path = source.TaleFiles.First().Key;
        var workspace = new StoryWorkspace(source);
        workspace.AnalyzeSnapshot(source, CompilerOptions.Default, CancellationToken.None);
        var allocated = new List<long>();
        for (var edit = 0; edit < 6; edit++)
        {
            files[path] = source.Files[path] + $"\n// edit {edit}";
            workspace.UpdateFile(path, files[path]);
            var snapshot = new StorySource(files, source.ImagePaths);
            var before = GC.GetAllocatedBytesForCurrentThread();
            var analysis = workspace.AnalyzeSnapshot(snapshot, CompilerOptions.Default, CancellationToken.None);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(analysis);
            if (edit >= 3) allocated.Add(bytes);
        }
        allocated.Sort();
        var ceiling = PerformanceSource.Utf8ByteCount(source) * 54;
        Assert.True(allocated[1] <= ceiling,
            $"A cached edit allocated {allocated[1]:N0} bytes; the 54 B/source byte ceiling is {ceiling:N0}.");
        Assert.Equal(source.TaleFiles.Count() + 6, workspace.SyntaxParseCount);
    }

    [Fact]
    public async Task A_realistic_one_file_edit_parses_only_that_file_and_rebinds_like_fresh_analysis()
    {
        var source = RealisticStory.Create(new RealisticStoryProfile { Chapters = 20 }).ToSource();
        var workspace = new StoryWorkspace(source);
        await Analyze(workspace);
        var syntax = workspace.Syntax;
        var count = source.TaleFiles.Count();
        Assert.Equal(count, workspace.SyntaxParseCount);
        Assert.Equal(count, syntax.Count);

        await workspace.AnalyzeAsync(TimeSpan.Zero);
        Assert.Equal(count, workspace.SyntaxParseCount);
        var path = source.TaleFiles.First().Key;
        workspace.Open(path, 1, new string(source.Files[path].ToCharArray()));
        await workspace.AnalyzeAsync(TimeSpan.Zero);
        Assert.Equal(count, workspace.SyntaxParseCount);
        Assert.Same(syntax[path], workspace.Syntax[path]);

        workspace.Change(path, 2, source.Files[path] + "\n// edit");
        await Analyze(workspace);
        Assert.Equal(count + 1, workspace.SyntaxParseCount);
        foreach (var (file, original) in syntax)
            if (file == path) Assert.NotSame(original, workspace.Syntax[file]);
            else Assert.Same(original, workspace.Syntax[file]);
    }

    [Fact]
    public async Task Lifecycle_changes_reuse_only_exact_effective_text_and_refresh_cross_file_symbols()
    {
        var source = Source();
        var workspace = new StoryWorkspace(source);
        await Analyze(workspace);
        var a = workspace.Syntax["a.tale"];
        var b = workspace.Syntax["b.tale"];

        workspace.Open("b.tale", 1, source.Files["b.tale"]);
        await Analyze(workspace);
        Assert.Equal(2, workspace.SyntaxParseCount);
        Assert.Same(b, workspace.Syntax["b.tale"]);

        workspace.Change("b.tale", 2, "=== b\n~ var:weather = 1\n-> END");
        Assert.Single(workspace.Syntax);
        var number = await Analyze(workspace);
        Assert.Contains(number.Symbols, symbol => symbol.Name == "copy" && symbol.Type == "integer");
        Assert.Equal(3, workspace.SyntaxParseCount);
        Assert.Same(a, workspace.Syntax["a.tale"]);
        Assert.False(workspace.Change("b.tale", 1, "=== ignored"));
        await workspace.AnalyzeAsync(TimeSpan.Zero);
        Assert.Equal(3, workspace.SyntaxParseCount);

        workspace.UpdateFile("b.tale", "=== renamed\n-> END");
        await Analyze(workspace);
        Assert.Equal(3, workspace.SyntaxParseCount);
        workspace.Close("b.tale");
        var closed = await Analyze(workspace);
        Assert.Equal(4, workspace.SyntaxParseCount);
        Assert.Contains(closed.Diagnostics, diagnostic => diagnostic.Code == "TW2002");
        Assert.DoesNotContain(closed.Symbols, symbol => symbol.Name == "weather");
        Assert.Same(a, workspace.Syntax["a.tale"]);

        workspace.UpdateFile("b.tale", null);
        Assert.Single(workspace.Syntax);
        workspace.UpdateFile("renamed.tale", source.Files["b.tale"]);
        await Analyze(workspace);
        Assert.Equal(5, workspace.SyntaxParseCount);
        Assert.Equal(["a.tale", "renamed.tale"], workspace.Syntax.Keys.Order());
        workspace.Open("renamed.tale", 1, "=== a\n* [Broken");
        var duplicate = await Analyze(workspace);
        Assert.Contains(duplicate.Diagnostics, diagnostic => diagnostic.Code == "TW3010");
        Assert.Equal(6, workspace.SyntaxParseCount);
        workspace.Close("renamed.tale");
        await Analyze(workspace);
        Assert.Equal(7, workspace.SyntaxParseCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("characters: [")]
    public async Task Manifest_recovery_and_type_changes_reuse_syntax_but_not_symbols(string? manifest)
    {
        var files = Source().Files.ToDictionary();
        if (manifest is null) files.Remove(StorySource.ManifestFileName);
        else files[StorySource.ManifestFileName] = manifest;
        var workspace = new StoryWorkspace(new StorySource(files));
        await Analyze(workspace);
        var syntax = workspace.Syntax;
        Assert.Equal(2, workspace.SyntaxParseCount);

        workspace.UpdateFile(StorySource.ManifestFileName, Manifest());
        var text = await Analyze(workspace);
        Assert.Contains(text.Symbols, symbol => symbol.Name == "copy" && symbol.Type == "text");
        workspace.Open(StorySource.ManifestFileName, 1, Manifest(number: true));
        var number = await Analyze(workspace);
        Assert.Contains(number.Diagnostics, diagnostic => diagnostic.Code == "TW2003");
        Assert.Contains(number.Symbols, symbol => symbol.Name == "copy" && symbol.Type == "integer");
        workspace.Close(StorySource.ManifestFileName);
        await Analyze(workspace);
        Assert.Equal(2, workspace.SyntaxParseCount);
        Assert.All(syntax, entry => Assert.Same(entry.Value, workspace.Syntax[entry.Key]));
    }

    [Fact]
    public async Task Reload_reuses_matching_syntax_after_overlays_and_evicts_deleted_files()
    {
        var source = Source();
        var original = new StoryWorkspace(source);
        await Analyze(original);
        original.Open("b.tale", 1, "=== overlay\n-> END");
        await Analyze(original);
        var syntax = original.Syntax;

        var files = source.Files.ToDictionary();
        files["a.tale"] += "\n// disk reload";
        var replacement = new StoryWorkspace(new StorySource(files));
        replacement.Open("b.tale", 1, "=== overlay\n-> END");
        replacement.ReuseSyntaxFrom(original);
        Assert.Single(replacement.Syntax);
        await Analyze(replacement);
        Assert.Equal(1, replacement.SyntaxParseCount);
        Assert.Same(syntax["b.tale"], replacement.Syntax["b.tale"]);
        Assert.NotSame(syntax["a.tale"], replacement.Syntax["a.tale"]);

        files.Remove("b.tale");
        var removed = new StoryWorkspace(new StorySource(files));
        removed.ReuseSyntaxFrom(replacement);
        Assert.Single(removed.Syntax);
        await Analyze(removed);
        Assert.Equal(0, removed.SyntaxParseCount);
    }

    [Fact]
    public async Task Recreated_workspaces_rebind_reused_syntax_against_new_registrations()
    {
        var files = Source().Files.ToDictionary();
        files["a.tale"] = "=== a\n{if @Ready}Ready.{/if}\n-> b";
        var source = new StorySource(files);
        var original = new StoryWorkspace(source);
        var missing = await Analyze(original);
        Assert.Contains(missing.Diagnostics, diagnostic => diagnostic.Code == "TW2005");
        var options = new CompilerOptions
        {
            Predicates = new NamedPredicateRegistry().Register("Ready", _ => throw new InvalidOperationException("Must not evaluate"))
        };
        var replacement = new StoryWorkspace(source, options);
        replacement.ReuseSyntaxFrom(original);
        var registered = (await replacement.AnalyzeAsync(TimeSpan.Zero)).Analysis;
        Assert.DoesNotContain(registered.Diagnostics, diagnostic => diagnostic.Code == "TW2005");
        Assert.Equal(StoryAnalyzer.Analyze(source, options).Diagnostics, registered.Diagnostics);
        Assert.Equal(0, replacement.SyntaxParseCount);
        Assert.All(original.Syntax, entry => Assert.Same(entry.Value, replacement.Syntax[entry.Key]));
    }

    [Fact]
    public async Task A_cancelled_parse_cannot_insert_old_text_into_the_latest_cache()
    {
        const string staleText = "=== stale\n-> END";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        TaleFileSyntax Parse(string text, CancellationToken token)
        {
            var syntax = TaleSyntaxParser.Parse(text, token);
            if (text == staleText)
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Parse was not released.");
            }
            return syntax;
        }
        var workspace = new StoryWorkspace(Source(), null, false, null, null, parseSyntax: Parse);
        await Analyze(workspace);
        workspace.Open("b.tale", 1, staleText);
        var stale = workspace.AnalyzeAsync(TimeSpan.Zero);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            workspace.Change("b.tale", 2, Source().Files["b.tale"]);
            var latest = workspace.AnalyzeAsync(TimeSpan.Zero);
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stale);
            Assert.True((await latest.WaitAsync(TimeSpan.FromSeconds(10))).Published);
            Assert.Equal(Source().Files["b.tale"], workspace.Syntax["b.tale"].Text);
            Assert.DoesNotContain(workspace.Current!.Symbols, symbol => symbol.Name == "stale");
            Assert.Equal(4, workspace.SyntaxParseCount);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task Repeated_edits_keep_only_live_syntax_and_obsolete_snapshots_are_collectible()
    {
        var workspace = new StoryWorkspace(Source());
        await workspace.AnalyzeAsync(TimeSpan.Zero);
        var oldAnalysis = AnalysisReference(workspace);
        var obsolete = new List<WeakReference<TaleFileSyntax>>();
        workspace.Open("b.tale", 1, Source().Files["b.tale"]);
        for (var version = 2; version < 22; version++)
        {
            obsolete.Add(SyntaxReference(workspace, "b.tale"));
            workspace.Change("b.tale", version, $"=== b\n~ var:weather = {version}\n-> END");
            await workspace.AnalyzeAsync(TimeSpan.Zero);
            Assert.Equal(2, workspace.Syntax.Count);
        }
        workspace.UpdateFile("a.tale", null);
        Assert.Single(workspace.Syntax);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.All(obsolete, reference => Assert.False(reference.TryGetTarget(out _)));
        Assert.False(oldAnalysis.TryGetTarget(out _));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<TaleFileSyntax> SyntaxReference(StoryWorkspace workspace, string path) =>
        new(workspace.Syntax[path]);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<StoryAnalysis> AnalysisReference(StoryWorkspace workspace) => new(workspace.Current!);

    private static async Task<StoryAnalysis> Analyze(StoryWorkspace workspace)
    {
        var update = await workspace.AnalyzeAsync(TimeSpan.Zero);
        Assert.True(update.Published);
        var cached = update.Analysis;
        var fresh = StoryAnalyzer.Analyze(cached.Source);
        Assert.Equal(fresh.Diagnostics, cached.Diagnostics);
        Assert.Equal(fresh.Symbols, cached.Symbols);
        Assert.Equal(JsonSerializer.Serialize(fresh.Tokens), JsonSerializer.Serialize(cached.Tokens));
        Assert.Equal(fresh.Expressions, cached.Expressions);
        Assert.Equivalent(fresh.Scenes, cached.Scenes, strict: true);
        Assert.Equivalent(fresh.Substitutions, cached.Substitutions, strict: true);
        Assert.Equivalent(fresh.World, cached.World, strict: true);
        var freshSteps = fresh.Scenes.SelectMany(scene => SceneSteps.Walk(scene.Definition.Steps)).ToArray();
        var cachedSteps = cached.Scenes.SelectMany(scene => SceneSteps.Walk(scene.Definition.Steps)).ToArray();
        Assert.Equal(freshSteps.Length, cachedSteps.Length);
        for (var index = 0; index < freshSteps.Length; index++)
        {
            Assert.Equal(freshSteps[index].GetType(), cachedSteps[index].GetType());
            Assert.Equivalent(freshSteps[index], cachedSteps[index], strict: true);
        }
        return cached;
    }
}
