using System.IO.Compression;
using System.Collections.ObjectModel;
using System.Collections.Frozen;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Talemark.Compilation;

/// <summary>
/// The source text and image-path catalogue of a story, keyed by relative path with <c>/</c>
/// separators. Hosts build one from a folder, a zip, or embedded resources; the compiler never touches the file
/// system itself (so it works unchanged in the browser).
/// </summary>
public sealed class StorySource
{
    // These bound damaged or hostile archives (ZIP bombs), not story size, so they sit far above any real story.
    private const int MaxStoryFiles = 100_000;
    private const long MaxStoryFileBytes = 256L * 1024 * 1024;
    /// <summary>Defensive expanded-text bound for story archives; not a folder or archive-image budget.</summary>
    public const long MaxArchiveExpandedBytes = 1024L * 1024 * 1024;

    internal readonly record struct ArchiveLimits(int MaxFiles, long MaxFileBytes, long MaxTotalBytes)
    {
        public static ArchiveLimits Default { get; } = new(MaxStoryFiles, MaxStoryFileBytes, MaxArchiveExpandedBytes);
    }

    /// <summary>Name of the manifest file every story must contain at its root.</summary>
    public const string ManifestFileName = "story.talewright.yaml";

    internal const string ConfigurationFileExtension = ".talewright.yaml";

    /// <summary>Extension of narrative files.</summary>
    public const string TaleExtension = ".tale";

    public StorySource(IReadOnlyDictionary<string, string> files, IEnumerable<string>? imagePaths = null)
    {
        Files = new ReadOnlyDictionary<string, string>(files.ToDictionary(StringComparer.Ordinal));
        ImagePaths = (imagePaths ?? []).ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>Relative path → file text.</summary>
    public IReadOnlyDictionary<string, string> Files { get; }

    /// <summary>Story-root-relative image file paths, including the <c>images/</c> prefix. No image bytes are loaded.</summary>
    public IReadOnlySet<string> ImagePaths { get; }

    /// <summary>The <c>.tale</c> files in stable (ordinal path) order.</summary>
    public IEnumerable<KeyValuePair<string, string>> TaleFiles =>
        Files.Where(f => f.Key.EndsWith(TaleExtension, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Key, StringComparer.Ordinal);

    /// <summary>Loads story files without authoring size ceilings. Symbolic links and excessive nesting are rejected.</summary>
    public static StorySource FromDirectory(string directory)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var imagePaths = new List<string>();
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
                else
                {
                    var relativePath = Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');
                    if (IsImagePath(relativePath))
                    {
                        imagePaths.Add(relativePath);
                    }

                    if (!IsStoryFile(path)) continue;
                    using var reader = new StreamReader(path);
                    files[relativePath] = reader.ReadToEnd();
                }
            }
        }

        return new StorySource(files, imagePaths);
    }

    /// <summary>
    /// Loads a zipped story. If <c>story.talewright.yaml</c> is not at the archive root but inside a single top-level folder
    /// (the usual result of "compress folder"), that folder is treated as the root. At most 100,000 story files,
    /// 256 MiB per file and 1 GiB total expanded text are read.
    /// </summary>
    public static StorySource FromZip(Stream zip) => FromZip(zip, ArchiveLimits.Default);

    internal static StorySource FromZip(Stream zip, ArchiveLimits limits)
    {
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        return FromZip(archive, out _, out _, limits);
    }

    /// <summary>
    /// Loads source text and maps story-relative image paths to archive members in the same discovery pass.
    /// The caller owns the archive and must keep it open while using the returned members. The source's text and
    /// image catalogue are detached from the archive; only the caller-held mapping depends on its lifetime.
    /// Unsafe member names and duplicate normalized image paths are rejected, not silently remapped.
    /// </summary>
    public static StorySource FromZip(ZipArchive archive, out IReadOnlyDictionary<string, ZipArchiveEntry> imageEntries)
        => FromZip(archive, out imageEntries, out _);

    /// <summary>
    /// Loads a caller-owned archive and reports actual expanded text bytes, including BOMs and configuration.
    /// This count is not a combined text/image budget. Mapping lifetime matches the archive.
    /// </summary>
    public static StorySource FromZip(ZipArchive archive, out IReadOnlyDictionary<string, ZipArchiveEntry> imageEntries,
        out long expandedTextBytes) => FromZip(archive, out imageEntries, out expandedTextBytes, ArchiveLimits.Default);

    private static StorySource FromZip(ZipArchive archive, out IReadOnlyDictionary<string, ZipArchiveEntry> imageEntries,
        out long expandedTextBytes, ArchiveLimits limits)
    {
        foreach (var entry in archive.Entries)
        {
            var path = entry.FullName.EndsWith('/') ? entry.FullName[..^1] : entry.FullName;
            if (!StoryImageResolver.IsSafePath(path))
            {
                throw new InvalidDataException($"Unsafe story archive member '{entry.FullName}'. Use relative paths with / separators and no traversal or web addresses.");
            }
        }

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
        if (storyEntries.Count > limits.MaxFiles)
        {
            throw new InvalidDataException($"A story archive may contain at most {limits.MaxFiles:N0} story files.");
        }

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var images = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrEmpty(entry.Name) || !normalized.StartsWith(root, StringComparison.Ordinal)) continue;
            var path = normalized[root.Length..];
            if (!IsImagePath(path)) continue;
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
            {
                throw new InvalidDataException($"Story images cannot be symbolic links: '{entry.FullName}'.");
            }

            if (!images.TryAdd(path, entry))
            {
                throw new InvalidDataException($"Story image '{path}' has duplicate archive entries.");
            }
        }

        long totalBytes = 0;
        foreach (var entry in storyEntries)
        {
            var path = entry.FullName.Replace('\\', '/');
            using var input = entry.Open();
            files[path[root.Length..]] = ReadText(input, path, ref totalBytes, limits);
        }

        imageEntries = new ReadOnlyDictionary<string, ZipArchiveEntry>(images);
        expandedTextBytes = totalBytes;
        return new StorySource(files, images.Keys);
    }

    private static string ReadText(Stream input, string path, ref long totalBytes, ArchiveLimits limits)
    {
        using var bounded = new LimitedReadStream(input, path, totalBytes, limits);
        using var reader = new StreamReader(bounded, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        var text = reader.ReadToEnd();
        totalBytes += bounded.BytesRead;
        return text;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        _ when bytes % (1024L * 1024 * 1024) == 0 => $"{bytes / 1024 / 1024 / 1024:N0} GiB",
        _ when bytes % (1024L * 1024) == 0 => $"{bytes / 1024 / 1024:N0} MiB",
        _ => $"{bytes:N0}-byte",
    };

    private sealed class LimitedReadStream(Stream input, string path, long initialTotalBytes, ArchiveLimits limits) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadCore(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) => ReadCore(buffer);

        private int ReadCore(Span<byte> buffer)
        {
            if (buffer.IsEmpty)
            {
                return 0;
            }

            var fileRemaining = limits.MaxFileBytes - BytesRead;
            var totalRemaining = limits.MaxTotalBytes - initialTotalBytes - BytesRead;
            var requested = (int)Math.Min(buffer.Length, Math.Min(fileRemaining, totalRemaining) + 1);
            var bytesRead = input.Read(buffer[..requested]);
            if (bytesRead > fileRemaining)
            {
                throw new InvalidDataException($"Story file '{path}' exceeds the {FormatBytes(limits.MaxFileBytes)} expanded-size limit.");
            }

            if (bytesRead > totalRemaining)
            {
                throw new InvalidDataException($"The story exceeds the {FormatBytes(limits.MaxTotalBytes)} total expanded-text limit.");
            }

            BytesRead += bytesRead;
            return bytesRead;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Reads just <c>game.title</c> from story.talewright.yaml, without compiling (for story pickers). Returns null if it can't.
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
                && root.Children.TryGetValue(new YamlDotNet.RepresentationModel.YamlScalarNode(ManifestVocabulary.Game), out var gameNode)
                && gameNode is YamlDotNet.RepresentationModel.YamlMappingNode game
                && game.Children.TryGetValue(new YamlDotNet.RepresentationModel.YamlScalarNode(ManifestVocabulary.Title), out var title)
                ? (title as YamlDotNet.RepresentationModel.YamlScalarNode)?.Value
                : null;
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return null;
        }
    }

    /// <summary>Reads <c>game.version</c> from story.talewright.yaml. Returns null when it is omitted or empty.</summary>
    public string? ReadVersion()
    {
        if (!Files.TryGetValue(ManifestFileName, out var yaml))
        {
            return null;
        }

        try
        {
            var stream = new YamlDotNet.RepresentationModel.YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.FirstOrDefault()?.RootNode is not YamlDotNet.RepresentationModel.YamlMappingNode root ||
                !root.Children.TryGetValue(new YamlDotNet.RepresentationModel.YamlScalarNode(ManifestVocabulary.Game), out var gameNode) ||
                gameNode is not YamlDotNet.RepresentationModel.YamlMappingNode game ||
                !game.Children.TryGetValue(new YamlDotNet.RepresentationModel.YamlScalarNode(ManifestVocabulary.Version), out var version))
            {
                return null;
            }

            var value = (version as YamlDotNet.RepresentationModel.YamlScalarNode)?.Value?.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return null;
        }
    }

    /// <summary>Recognizes reserved story configuration inputs, including supporting files.</summary>
    public static bool IsConfigurationFile(string path) =>
        path.EndsWith(ConfigurationFileExtension, StringComparison.Ordinal);

    /// <summary>Recognizes source text shared by folder, archive, embedded, and publication hosts.</summary>
    public static bool IsStoryFile(string path) =>
        IsConfigurationFile(path) ||
        path.EndsWith(TaleExtension, StringComparison.OrdinalIgnoreCase);

    private static bool IsImagePath(string path) =>
        path.StartsWith("images/", StringComparison.Ordinal) && path.Length > "images/".Length;
}
