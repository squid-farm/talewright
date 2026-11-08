using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Talemark.Tests;

public sealed class ManifestNullContainerTests
{
    private const string Marker = "__container_value__";
    private const string Game = "game:\n  id: null-containers\n  title: Null containers\n  start: beginning\n";
    private const string Player = "characters:\n  player:\n    first-name: Ada\n";

    public static IEnumerable<object[]> InvalidContainers()
    {
        var cases = new (string Name, string Yaml, string Message)[]
        {
            ("game", $"game: {Marker}\n" + Player, "must be a mapping"),
            ("game.ruleset", Game + $"  ruleset: {Marker}\n" + Player, "must be a mapping"),
            ("game.ruleset.disable", Game + $"  ruleset:\n    disable: {Marker}\n" + Player, "must be a list"),
            ("game.ruleset.stats", Game + $"  ruleset:\n    stats: {Marker}\n" + Player, "must be a list"),
            ("game.ruleset.stats entry", Game + $"  ruleset:\n    stats:\n      - {Marker}\n" + Player, "must be a mapping"),
            ("game.creation", Game + $"  creation: {Marker}\n" + Player, "must be a mapping"),
            ("game.creation.pronouns", Game + $"  creation:\n    pronouns: {Marker}\n" + Player, "must be a list"),
            ("game.creation.stats", Game + $"  creation:\n    stats: {Marker}\n" + Player, "must be a mapping"),
            ("game.creation.stats.str", Game + $"  ruleset: rpg\n  creation:\n    stats:\n      str: {Marker}\n" + Player, "must be a list"),
            ("game.creation.presets", Game + $"  creation:\n    presets: {Marker}\n" + Player, "must be a mapping"),
            ("game.creation.presets entry", Game + $"  creation:\n    presets:\n      explorer: {Marker}\n" + Player, "must be a mapping"),
            ("game.creation.presets.stats", Game + $"  creation:\n    presets:\n      explorer:\n        stats: {Marker}\n" + Player, "must be a mapping"),
            ("characters", Game + $"characters: {Marker}\n", "must be a mapping"),
            ("characters.player", Game + $"characters:\n  player: {Marker}\n", "must be a mapping"),
            ("characters.player.stats", Game + Player + $"    stats: {Marker}\n", "must be a mapping"),
            ("characters.player.inventory", Game + Player + $"    inventory: {Marker}\n", "inventory must be a list"),
            ("characters entry", Game + Player + $"  guide: {Marker}\n", "must be a mapping"),
            ("items", Game + Player + $"items: {Marker}\n", "must be a mapping"),
            ("items.modifiers", Game + Player + $"items:\n  coin:\n    modifiers: {Marker}\n", "must be a mapping"),
            ("factions", Game + Player + $"factions: {Marker}\n", "must be a mapping"),
            ("vars", Game + Player + $"vars: {Marker}\n", "must be a mapping"),
            ("flags", Game + Player + $"flags: {Marker}\n", "must be a list"),
            ("triggers", Game + Player + $"triggers: {Marker}\n", "must be a list"),
            ("triggers entry", Game + Player + $"triggers:\n  - {Marker}\n", "must be a mapping"),
        };
        var values = new[] { "null", "Null", "NULL", "~", "", "''", "\"\"", "'~'", "\"~\"" };
        foreach (var (name, yaml, message) in cases)
        {
            foreach (var value in values)
            {
                yield return [name, yaml, message, value];
            }
        }
    }

    [Theory]
    [MemberData(nameof(InvalidContainers))]
    public void Null_and_scalar_values_do_not_replace_containers(string name, string template, string message, string value)
    {
        var marker = template.IndexOf(Marker, StringComparison.Ordinal);
        Assert.True(marker >= 0);
        var before = template[..marker];
        // YamlDotNet marks an empty scalar at the separator's trailing space.
        var location = new SourceLocation(StorySource.ManifestFileName,
            before.Count(character => character == '\n') + 1,
            before.Length - before.LastIndexOf('\n') - (value.Length == 0 ? 1 : 0));
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = template.Replace(Marker, value, StringComparison.Ordinal),
            ["main.tale"] = "=== beginning\nHello.\n-> END\n",
        });
        var compiled = StoryCompiler.Compile(source);
        var analyzed = StoryAnalyzer.Analyze(source);
        Assert.False(compiled.Success);
        Assert.Null(compiled.Story);
        foreach (var diagnostics in new[] { compiled.Diagnostics, analyzed.Diagnostics })
        {
            Assert.True(diagnostics.Any(diagnostic => diagnostic.Code == "TW4003"
                && diagnostic.Location == location
                && diagnostic.Message.Contains(message, StringComparison.Ordinal)),
                $"{name}={value}: expected {message} at {location}; got {string.Join("; ", diagnostics)}");
        }
    }

    [Theory]
    [InlineData("items: ~", 1, 8)]
    [InlineData("factions: ~", 1, 11)]
    [InlineData("flags: ~", 1, 8)]
    [InlineData("vars: ~", 1, 7)]
    [InlineData("triggers: ~", 1, 11)]
    [InlineData("characters:\n  guide: ~", 2, 10)]
    [InlineData("items:\n  coin:\n    modifiers: ~", 3, 16)]
    public void Null_containers_in_supporting_files_retain_their_source_location(string fragment, int line, int column)
    {
        const string file = "nested/optional.talewright.yaml";
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = Game + Player,
            [file] = fragment,
            ["main.tale"] = "=== beginning\nHello.\n-> END\n",
        });
        var compiled = StoryCompiler.Compile(source);
        var analyzed = StoryAnalyzer.Analyze(source);
        Assert.False(compiled.Success);
        foreach (var diagnostics in new[] { compiled.Diagnostics, analyzed.Diagnostics })
        {
            Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "TW4003"
                && diagnostic.Location == new SourceLocation(file, line, column));
        }
    }

    [Fact]
    public void Omitted_and_explicitly_empty_containers_keep_their_defaults()
    {
        var empty = """
            game:
              id: empty-containers
              title: Empty containers
              start: beginning
              ruleset: {disable: [], stats: []}
              creation: {pronouns: [], stats: {}, presets: {}}
            characters:
              player: {first-name: Ada, stats: {}, inventory: []}
            items: {}
            factions: {}
            flags: []
            vars: {}
            triggers: []
            """;
        foreach (var yaml in new[] { Game + Player, empty })
        {
            var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
            {
                [StorySource.ManifestFileName] = yaml,
                ["main.tale"] = "=== beginning\nHello.\n-> END\n",
            }));
            Assert.True(result.Success, string.Join("; ", result.Diagnostics));
            Assert.Empty(result.Diagnostics);
            Assert.Empty(result.Story!.World.Items);
            Assert.Empty(result.Story.World.Factions);
            Assert.Empty(result.Story.Triggers);
        }
    }
}
