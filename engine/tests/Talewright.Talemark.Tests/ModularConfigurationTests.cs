using System.IO.Compression;
using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Narrative.Rendering;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;

namespace Talewright.Talemark.Tests;

public sealed class ModularConfigurationTests
{
    private const string Game = "game: { id: modular, title: Modular, start: start, seed: 42 }\n";
    private const string Characters = """
        characters:
          player: { first-name: Ada, inventory: [rope] }
          guide: { first-name: Bea }
        """;
    private const string World = """
        items: { rope: { name: Rope } }
        factions: { guild: { name: Guild, standing: 5 } }
        flags: [ready]
        vars: { count: 2, weather: "sunny" }
        """;
    private const string Triggers = """
        triggers:
          - id: first
            when: flag:ready
            do: var:count += 1
            once: true
          - id: second
            when: faction:guild > 0
            do: set flag:done
            once: true
        """;
    private const string Tale = """
        === start
        > guide: Hello, {player:first-name}. {var:weather}, {var:count}.
        {image("camp.png", "Camp")}
        -> END
        """;

    private static Dictionary<string, string> Files() => new()
    {
        [StorySource.ManifestFileName] = Game,
        ["a/people.talewright.yaml"] = Characters,
        ["z/mixed.talewright.yaml"] = World,
        ["b/events.talewright.yaml"] = Triggers,
        ["chapters/main.tale"] = Tale,
    };

    private static StoryDefinition Compile(IReadOnlyDictionary<string, string> files)
    {
        var result = StoryCompiler.Compile(new StorySource(files, ["images/camp.png"]));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
        return result.Story!;
    }

    private static void Equivalent(StoryDefinition expected, StoryDefinition actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.World.Characters.Keys.OrderBy(id => id.Value),
            actual.World.Characters.Keys.OrderBy(id => id.Value));
        foreach (var id in expected.World.Characters.Keys)
        {
            Assert.Equal(expected.World.Characters[id].FullName, actual.World.Characters[id].FullName);
            Assert.Equal(expected.World.Characters[id].Inventory.OrderBy(item => item.Key.Value),
                actual.World.Characters[id].Inventory.OrderBy(item => item.Key.Value));
        }
        Assert.Equal(expected.World.Items.Keys, actual.World.Items.Keys);
        foreach (var id in expected.World.Items.Keys)
        {
            Assert.Equal(expected.World.Items[id].Name, actual.World.Items[id].Name);
            Assert.Equal(expected.World.Items[id].Modifiers, actual.World.Items[id].Modifiers);
        }
        Assert.Equal(expected.World.Factions, actual.World.Factions);
        Assert.Equal(expected.World.Flags, actual.World.Flags);
        Assert.Equal(expected.World.Variables, actual.World.Variables);
        Assert.Equal(expected.World.TextVariables, actual.World.TextVariables);
        Assert.Equal(expected.Triggers.Select(trigger => trigger.Id), actual.Triggers.Select(trigger => trigger.Id));
        Assert.Equal(new[] { "first", "second" }, actual.Triggers.Select(trigger => trigger.Id));
        foreach (var (left, right) in expected.Triggers.Zip(actual.Triggers))
        {
            Assert.Equal(left.When, right.When);
            Assert.Equal(left.Effects, right.Effects);
            Assert.Equal(left.Once, right.Once);
        }
        string Render(StoryDefinition story) => new TemplateRenderer().Render(
            story.GetScene(story.Start).Body, GameState.CreateInitial(story.World, 42)).ToPlainText();
        Assert.Equal(Render(expected), Render(actual));
        Assert.Contains("Hello, Ada. sunny, 2.", Render(actual));
    }

    [Fact]
    public void Root_and_distributed_declarations_resolve_before_references_regardless_of_discovery_order()
    {
        var files = Files();
        var root = new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = string.Join("\n", Game, Characters, World, Triggers),
            ["chapters/main.tale"] = Tale,
        };
        Equivalent(Compile(root), Compile(files));
        Equivalent(Compile(files), Compile(files.Reverse().ToDictionary()));
        files["a/people.talewright.yaml"] = "characters:\n  player: { first-name: Ada, inventory: [rope] }\n";
        files["nested/other/guide.talewright.yaml"] = "characters:\n  guide: { first-name: Bea }\n";
        Equivalent(Compile(root), Compile(files));
    }

    [Fact]
    public void Moving_declarations_changes_source_fingerprint_not_behavior()
    {
        var files = Files();
        var original = Compile(files);
        files["other/nested/person.talewright.yaml"] = files["a/people.talewright.yaml"];
        files.Remove("a/people.talewright.yaml");
        var moved = Compile(files);
        Equivalent(original, moved);
        Assert.NotEqual(original.ContentFingerprint, moved.ContentFingerprint);
    }

    [Fact]
    public void Shipped_four_chapter_story_keeps_shared_configuration_in_root_and_can_be_split()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Talewright.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var source = StorySource.FromDirectory(Path.Combine(root.FullName, "samples", "LostRelic"));
        Assert.Equal(StorySource.ManifestFileName, Assert.Single(source.Files.Keys, path => path.EndsWith(".yaml")));
        Assert.Contains("images/camp-art.jpeg", source.ImagePaths);
        var result = StoryCompiler.Compile(source);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
        Assert.Equal("Tomas Okafor", result.Story!.World.Characters[new("guide")].FullName);
        var files = source.Files.ToDictionary();
        const string guide = "  guide:\n";
        var manifest = files[StorySource.ManifestFileName].Replace("\r\n", "\n");
        var start = manifest.IndexOf(guide, StringComparison.Ordinal);
        var end = manifest.IndexOf("\nfactions:", start, StringComparison.Ordinal);
        files[StorySource.ManifestFileName] = manifest.Remove(start, end - start);
        files["characters/guide.talewright.yaml"] = "characters:\n" + manifest[start..end];
        var split = StoryCompiler.Compile(new StorySource(files.Reverse().ToDictionary(), source.ImagePaths));
        Assert.True(split.Success, string.Join("\n", split.Diagnostics));
        Assert.Empty(split.Diagnostics);
        Assert.Equal(result.Story.World.Characters[new("guide")].FullName, split.Story!.World.Characters[new("guide")].FullName);
        Assert.Equal(result.Story.Triggers.Select(trigger => trigger.Id), split.Story.Triggers.Select(trigger => trigger.Id));
        Assert.Equal(result.Story.Scenes.Select(scene => scene.Id), split.Story.Scenes.Select(scene => scene.Id));
    }

    [Theory]
    [InlineData("characters: { player: { first-name: Other } }", "a/people.talewright.yaml:2:3")]
    [InlineData("items: { rope: Other }", "z/mixed.talewright.yaml:1:10")]
    [InlineData("factions: { guild: { name: Other } }", "z/mixed.talewright.yaml:2:13")]
    [InlineData("flags: [ready]", "z/mixed.talewright.yaml:3:9")]
    [InlineData("vars: { count: \"text\" }", "z/mixed.talewright.yaml:4:9")]
    [InlineData("vars: { weather: 7 }", "z/mixed.talewright.yaml:4:19")]
    [InlineData("triggers: []", "b/events.talewright.yaml:2:3")]
    public void Duplicate_identities_and_nonadditive_sections_report_both_original_sources(string duplicate, string first)
    {
        var files = Files();
        files["zz/duplicate.talewright.yaml"] = duplicate;
        var result = StoryCompiler.Compile(new StorySource(files, ["images/camp.png"]));
        Assert.False(result.Success);
        Assert.Null(result.Story);
        var error = Assert.Single(result.Errors);
        Assert.Equal("TW4007", error.Code);
        Assert.Equal("zz/duplicate.talewright.yaml", error.Location.File);
        Assert.Equal(1, error.Location.Line);
        Assert.Contains(first, error.Message);
    }

    [Theory]
    [InlineData("characters/story.talewright.yaml", Characters)]
    [InlineData("nested/deeper/story.talewright.yaml", "game: { id: other }\n" + Characters)]
    [InlineData("nested/story.talewright.yaml", "invalid: [")]
    public void Additional_reserved_manifests_are_rejected_before_assembly(string file, string text)
    {
        var files = Files();
        files[file] = text;
        var source = new StorySource(files, ["images/camp.png"]);
        var result = StoryCompiler.Compile(source);
        Assert.False(result.Success);
        var error = Assert.Single(result.Errors);
        Assert.Equal("TW4002", error.Code);
        Assert.Equal(file, error.Location.File);
        Assert.Equal(1, error.Location.Line);
        Assert.Equal(1, error.Location.Column);
        Assert.Contains("reserved", error.Message);
        Assert.Equal(result.Diagnostics, StoryAnalyzer.Analyze(source).Diagnostics);
    }

    [Theory]
    [InlineData("game: { id: forbidden }", "TW4002", 1)]
    [InlineData("characters:\n  guide: {first-name: Bea, inventory: [missing]}", "TW4005", 2)]
    [InlineData("items: [", "TW4001", 2)]
    [InlineData("flags: not-a-list", "TW4003", 1)]
    [InlineData("vars: []", "TW4003", 1)]
    [InlineData("characters: {}\n---\ncharacters: {}", "TW4001", 1)]
    [InlineData("triggers:\n  - id: invalid\n    when: var:missing > 0\n    do: set flag:done", "TW2004", 3)]
    public void Fragment_diagnostics_and_unsaved_analysis_retain_original_file_and_position(string text, string code, int line)
    {
        var files = Files();
        files.Remove("b/events.talewright.yaml");
        files["a/people.talewright.yaml"] = "characters: { player: { first-name: Ada } }";
        files["nested/unsaved.talewright.yaml"] = text;
        var source = new StorySource(files, ["images/camp.png"]);
        var compiled = StoryCompiler.Compile(source);
        var analyzed = StoryAnalyzer.Analyze(source);
        foreach (var diagnostics in new[] { compiled.Diagnostics, analyzed.Diagnostics })
        {
            var error = Assert.Single(diagnostics, d => d.Code == code);
            Assert.Equal("nested/unsaved.talewright.yaml", error.Location.File);
            Assert.Equal(line, error.Location.Line);
            Assert.True(error.Location.Column > 0);
        }
    }

    [Fact]
    public void Folder_and_wrapped_archive_load_the_same_split_illustrated_story()
    {
        var root = Path.Combine(Path.GetTempPath(), "talewright-modular-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (file, text) in Files())
            {
                var path = Path.Combine(root, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
            }
            Directory.CreateDirectory(Path.Combine(root, "images"));
            File.WriteAllBytes(Path.Combine(root, "images/camp.png"), [1, 2, 3]);
            File.WriteAllText(Path.Combine(root, "ignored.yaml"), "not configuration");
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    var entry = zip.CreateEntry("wrapper/" + Path.GetRelativePath(root, path).Replace('\\', '/'));
                    using var output = entry.Open();
                    using var input = File.OpenRead(path);
                    input.CopyTo(output);
                }
            }
            buffer.Position = 0;
            var folder = StorySource.FromDirectory(root);
            var archive = StorySource.FromZip(buffer);
            Assert.Equal(folder.Files.OrderBy(file => file.Key), archive.Files.OrderBy(file => file.Key));
            Assert.Equal(folder.ImagePaths, archive.ImagePaths);
            Assert.Equal(["images/camp.png"], archive.ImagePaths);
            Assert.DoesNotContain("ignored.yaml", archive.Files.Keys);
            Equivalent(Compile(folder.Files), Compile(archive.Files));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
