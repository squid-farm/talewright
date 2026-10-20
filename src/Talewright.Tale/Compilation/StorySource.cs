using System.IO.Compression;

namespace Talewright.Tale.Compilation;

/// <summary>
/// The raw files of a story: <c>story.yaml</c> plus <c>*.tale</c> files, keyed by relative path with <c>/</c>
/// separators. Hosts build one from a folder, a zip, or embedded resources; the compiler never touches the file
/// system itself (so it works unchanged in the browser).
/// </summary>
public sealed class StorySource
{
    /// <summary>Name of the manifest file every story must contain at its root.</summary>
    public const string ManifestFileName = "story.yaml";

    /// <summary>Extension of narrative files.</summary>
    public const string TaleExtension = ".tale";

    public StorySource(IReadOnlyDictionary<string, string> files) => Files = files;

    /// <summary>Relative path → file text.</summary>
    public IReadOnlyDictionary<string, string> Files { get; }

    /// <summary>The <c>.tale</c> files in stable (ordinal path) order.</summary>
    public IEnumerable<KeyValuePair<string, string>> TaleFiles =>
        Files.Where(f => f.Key.EndsWith(TaleExtension, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Key, StringComparer.Ordinal);

    /// <summary>Loads every <c>story.yaml</c>/<c>*.tale</c> file under <paramref name="directory"/>.</summary>
    public static StorySource FromDirectory(string directory)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (IsStoryFile(path))
            {
                files[Path.GetRelativePath(directory, path).Replace('\\', '/')] = File.ReadAllText(path);
            }
        }

        return new StorySource(files);
    }

    /// <summary>
    /// Loads a zipped story. If <c>story.yaml</c> is not at the archive root but inside a single top-level folder
    /// (the usual result of "compress folder"), that folder is treated as the root.
    /// </summary>
    public static StorySource FromZip(Stream zip)
    {
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name) && IsStoryFile(e.FullName)).ToList();
        var manifest = entries
            .Select(e => e.FullName.Replace('\\', '/'))
            .Where(p => Path.GetFileName(p) == ManifestFileName)
            .OrderBy(p => p.Length)
            .FirstOrDefault();
        var root = manifest is null ? string.Empty : manifest[..^ManifestFileName.Length];

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var path = entry.FullName.Replace('\\', '/');
            if (!path.StartsWith(root, StringComparison.Ordinal))
            {
                continue;
            }

            using var reader = new StreamReader(entry.Open());
            files[path[root.Length..]] = reader.ReadToEnd();
        }

        return new StorySource(files);
    }

    private static bool IsStoryFile(string path) =>
        Path.GetFileName(path) == ManifestFileName || path.EndsWith(TaleExtension, StringComparison.OrdinalIgnoreCase);
}
