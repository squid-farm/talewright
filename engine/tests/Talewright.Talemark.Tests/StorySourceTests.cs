using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using System.IO.Compression;

namespace Talewright.Talemark.Tests;

public sealed class StorySourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"talewright-source-test-{Guid.NewGuid():N}");

    public StorySourceTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("")]
    [InlineData("wrapped/")]
    public void Caller_owned_archive_maps_images_relative_to_the_same_source_root(string prefix)
    {
        using var buffer = new MemoryStream();
        using (var output = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var path in new[] { StorySource.ManifestFileName, "main.tale",
                "characters/guide.talewright.yaml", "unrelated.yaml", "unrelated.yml" })
            {
                using var writer = new StreamWriter(output.CreateEntry(prefix + path).Open());
                writer.Write(path);
            }
            using var image = output.CreateEntry(prefix + "images/camp.png").Open();
            image.Write([1, 2, 3]);
        }
        buffer.Position = 0;
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
        var source = StorySource.FromZip(archive, out var images);

        Assert.Equal(3, source.Files.Count);
        Assert.Equal("characters/guide.talewright.yaml", source.Files["characters/guide.talewright.yaml"]);
        Assert.Equal(["images/camp.png"], source.ImagePaths);
        Assert.Same(archive.GetEntry(prefix + "images/camp.png"), images["images/camp.png"]);
        using (var actual = images["images/camp.png"].Open())
            Assert.Equal(1, actual.ReadByte());
        archive.Dispose();
        buffer.Position = 0;
        var sourceOnly = StorySource.FromZip(buffer);
        Assert.Equal(source.Files.OrderBy(pair => pair.Key), sourceOnly.Files.OrderBy(pair => pair.Key));
        Assert.True(source.ImagePaths.SetEquals(sourceOnly.ImagePaths));
        Assert.True(buffer.CanRead);
        Assert.Throws<ObjectDisposedException>(() => images["images/camp.png"].Open());
    }

    [Theory]
    [InlineData("", "images/camp.png")]
    [InlineData("", "images\\camp.png")]
    [InlineData("wrapped/", "wrapped\\images\\camp.png")]
    public void Duplicate_normalized_archive_image_members_are_rejected_instead_of_selecting_ambiguous_bytes(
        string prefix, string duplicate)
    {
        using var buffer = new MemoryStream();
        using (var output = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            output.CreateEntry(prefix + StorySource.ManifestFileName);
            output.CreateEntry(prefix + "images/camp.png");
            output.CreateEntry(duplicate);
        }
        buffer.Position = 0;
        Assert.Contains("duplicate", Assert.Throws<InvalidDataException>(() => StorySource.FromZip(buffer)).Message);
    }

    [Theory]
    [InlineData("images/../camp.png")]
    [InlineData("/images/camp.png")]
    [InlineData("images\\camp.png")]
    [InlineData("images//camp.png")]
    [InlineData("images/./camp.png")]
    [InlineData("images/camp.png?query")]
    [InlineData("https://example.test/camp.png")]
    [InlineData("../characters/guide.talewright.yaml")]
    public void Unsafe_archive_members_have_explicit_diagnostics(string path)
    {
        using var buffer = new MemoryStream();
        using (var output = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            output.CreateEntry(path);
        buffer.Position = 0;
        var error = Assert.Throws<InvalidDataException>(() => StorySource.FromZip(buffer));
        Assert.Contains("Unsafe story archive member", error.Message);
        Assert.Contains(path, error.Message);
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(1_001)]
    public void Directory_story_file_count_has_the_same_boundary_as_archives(int count)
    {
        for (var i = 0; i < count; i++)
        {
            File.WriteAllText(Path.Combine(_root, $"{i}.tale"), "");
        }

        if (count == 1_000)
        {
            Assert.Equal(count, StorySource.FromDirectory(_root).Files.Count);
        }
        else
        {
            Assert.Contains("1,000", Assert.Throws<InvalidDataException>(() => StorySource.FromDirectory(_root)).Message);
        }
    }

    [Fact]
    public void Directory_accepts_exact_file_and_total_bounds_but_rejects_one_extra_byte()
    {
        var text = new string('x', 5 * 1024 * 1024);
        for (var i = 0; i < 10; i++)
        {
            var name = i == 9 ? "shared.talewright.yaml" : $"{i}.tale";
            File.WriteAllText(Path.Combine(_root, name), text);
        }

        Assert.Equal(10, StorySource.FromDirectory(_root).Files.Count);
        File.WriteAllText(Path.Combine(_root, "extra.tale"), "x");
        Assert.Contains("50 MiB", Assert.Throws<InvalidDataException>(() => StorySource.FromDirectory(_root)).Message);
        File.Delete(Path.Combine(_root, "extra.tale"));
        File.AppendAllText(Path.Combine(_root, "0.tale"), "x");
        Assert.Contains("5 MiB", Assert.Throws<InvalidDataException>(() => StorySource.FromDirectory(_root)).Message);
    }

    [Fact]
    public void Directory_and_zip_preserve_stream_reader_bom_and_malformed_input_behavior()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["utf16.tale"] = [0xFF, 0xFE, (byte)'h', 0, (byte)'i', 0],
            ["malformed.tale"] = [0xC3, 0x28],
        };
        foreach (var (path, bytes) in files)
        {
            File.WriteAllBytes(Path.Combine(_root, path), bytes);
        }

        var directorySource = StorySource.FromDirectory(_root);
        using var zipBuffer = new MemoryStream();
        using (var archive = new ZipArchive(zipBuffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, bytes) in files)
            {
                using var entry = archive.CreateEntry(path).Open();
                entry.Write(bytes);
            }
        }

        zipBuffer.Position = 0;
        var zipSource = StorySource.FromZip(zipBuffer);

        foreach (var source in new[] { directorySource, zipSource })
        {
            Assert.Equal("hi", source.Files["utf16.tale"]);
            Assert.Equal("\uFFFD(", source.Files["malformed.tale"]);
        }
    }

    [Fact]
    public void Zip_expanded_limits_are_enforced_while_reading_entry_bytes()
    {
        using var oversizedFile = new MemoryStream();
        using (var archive = new ZipArchive(oversizedFile, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = archive.CreateEntry("nested/large.talewright.yaml").Open();
            WriteBytes(entry, 5L * 1024 * 1024 + 1);
        }

        oversizedFile.Position = 0;
        Assert.Contains("5 MiB", Assert.Throws<InvalidDataException>(() => StorySource.FromZip(oversizedFile)).Message);

        using var oversizedTotal = new MemoryStream();
        using (var archive = new ZipArchive(oversizedTotal, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var index = 0; index < 11; index++)
            {
                var name = index == 10 ? "nested/config.talewright.yaml" : $"{index}.tale";
                using var entry = archive.CreateEntry(name).Open();
                WriteBytes(entry, index == 10 ? 1 : 5L * 1024 * 1024);
            }
        }

        oversizedTotal.Position = 0;
        Assert.Contains("50 MiB", Assert.Throws<InvalidDataException>(() => StorySource.FromZip(oversizedTotal)).Message);
    }

    [Fact]
    public void Directory_accepts_64_levels_and_rejects_65()
    {
        var path = _root;
        for (var i = 0; i < 64; i++)
        {
            path = Path.Combine(path, "d");
        }

        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "main.tale"), "Text");
        Assert.Single(StorySource.FromDirectory(_root).Files);
        Directory.CreateDirectory(Path.Combine(path, "d"));
        Assert.Contains("64 levels", Assert.Throws<InvalidDataException>(() => StorySource.FromDirectory(_root)).Message);
    }

    [Fact]
    public void Excluded_child_is_not_read_or_counted_against_parent_limits()
    {
        File.WriteAllText(Path.Combine(_root, "main.tale"), "=== parent\n-> END\n");
        var child = Path.Combine(_root, "child");
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(child, "large.tale"), new string('x', 5 * 1024 * 1024 + 1));
        for (var index = 0; index < 1_000; index++)
            File.WriteAllText(Path.Combine(child, $"{index}.tale"), "");
        var source = StorySource.FromDirectory(_root, [child]);
        Assert.Equal("main.tale", Assert.Single(source.Files).Key);
        Assert.Throws<InvalidDataException>(() => StorySource.FromDirectory(_root));
        File.WriteAllText(Path.Combine(_root, "large.tale"), new string('x', 5 * 1024 * 1024 + 1));
        Assert.Contains("5 MiB", Assert.Throws<InvalidDataException>(() => StorySource.FromDirectory(_root, [child])).Message);
    }

    private const string Manifest = "game:\n  id: canonical\n  title: Canonical\n  start: start\n  ruleset: { base: none }\ncharacters:\n  player:\n    first-name: Tester\n";
    private const string Tale = "=== start\nHello.\n-> END\n";

    [Fact]
    public void Manifest_filename_is_the_canonical_talewright_name() =>
        Assert.Equal("story.talewright.yaml", StorySource.ManifestFileName);

    [Fact]
    public void Title_extraction_uses_game_title_without_a_legacy_root_fallback()
    {
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = "title: Legacy title\n",
        });

        Assert.Null(source.ReadTitle());
    }

    [Fact]
    public void Directory_loads_canonical_manifest_and_reports_extra_root_configuration()
    {
        Directory.CreateDirectory(Path.Combine(_root, "chapters"));
        File.WriteAllText(Path.Combine(_root, "story.talewright.yaml"), Manifest);
        File.WriteAllText(Path.Combine(_root, "chapters", "main.tale"), Tale);
        File.WriteAllText(Path.Combine(_root, "chapters", "characters.talewright.yaml"), "characters: {guide: {first-name: Guide}}\n");
        File.WriteAllText(Path.Combine(_root, "story.yaml"), "title: Unrelated\n");
        File.WriteAllText(Path.Combine(_root, "other.talewright.yaml"), "title: Not a manifest\n");

        var source = StorySource.FromDirectory(_root);

        Assert.Equal(["chapters/characters.talewright.yaml", "chapters/main.tale", "other.talewright.yaml", "story.talewright.yaml"],
            source.Files.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Canonical", source.ReadTitle());
        var diagnostic = Assert.Single(StoryCompiler.Compile(source).Diagnostics, d => d.Code == "TW4002");
        Assert.Equal(new SourceLocation("other.talewright.yaml", 1, 1), diagnostic.Location);
    }

    [Fact]
    public void Zip_loads_extra_root_configuration_and_reports_it()
    {
        using var zip = new MemoryStream(Talewright.Testing.TestArchives.Create([
            new("story.talewright.yaml", Manifest),
            new("extra.talewright.yaml", "title: Extra\n"),
            new("chapters/characters.talewright.yaml", "characters: {guide: {first-name: Guide}}\n"),
            new("main.tale", Tale),
        ]));

        var source = StorySource.FromZip(zip);
        Assert.Contains("extra.talewright.yaml", source.Files.Keys);
        Assert.Contains("chapters/characters.talewright.yaml", source.Files.Keys);
        var diagnostic = Assert.Single(StoryCompiler.Compile(source).Diagnostics, d => d.Code == "TW4002");
        Assert.Equal(new SourceLocation("extra.talewright.yaml", 1, 1), diagnostic.Location);
    }

    [Fact]
    public void Nested_story_manifest_cannot_declare_game_or_establish_a_second_story()
    {
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = Manifest,
            ["chapter/story.talewright.yaml"] = "game: {id: nested, title: Nested, start: nested}\n",
            ["chapters/main.tale"] = Tale,
        });

        var diagnostic = Assert.Single(StoryCompiler.Compile(source).Diagnostics, d => d.Code == "TW4002");
        Assert.Equal(new SourceLocation("chapter/story.talewright.yaml", 1, 7), diagnostic.Location);
    }

    [Fact]
    public void Directory_with_only_generic_story_yaml_reports_missing_canonical_manifest()
    {
        File.WriteAllText(Path.Combine(_root, "story.yaml"), Manifest);
        File.WriteAllText(Path.Combine(_root, "main.tale"), Tale);

        var source = StorySource.FromDirectory(_root);

        Assert.Null(source.ReadTitle());
        var diagnostic = Assert.Single(StoryCompiler.Compile(source).Diagnostics, d => d.Code == "TW4002");
        Assert.Contains("story.talewright.yaml", diagnostic.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("MyStory/")]
    public void Zip_loads_canonical_manifest_at_root_or_in_single_wrapping_folder(string prefix)
    {
        using var zip = new MemoryStream(Talewright.Testing.TestArchives.Create([
            new($"{prefix}story.talewright.yaml", Manifest),
            new($"{prefix}chapters/main.tale", Tale),
            new($"{prefix}story.yaml", "title: Unrelated\n"),
        ]));

        var source = StorySource.FromZip(zip);

        Assert.Equal(["chapters/main.tale", "story.talewright.yaml"], source.Files.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Canonical", source.ReadTitle());
        Assert.True(StoryCompiler.Compile(source).Success);
    }

    [Fact]
    public void Zip_with_only_generic_story_yaml_reports_missing_canonical_manifest()
    {
        using var zip = new MemoryStream(Talewright.Testing.TestArchives.Create([
            new("MyStory/story.yaml", Manifest),
            new("MyStory/main.tale", Tale),
        ]));

        var source = StorySource.FromZip(zip);

        Assert.Equal(["MyStory/main.tale"], source.Files.Keys);
        Assert.Contains(StoryCompiler.Compile(source).Diagnostics, d => d.Code == "TW4002");
    }

    [Fact]
    public void In_memory_image_catalogue_is_an_ordinal_snapshot_separate_from_text()
    {
        var images = new List<string> { "images/map.png", "images/Map.png" };
        var source = new StorySource(new Dictionary<string, string> { ["main.tale"] = Tale }, images);
        images.Clear();

        Assert.Equal(["images/Map.png", "images/map.png"], source.ImagePaths.Order(StringComparer.Ordinal));
        Assert.False(source.ImagePaths.Contains("images/MAP.png"));
        Assert.Single(source.Files);
        Assert.Empty(new StorySource(new Dictionary<string, string>()).ImagePaths);
    }

    [Theory]
    [InlineData("")]
    [InlineData("WrappedStory/")]
    public void Directory_zip_and_in_memory_image_lookup_agree(string prefix)
    {
        var files = new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = Manifest,
            ["main.tale"] = Tale,
            ["images/maps/old_map-v2.png"] = "not decoded as an image",
            ["images/maps/old_map-v2.webp"] = "not decoded as an image",
            ["images/unused.gif"] = "unsupported format",
            ["other.png"] = "not in images/",
        };
        foreach (var (path, text) in files)
        {
            var fullPath = Path.Combine(_root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, text);
        }

        using var zip = new MemoryStream(Talewright.Testing.TestArchives.Create(
            files.Select(file => new KeyValuePair<string, string>(prefix + file.Key, file.Value))
                .Concat(prefix.Length == 0 ? [] : new[]
                {
                    new KeyValuePair<string, string>("images/outside-wrapper.png", "outside wrapper"),
                    new KeyValuePair<string, string>("OtherStory/images/other.png", "other story"),
                })));
        string[] expectedPaths = ["images/maps/old_map-v2.png", "images/maps/old_map-v2.webp", "images/unused.gif"];
        var memory = new StorySource(files.Where(f => f.Key is "main.tale" or StorySource.ManifestFileName)
            .ToDictionary(), expectedPaths);
        foreach (var source in new[] { StorySource.FromDirectory(_root), StorySource.FromZip(zip), memory })
        {
            Assert.Equal(expectedPaths, source.ImagePaths.Order(StringComparer.Ordinal));
            Assert.Equal(["main.tale", StorySource.ManifestFileName], source.Files.Keys.Order(StringComparer.Ordinal));
            Assert.True(StoryCompiler.Compile(source).Success);

            var diagnostics = new DiagnosticBag();
            var location = new SourceLocation("main.tale", 2, 1);
            var result = StoryImageResolver.Resolve(source, "maps/old_map-v2", null, location, diagnostics);
            Assert.Equal(new ResolvedStoryImage("images/maps/old_map-v2.png", "Old map v2"), result);
            Assert.Equal(["TW2013", "TW2014"], diagnostics.Items.Select(d => d.Code));
            Assert.All(diagnostics.Items, diagnostic => Assert.Equal(location, diagnostic.Location));
        }
    }

    [Fact]
    public void Excluded_image_directory_is_not_catalogued()
    {
        var excluded = Path.Combine(_root, "images", "excluded");
        Directory.CreateDirectory(excluded);
        File.WriteAllBytes(Path.Combine(excluded, "map.png"), [0, 255, 0]);
        Assert.Empty(StorySource.FromDirectory(_root, [excluded]).ImagePaths);
    }

    [Fact]
    public void Binary_images_are_not_read_as_text_or_subject_to_text_size_limits()
    {
        Directory.CreateDirectory(Path.Combine(_root, "images"));
        using (var image = File.Create(Path.Combine(_root, "images", "map.png")))
        {
            image.SetLength(5 * 1024 * 1024 + 1);
        }

        var directory = StorySource.FromDirectory(_root);
        Assert.Empty(directory.Files);
        Assert.Equal("images/map.png", Assert.Single(directory.ImagePaths));

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var image = archive.CreateEntry("images/map.png").Open();
            image.Write(new byte[5 * 1024 * 1024 + 1]);
        }

        buffer.Position = 0;
        var zipped = StorySource.FromZip(buffer);
        Assert.Empty(zipped.Files);
        Assert.Equal("images/map.png", Assert.Single(zipped.ImagePaths));
    }

    [Fact]
    public void Zip_image_folder_case_is_not_normalized_into_safe_lookup_targets()
    {
        using var zip = new MemoryStream(Talewright.Testing.TestArchives.Create([
            new("Images/map.png", ""),
            new("images/", ""),
        ]));
        var source = StorySource.FromZip(zip);
        var diagnostics = new DiagnosticBag();
        Assert.Null(StoryImageResolver.Resolve(source, "map.png", "Map.", new("main.tale", 2, 1), diagnostics));
        Assert.Equal("TW2012", Assert.Single(diagnostics.Items).Code);
    }

    [Fact]
    public void Unix_backslashes_in_filenames_are_not_mistaken_for_directory_separators()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(Path.Combine(_root, "images"));
        File.WriteAllBytes(Path.Combine(_root, "images", @"maps\map.png"), [0, 255]);

        var source = StorySource.FromDirectory(_root);
        Assert.Equal(@"images/maps\map.png", Assert.Single(source.ImagePaths));
        var diagnostics = new DiagnosticBag();
        Assert.Null(StoryImageResolver.Resolve(source, "maps/map.png", "Map.", new("main.tale", 2, 1), diagnostics));
        Assert.Equal("TW2012", Assert.Single(diagnostics.Items).Code);
    }

    [Fact]
    public void Directory_image_symbolic_links_are_rejected()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(Path.Combine(_root, "images"));
        var target = Path.Combine(_root, "outside.png");
        File.WriteAllBytes(target, [0, 255]);
        File.CreateSymbolicLink(Path.Combine(_root, "images", "map.png"), target);

        Assert.Contains("symbolic links", Assert.Throws<InvalidDataException>(() => StorySource.FromDirectory(_root)).Message);
    }

    [Fact]
    public void Zip_image_symbolic_links_are_rejected_without_reading_their_target()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var image = archive.CreateEntry("images/map.png");
            image.ExternalAttributes = unchecked((int)0xA1FF0000);
            using var writer = new StreamWriter(image.Open());
            writer.Write("../outside.png");
        }

        buffer.Position = 0;
        Assert.Contains("symbolic links", Assert.Throws<InvalidDataException>(() => StorySource.FromZip(buffer)).Message);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static void WriteBytes(Stream output, long count)
    {
        var buffer = new byte[8192];
        while (count > 0)
        {
            var length = (int)Math.Min(count, buffer.Length);
            output.Write(buffer, 0, length);
            count -= length;
        }
    }
}
