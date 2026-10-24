using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Talewright.Talemark.Compilation;

namespace Talewright.Testing;

public sealed record PerformanceCorpusTier(
    string Name,
    int StoryFileCount,
    int SceneCount,
    long ExpandedBytes,
    int HistoryDepth)
{
    public static IReadOnlyList<PerformanceCorpusTier> All { get; } =
    [
        new("Typical", 10, 100, 512 * 1024, 100),
        new("Medium", 100, 1_000, 5 * 1024 * 1024, 1_000),
        new("Large", 500, 10_000, 25 * 1024 * 1024, 5_000),
        new("Gigantic", 1_000, 50_000, 50 * 1024 * 1024, 10_000),
    ];

    public static PerformanceCorpusTier Get(string name) =>
        All.FirstOrDefault(tier => tier.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown performance corpus tier '{name}'.", nameof(name));
}

public sealed record PerformanceCorpusMetadata(
    string Tier,
    int StoryFileCount,
    int SceneCount,
    int ChoiceCount,
    long ExpandedBytes,
    long CompressedBytes,
    int Seed,
    string Sha256);

public sealed class PerformanceCorpus : IDisposable
{
    private const int SeedValue = 57057;
    private const long MaxFileBytes = 5L * 1024 * 1024;
    private const long MaxExpandedBytes = 50L * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private PerformanceCorpus(string root, string archivePath, PerformanceCorpusMetadata metadata)
    {
        Root = root;
        ArchivePath = archivePath;
        Metadata = metadata;
    }

    public string Root { get; }
    public string ArchivePath { get; }
    public PerformanceCorpusMetadata Metadata { get; }

    public static PerformanceCorpus Create(
        PerformanceCorpusTier tier,
        string root,
        bool lowCompressibility = false)
    {
        root = Path.GetFullPath(root);
        var archivePath = root + ".zip";
        if (Directory.Exists(root) || File.Exists(root) || File.Exists(archivePath))
        {
            throw new IOException($"Performance corpus output already exists: '{root}'.");
        }

        if (tier.StoryFileCount is < 2 or > 1_000 ||
            tier.SceneCount <= 0 ||
            tier.ExpandedBytes is <= 0 or > MaxExpandedBytes ||
            tier.HistoryDepth is < 0 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(tier), "Corpus dimensions exceed the supported story limits.");
        }

        Directory.CreateDirectory(root);
        try
        {
            var taleFileCount = tier.StoryFileCount - 1;
            var builders = Enumerable.Range(0, taleFileCount).Select(_ => new StringBuilder()).ToArray();
            for (var file = 0; file < taleFileCount; file++)
            {
                var firstScene = file * tier.SceneCount / taleFileCount;
                var endScene = (file + 1) * tier.SceneCount / taleFileCount;
                for (var scene = firstScene; scene < endScene; scene++)
                {
                    AppendScene(builders[file], scene, tier.SceneCount);
                }
            }

            var manifest = CreateManifest(tier.Name);
            var currentBytes = Utf8.GetByteCount(manifest) + builders.Sum(builder => (long)Utf8.GetByteCount(builder.ToString()));
            if (currentBytes > tier.ExpandedBytes)
            {
                throw new InvalidOperationException(
                    $"The generated {tier.Name} scenes require {currentBytes:N0} bytes, above the target {tier.ExpandedBytes:N0}.");
            }

            var remaining = tier.ExpandedBytes - currentBytes;
            var random = new DeterministicText(SeedValue);
            for (var file = 0; file < builders.Length; file++)
            {
                var filesLeft = builders.Length - file;
                var allocation = (remaining + filesLeft - 1) / filesLeft;
                AppendPadding(builders[file], checked((int)allocation), lowCompressibility, random);
                remaining -= allocation;
            }

            if (remaining != 0)
            {
                throw new InvalidOperationException("The performance corpus byte target was not reached.");
            }

            File.WriteAllText(Path.Combine(root, StorySource.ManifestFileName), manifest, Utf8);
            for (var file = 0; file < builders.Length; file++)
            {
                var path = Path.Combine(root, "chapters", $"part-{file:D4}.tale");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, builders[file].ToString(), Utf8);
            }

            var sourceFiles = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.Ordinal)
                .ToArray();
            var expandedBytes = sourceFiles.Sum(path => new FileInfo(path).Length);
            var maxFileBytes = sourceFiles.Max(path => new FileInfo(path).Length);
            if (sourceFiles.Length != tier.StoryFileCount ||
                expandedBytes != tier.ExpandedBytes ||
                maxFileBytes > MaxFileBytes)
            {
                throw new InvalidOperationException(
                    $"Generated corpus does not match its limits: {sourceFiles.Length} files, {expandedBytes:N0} bytes, largest file {maxFileBytes:N0} bytes.");
            }

            using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                foreach (var path in sourceFiles)
                {
                    var entry = zip.CreateEntry(Path.GetRelativePath(root, path).Replace('\\', '/'), CompressionLevel.Optimal);
                    entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    using var output = entry.Open();
                    using var input = File.OpenRead(path);
                    input.CopyTo(output);
                }
            }

            var metadata = new PerformanceCorpusMetadata(
                tier.Name,
                sourceFiles.Length,
                tier.SceneCount,
                builders.Sum(builder => CountChoices(builder.ToString())),
                expandedBytes,
                new FileInfo(archivePath).Length,
                SeedValue,
                ComputeHash(sourceFiles, root));
            return new PerformanceCorpus(root, archivePath, metadata);
        }
        catch
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            throw;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }

        if (File.Exists(ArchivePath))
        {
            File.Delete(ArchivePath);
        }
    }

    private static string CreateManifest(string tierName) => $$"""
        id: performance-{{tierName.ToLowerInvariant()}}
        title: {{tierName}} performance corpus
        version: 1.0.0
        start: scene.0
        seed: {{SeedValue}}
        ruleset:
          base: generic
        vars: {visits: 0}
        characters:
          player: {first-name: Tester}
        """;

    private static void AppendScene(StringBuilder builder, int index, int sceneCount)
    {
        builder.Append("=== scene.").Append(index).Append('\n');
        builder.Append("@title Scene ").Append(index).Append('\n');
        builder.Append("@tags benchmark\n");
        builder.Append("@priority ").Append(index % 10).Append('\n');
        if (index == 0)
        {
            builder.Append("@hub benchmark\n");
            builder.Append("@hub-limit 20\n");
        }

        builder.Append("~ var:visits += 1\n");
        builder.Append("Passage for {player:first-name}; visit {var:visits}.\n");
        builder.Append("* [Next] { var:visits += 1 } -> scene.").Append((index + 1) % sceneCount).Append('\n');
        if (index % 10 == 0)
        {
            builder.Append("* [Qualified] (requires: player:int >= 0) -> scene.")
                .Append((index + 1) % sceneCount).Append('\n');
        }

        builder.Append('\n');
    }

    private static void AppendPadding(
        StringBuilder builder,
        int bytes,
        bool lowCompressibility,
        DeterministicText random)
    {
        if (bytes == 0)
        {
            return;
        }

        if (bytes < 3)
        {
            builder.Append('\n', bytes);
            return;
        }

        builder.Append("//");
        var contentBytes = bytes - 3;
        if (lowCompressibility)
        {
            random.Append(builder, contentBytes);
        }
        else
        {
            builder.Append('x', contentBytes);
        }

        builder.Append('\n');
    }

    private static int CountChoices(string content) =>
        content.Split('\n').Count(line => line.StartsWith("* [", StringComparison.Ordinal));

    private static string ComputeHash(IEnumerable<string> paths, string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in paths)
        {
            var relativePath = Utf8.GetBytes(Path.GetRelativePath(root, path).Replace('\\', '/'));
            hash.AppendData(relativePath);
            hash.AppendData([0]);
            using var input = File.OpenRead(path);
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private sealed class DeterministicText(int seed)
    {
        private uint _state = unchecked((uint)seed);

        public void Append(StringBuilder builder, int count)
        {
            const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
            for (var i = 0; i < count; i++)
            {
                _state ^= _state << 13;
                _state ^= _state >> 17;
                _state ^= _state << 5;
                builder.Append(Alphabet[(int)(_state % Alphabet.Length)]);
            }
        }
    }
}
