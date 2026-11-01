using Talewright.Core.Identity;
using Talewright.Core.Rulesets;
using Talewright.Core.World;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
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

    private static StorySymbols Symbols()
    {
        var options = CompilerOptions.Default;
        var world = new WorldDefinition(new RulesetBuilder("analysis-cancellation").Build(),
            [], [], [], [], new Dictionary<VarId, int>());
        return new StorySymbols
        {
            World = world,
            Flags = new HashSet<FlagId>(),
            Variables = new HashSet<VarId>(),
            Scenes = new HashSet<SceneId>(),
            Predicates = options.Predicates,
            Modifiers = options.Modifiers,
        };
    }
}
