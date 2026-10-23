using System.IO.Compression;
using System.Collections.ObjectModel;

namespace Talewright.Tale.Compilation;

/// <summary>
/// The raw files of a story: <c>story.yaml</c> plus <c>*.tale</c> files, keyed by relative path with <c>/</c>
/// separators. Hosts build one from a folder, a zip, or embedded resources; the compiler never touches the file
/// system itself (so it works unchanged in the browser).
/// </summary>
public sealed class StorySource
{
    private const int MaxStoryFiles = 1_000;
    private const long MaxStoryFileBytes = 5L * 1024 * 1024;
    private const long MaxExpandedBytes = 50L * 1024 * 1024;

    /// <summary>Name of the manifest file every story must contain at its root.</summary>
    public const string ManifestFileName = "story.yaml";

    /// <summary>Extension of narrative files.</summary>
    public const string TaleExtension = ".tale";

    public StorySource(IReadOnlyDictionary<string, string> files) =>
        Files = new ReadOnlyDictionary<string, string>(files.ToDictionary(StringComparer.Ordinal));

    /// <summary>Relative path → file text.</summary>
    public IReadOnlyDictionary<string, string> Files { get; }

    /// <summary>The <c>.tale</c> files in stable (ordinal path) order.</summary>
    public IEnumerable<KeyValuePair<string, string>> TaleFiles =>
        Files.Where(f => f.Key.EndsWith(TaleExtension, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Key, StringComparer.Ordinal);

    /// <summary>Loads story files with the same count/size bounds as ZIP loading. Symbolic links are rejected.</summary>
    public static StorySource FromDirectory(string directory)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        long totalBytes = 0;
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((directory, 0));
        while (pending.TryPop(out var current))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(current.Path))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"Story folders cannot contain symbolic links: '{path}'.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (current.Depth >= 64)
                    {
                        throw new InvalidDataException("Story folders may be nested at most 64 levels deep.");
                    }

                    pending.Push((path, current.Depth + 1));
                }
                else if (IsStoryFile(path))
                {
                    if (files.Count >= MaxStoryFiles)
                    {
                        throw new InvalidDataException("A story may contain at most 1,000 story files.");
                    }

                    using var input = File.OpenRead(path);
                    files[Path.GetRelativePath(directory, path).Replace('\\', '/')] = ReadText(input, path, ref totalBytes);
                }
            }
        }

        return new StorySource(files);
    }

    /// <summary>
    /// Loads a zipped story. If <c>story.yaml</c> is not at the archive root but inside a single top-level folder
    /// (the usual result of "compress folder"), that folder is treated as the root. At most 1,000 story files,
    /// 5 MiB per file and 50 MiB total expanded text are read.
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
        var storyEntries = entries
            .Where(entry => entry.FullName.Replace('\\', '/').StartsWith(root, StringComparison.Ordinal))
            .ToList();
        if (storyEntries.Count > MaxStoryFiles)
        {
            throw new InvalidDataException($"A story archive may contain at most {MaxStoryFiles:N0} story files.");
        }

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var entry in storyEntries)
        {
            var path = entry.FullName.Replace('\\', '/');
            using var input = entry.Open();
            files[path[root.Length..]] = ReadText(input, path, ref totalBytes);
        }

        return new StorySource(files);
    }

    private static string ReadText(Stream input, string path, ref long totalBytes)
    {
        using var content = new MemoryStream();
        var buffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (content.Length + bytesRead > MaxStoryFileBytes)
            {
                throw new InvalidDataException($"Story file '{path}' exceeds the 5 MiB expanded-size limit.");
            }

            if (totalBytes + bytesRead > MaxExpandedBytes)
            {
                throw new InvalidDataException("The story exceeds the 50 MiB total expanded-text limit.");
            }

            content.Write(buffer, 0, bytesRead);
            totalBytes += bytesRead;
        }

        content.Position = 0;
        using var reader = new StreamReader(content);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Reads just <c>title</c> from story.yaml, without compiling (for story pickers). Returns null if it can't.
    /// </summary>
    public string? ReadTitle()
    {
        if (!Files.TryGetValue(ManifestFileName, out var yaml))
        {
            return null;
        }

        try
        {
            var stream = new YamlDotNet.RepresentationModel.YamlStream();
            stream.Load(new StringReader(yaml));
            return stream.Documents.FirstOrDefault()?.RootNode is YamlDotNet.RepresentationModel.YamlMappingNode root
                && root.Children.TryGetValue(new YamlDotNet.RepresentationModel.YamlScalarNode("title"), out var title)
                ? (title as YamlDotNet.RepresentationModel.YamlScalarNode)?.Value
                : null;
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return null;
        }
    }

    private static bool IsStoryFile(string path) =>
        Path.GetFileName(path) == ManifestFileName || path.EndsWith(TaleExtension, StringComparison.OrdinalIgnoreCase);
}
