using System.Collections;
using Talewright.Core.Identity;
using Talewright.Core.Rulesets;
using Talewright.Core.World;
using Talewright.Rules.Expressions;
using Talewright.Rules.Storylets;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Model;
using Talewright.Talemark.Parsing;

namespace Talewright.Talemark.Tests;

public sealed class AnalysisCancellationTests
{
    [Fact]
    public void Analyzer_throws_before_parsing_when_token_is_already_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = "invalid yaml: [",
            ["main.tale"] = "=== first\n-> END",
        });

        Assert.Throws<OperationCanceledException>(() =>
            StoryAnalyzer.Analyze(source, cancellationToken: cancellation.Token));
    }

    [Fact]
    public void Discovery_stops_at_the_next_scene_after_cancellation_from_the_first_scene_callback()
    {
        using var cancellation = new CancellationTokenSource();
        var scenes = new List<string>();
        var text = "=== first\n-> END\n=== second\n-> END\n";

        Assert.Throws<OperationCanceledException>(() => TaleParser.Discover(
            "main.tale",
            text,
            Symbols(),
            (_, _) => { },
            (id, _) =>
            {
                scenes.Add(id.ToString());
                if (scenes.Count == 1) cancellation.Cancel();
            },
            cancellation.Token));

        Assert.Equal(["first"], scenes);
    }

    [Fact]
    public void Buffered_paragraph_stops_after_cancellation_during_inline_processing()
    {
        using var cancellation = new CancellationTokenSource();
        var lookups = 0;
        var variables = new ObservedSet<VarId>([new("n")], onContains: () =>
        {
            if (++lookups == 1) cancellation.Cancel();
        });
        var text = "=== first\n" + string.Join('\n', Enumerable.Repeat("Value {var:n}.", 2048)) + "\n-> END\n";

        Assert.Throws<OperationCanceledException>(() =>
            TaleParser.Parse("main.tale", text, Symbols(variables), new DiagnosticBag(), cancellation.Token));
        Assert.InRange(lookups, 1, 64);
    }

    [Fact]
    public void Buffered_image_alt_template_stops_during_nested_inline_processing()
    {
        using var cancellation = new CancellationTokenSource();
        var lookups = 0;
        var variables = new ObservedSet<VarId>([new("n")], onContains: () =>
        {
            if (++lookups == 1) cancellation.Cancel();
        });
        var alt = string.Join(' ', Enumerable.Repeat("{var:n}", 1024));
        var text = $"=== first\n{{image(\"images/test.png\", \"{alt}\")}}\n-> END\n";
        var imageSource = new StorySource(new Dictionary<string, string>(), ["images/test.png"]);

        Assert.Throws<OperationCanceledException>(() =>
            TaleParser.Parse("main.tale", text, Symbols(variables, imageSource), new DiagnosticBag(), cancellation.Token));
        Assert.InRange(lookups, 1, 64);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Markdown_stops_during_block_and_inline_line_processing(bool inline)
    {
        using var cancellation = new CancellationTokenSource();
        var visits = 0;
        void CancelOnFirstLine()
        {
            if (++visits == 1) cancellation.Cancel();
        }

        var context = new InlineParser.MarkdownCancellationContext(cancellation.Token,
            onBlockLine: inline ? null : CancelOnFirstLine,
            onInlineLine: inline ? CancelOnFirstLine : null);
        var text = string.Join('\n', Enumerable.Repeat("ordinary *prose*.", 512));

        Assert.Throws<OperationCanceledException>(() => InlineParser.ParseMarkdown(text, context));
        Assert.InRange(visits, 1, 64);
    }

    [Theory]
    [InlineData("First line\nsecond *emphasis*.")]
    [InlineData("**Strong\nacross lines** and `code\nspan`.")]
    [InlineData("Hard break  \nnext line and [link](https://example.test).")]
    public void Cancellable_markdown_pipeline_preserves_uncancelled_output(string text)
    {
        using var cancellation = new CancellationTokenSource();
        var normal = InlineParser.ParseMarkdown(text, null);
        var cancellable = InlineParser.ParseMarkdown(text, new(cancellation.Token));

        Assert.Equal(Markdig.Markdown.ToHtml(normal), Markdig.Markdown.ToHtml(cancellable));
    }

    [Fact]
    public void Buffered_source_locations_preserve_line_and_column_mapping()
    {
        var source = new SourceText();
        source.AppendLine("first", new SourceLocation("main.tale", 3, 2));
        source.AppendLine("second", new SourceLocation("main.tale", 4, 3));
        source.AppendLine("", new SourceLocation("main.tale", 5, 1));
        source.AppendLine("", new SourceLocation("main.tale", 6, 1));

        Assert.Equal(new SourceLocation("main.tale", 3, 2), source.Locate(-1));
        Assert.Equal(new SourceLocation("main.tale", 3, 7), source.Locate(5));
        Assert.Equal(new SourceLocation("main.tale", 4, 3), source.Locate(6));
        Assert.Equal(new SourceLocation("main.tale", 4, 9), source.Locate(12));
        Assert.Equal(new SourceLocation("main.tale", 5, 1), source.Locate(13));
        Assert.Equal(new SourceLocation("main.tale", 6, 1), source.Locate(14));

        var repeatedStarts = new SourceText();
        repeatedStarts.AppendLine("", new SourceLocation("main.tale", 1, 1));
        repeatedStarts.AppendLine("", new SourceLocation("main.tale", 2, 1));
        repeatedStarts.AppendLine("x", new SourceLocation("main.tale", 3, 1));
        Assert.Equal(new SourceLocation("main.tale", 3, 1), repeatedStarts.Locate(0));
    }

    [Fact]
    public void Validation_stops_during_divert_indexing_without_visiting_later_scenes()
    {
        using var cancellation = new CancellationTokenSource();
        var tagVisits = 0;
        var stepsAfterCancellation = 0;
        var tags = new ObservedSet<string>(["shared"], onEnumerate: () =>
        {
            if (++tagVisits == 129) cancellation.Cancel();
        });
        var steps = new ObservedList<SceneStep>([Effect()], (_, _) =>
        {
            if (cancellation.IsCancellationRequested) stepsAfterCancellation++;
        });
        var hub = new StoryletTags(["shared"]);
        var scenes = Enumerable.Range(0, 128).Select(i => new ParsedScene(new SceneDefinition
        {
            Id = new SceneId($"scene{i}"),
            Tags = tags,
            Hub = hub,
            Steps = steps,
        }, SourceLocation.None) { EndLine = 1 }).ToArray();
        var manifest = new StoryManifest { Start = "scene0", World = Symbols().World };

        Assert.Throws<OperationCanceledException>(() =>
        {
            StoryValidator.Validate(manifest, scenes, new DiagnosticBag(), cancellation.Token);
        });
        Assert.Equal(129, tagVisits);
        Assert.Equal(0, stepsAfterCancellation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Validation_stops_during_single_scene_flow_walks(bool nested)
    {
        using var cancellation = new CancellationTokenSource();
        var visits = 0;
        var steps = new ObservedList<SceneStep>(Enumerable.Repeat(Effect(), 4096).ToArray(), (pass, _) =>
        {
            if (pass == 2 && ++visits == 1) cancellation.Cancel();
        });
        var scene = new SceneDefinition
        {
            Id = new SceneId("first"),
            Steps = nested
                ? [new ConditionalStep("branch", Range(), new BoolLiteral(true), steps, [])]
                : steps,
        };
        var manifest = new StoryManifest { Start = "first", World = Symbols().World };

        Assert.Throws<OperationCanceledException>(() =>
        {
            StoryValidator.Validate(manifest, [new(scene, SourceLocation.None) { EndLine = 1 }],
                new DiagnosticBag(), cancellation.Token);
        });
        Assert.InRange(visits, 1, 64);
    }

    [Fact]
    public void Reachability_graph_construction_stops_at_the_next_scene()
    {
        using var cancellation = new CancellationTokenSource();
        var scenes = Enumerable.Range(0, 256).Select(i => new SceneDefinition
        {
            Id = new SceneId($"scene{i}"),
            Steps = [Effect()],
        }).ToArray();
        var diverts = StoryDiverts.From(scenes);
        var visits = 0;
        var observed = new ObservedList<SceneDefinition>(scenes, (_, _) =>
        {
            if (++visits == 1) cancellation.Cancel();
        });

        Assert.Throws<OperationCanceledException>(() =>
            StoryReachability.Find(observed, diverts, scenes[0].Id, new CancellationCheckpoint(cancellation.Token)));
        Assert.Equal(1, visits);
    }

    [Fact]
    public void Divert_indexing_stops_while_collecting_hub_offers()
    {
        using var cancellation = new CancellationTokenSource();
        var visits = 0;
        var steps = new ObservedList<SceneStep>([Effect()], (_, _) =>
        {
            if (++visits == 1) cancellation.Cancel();
        });
        var hub = new StoryletTags(["shared"]);
        var scenes = Enumerable.Range(0, 128).Select(i => new SceneDefinition
        {
            Id = new SceneId($"scene{i}"),
            Tags = new HashSet<string> { "shared" },
            Hub = hub,
            Steps = steps,
        }).ToArray();

        Assert.Throws<OperationCanceledException>(() =>
            StoryDiverts.From(scenes, new CancellationCheckpoint(cancellation.Token)));
        Assert.Equal(1, visits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cancellable_divert_and_graph_walks_preserve_uncancelled_results(bool matchAll)
    {
        var hubId = new SceneId("hub");
        var target = new DivertTarget(hubId);
        var scenes = new[]
        {
            new SceneDefinition
            {
                Id = hubId, Tags = new HashSet<string> { "a" },
                Hub = new StoryletTags(["a", "b", "c"], matchAll), Steps = [Effect()],
            },
            new SceneDefinition
            {
                Id = new SceneId("both"), Tags = new HashSet<string> { "a", "b", "c" },
                Steps = [new DivertStep("out", Range(), target)],
            },
            new SceneDefinition
            {
                Id = new SceneId("b"), Tags = new HashSet<string> { "b", "c" },
                Steps = [new ConditionalStep("branch", Range(), new BoolLiteral(true),
                    [new DivertStep("out", Range(), target)], [Effect()])],
            },
            new SceneDefinition
            {
                Id = new SceneId("a"), Tags = new HashSet<string> { "a" },
                Steps = [new DivertStep("out", Range(), target)],
            },
        };
        using var cancellation = new CancellationTokenSource();
        var normal = StoryDiverts.From(scenes);
        var cancellable = StoryDiverts.From(scenes, new CancellationCheckpoint(cancellation.Token));
        var expectedOffers = matchAll ? new[] { "both" } : ["both", "b", "a"];

        Assert.Equal(expectedOffers, cancellable.HubOffers.Select(offer => offer.Scene.Value));
        Assert.Equal(normal.HubOffers.Select(offer => offer.Id), cancellable.HubOffers.Select(offer => offer.Id));
        Assert.Equal(normal.Diverts.Select(divert => (divert.Id, divert.From, divert.Target, divert.Kind, divert.Order)),
            cancellable.Diverts.Select(divert => (divert.Id, divert.From, divert.Target, divert.Kind, divert.Order)));

        var normalGraph = StoryReachability.Find(scenes, normal, hubId);
        var cancellableGraph = StoryReachability.Find(scenes, cancellable, hubId,
            new CancellationCheckpoint(cancellation.Token));
        Assert.Equal(normalGraph.Graph.EdgeCount, cancellableGraph.Graph.EdgeCount);
        foreach (var scene in scenes)
            Assert.Equal(normalGraph.IsReached(scene.Id), cancellableGraph.IsReached(scene.Id));
    }

    private static EffectStep Effect() => new("effect", Range(), []);
    private static SourceRange Range() => new(SourceLocation.None, SourceLocation.None);

    private static StorySymbols Symbols(IReadOnlySet<VarId>? variables = null, StorySource? imageSource = null)
    {
        var options = CompilerOptions.Default;
        var world = new WorldDefinition(new RulesetBuilder("analysis-cancellation").Build(),
            [], [], [], [], new Dictionary<VarId, int>());
        return new StorySymbols
        {
            World = world,
            Flags = new HashSet<FlagId>(),
            Variables = variables ?? new HashSet<VarId>(),
            Scenes = new HashSet<SceneId>(),
            Predicates = options.Predicates,
            Modifiers = options.Modifiers,
            ImageSource = imageSource,
        };
    }

    private sealed class ObservedList<T>(IReadOnlyList<T> items, Action<int, int> onVisit) : IReadOnlyList<T>
    {
        private int _pass;

        public int Count => items.Count;
        public T this[int index] => items[index];

        public IEnumerator<T> GetEnumerator()
        {
            var pass = ++_pass;
            for (var index = 0; index < items.Count; index++)
            {
                onVisit(pass, index);
                yield return items[index];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ObservedSet<T>(IEnumerable<T> items, Action? onContains = null, Action? onEnumerate = null)
        : IReadOnlySet<T>
    {
        private readonly HashSet<T> _items = new(items);

        public int Count => _items.Count;
        public bool Contains(T item)
        {
            onContains?.Invoke();
            return _items.Contains(item);
        }

        public IEnumerator<T> GetEnumerator()
        {
            onEnumerate?.Invoke();
            return _items.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public bool IsProperSubsetOf(IEnumerable<T> other) => _items.IsProperSubsetOf(other);
        public bool IsProperSupersetOf(IEnumerable<T> other) => _items.IsProperSupersetOf(other);
        public bool IsSubsetOf(IEnumerable<T> other) => _items.IsSubsetOf(other);
        public bool IsSupersetOf(IEnumerable<T> other) => _items.IsSupersetOf(other);
        public bool Overlaps(IEnumerable<T> other) => _items.Overlaps(other);
        public bool SetEquals(IEnumerable<T> other) => _items.SetEquals(other);
    }
}
