using System.Globalization;
using System.Text;
using Talewright.Talemark.Compilation;

namespace Talewright.Testing;

public static class BenchmarkStories
{
    public const string TaleFileName = "main.tale";

    public static StorySource CreateSceneLoop(int scenes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scenes);

        var manifest = new ManifestFaker("benchmark", "Benchmark", "scene.0", 42);
        var player = manifest.Character("player", "Tester");
        // The original benchmark leaves this variable undeclared in the manifest.
        var visits = new TaleVariableRef("visits");
        var increment = EffectFaker.Line(EffectFaker.Increase(visits, 1));
        var playerName = SubstitutionFaker.Character(player, "first-name");
        var visitCount = SubstitutionFaker.Variable(visits);
        var tale = new StringBuilder();
        for (var i = 0; i < scenes; i++)
        {
            var number = i.ToString(CultureInfo.InvariantCulture);
            var next = ((i + 1) % scenes).ToString(CultureInfo.InvariantCulture);
            tale.Append(SceneFaker.Header($"scene.{number}")).Append('\n')
                .Append(SceneFaker.Title($"Scene {number}")).Append('\n')
                .Append(increment).Append('\n')
                .Append("Hello, ").Append(playerName).Append(". Visits: ").Append(visitCount).Append(".\n")
                .Append(ChoiceFaker.Create($"scene.{next}", "Next")).Append('\n');
        }

        return new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = manifest.Build(),
            [TaleFileName] = tale.ToString(),
        });
    }
}
