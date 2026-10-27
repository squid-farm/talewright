using BenchmarkDotNet.Attributes;
using System.IO.Compression;
using Talewright.Talemark.Compilation;

namespace Talewright.BenchmarkDotNet;

[MemoryDiagnoser]
[JsonExporterAttribute.Full]
public class BoundaryInputBenchmarks
{
    private const int MaxFileBytes = 5 * 1024 * 1024;
    private const long MaxExpandedBytes = 50L * 1024 * 1024;
    private string _root = null!;
    private string _archivePath = null!;
    private string? _expectedError;

    [Params(
        "file-exact",
        "file-over-one",
        "total-exact",
        "total-over-one",
        "count-exact",
        "count-over-one",
        "depth-exact",
        "depth-over-one",
        "zip-file-exact",
        "zip-file-over-one",
        "zip-total-exact",
        "zip-total-over-one",
        "zip-count-exact",
        "zip-count-over-one")]
    public string Boundary { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"talewright-boundary-{Environment.ProcessId}-{Guid.NewGuid():N}-{Boundary}");
        _archivePath = _root + ".zip";
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, StorySource.ManifestFileName), "id: abcde\n");

        switch (Boundary)
        {
            case "file-exact":
                WriteSizedFile(Path.Combine(_root, "main.tale"), MaxFileBytes);
                break;
            case "file-over-one":
                WriteSizedFile(Path.Combine(_root, "main.tale"), MaxFileBytes + 1);
                break;
            case "total-exact":
                AddEqualSizedFiles(10, MaxFileBytes - 1);
                break;
            case "total-over-one":
                AddEqualSizedFiles(9, MaxFileBytes - 1);
                WriteSizedFile(Path.Combine(_root, "chapter-9.tale"), MaxFileBytes);
                break;
            case "count-exact":
                AddEmptyFiles(999);
                break;
            case "count-over-one":
                AddEmptyFiles(1_000);
                break;
            case "depth-exact":
                AddNestedFile(64);
                break;
            case "depth-over-one":
                AddNestedFile(65);
                _expectedError = "64 levels";
                break;
            case "zip-file-exact":
                CreateZip([("main.tale", MaxFileBytes)]);
                break;
            case "zip-file-over-one":
                CreateZip([("main.tale", MaxFileBytes + 1)]);
                _expectedError = "5 MiB";
                break;
            case "zip-total-exact":
                CreateZip(Enumerable.Range(0, 9)
                    .Select(index => ($"chapter-{index}.tale", (long)MaxFileBytes))
                    .Append(("chapter-9.tale", MaxFileBytes - 10L)));
                break;
            case "zip-total-over-one":
                CreateZip(Enumerable.Range(0, 9)
                    .Select(index => ($"chapter-{index}.tale", (long)MaxFileBytes))
                    .Append(("chapter-9.tale", MaxFileBytes - 9L)));
                _expectedError = "50 MiB";
                break;
            case "zip-count-exact":
                CreateZip(Enumerable.Range(0, 999).Select(index => ($"chapter-{index:D4}.tale", 0L)));
                break;
            case "zip-count-over-one":
                CreateZip(Enumerable.Range(0, 1_000).Select(index => ($"chapter-{index:D4}.tale", 0L)));
                _expectedError = "1,000 story files";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(Boundary), Boundary, "Unknown boundary workload.");
        }

        _ = ReadBoundary();
    }

    [Benchmark]
    public int ReadBoundary()
    {
        try
        {
            StorySource source;
            if (Boundary.StartsWith("zip-", StringComparison.Ordinal))
            {
                using var zip = File.OpenRead(_archivePath);
                source = StorySource.FromZip(zip);
            }
            else
            {
                source = StorySource.FromDirectory(_root);
            }

            if (_expectedError is not null)
            {
                throw new InvalidOperationException(
                    $"Boundary '{Boundary}' should have failed with '{_expectedError}'.");
            }

            if (Boundary is "file-exact" or "zip-file-exact" &&
                    source.Files.Values.Max(text => System.Text.Encoding.UTF8.GetByteCount(text)) != MaxFileBytes ||
                Boundary is "total-exact" or "zip-total-exact" &&
                    source.Files.Values.Sum(text => (long)System.Text.Encoding.UTF8.GetByteCount(text)) != MaxExpandedBytes ||
                Boundary is "count-exact" or "zip-count-exact" && source.Files.Count != 1_000 ||
                Boundary == "depth-exact" && source.Files.Count != 2)
            {
                throw new InvalidOperationException($"Boundary '{Boundary}' did not exercise its exact supported limit.");
            }

            return source.Files.Count;
        }
        catch (InvalidDataException ex) when (
            _expectedError is not null &&
            ex.Message.Contains(_expectedError, StringComparison.Ordinal))
        {
            return ex.Message.Length;
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        if (File.Exists(_archivePath))
        {
            File.Delete(_archivePath);
        }
    }

    private void AddEqualSizedFiles(int count, int bytes)
    {
        for (var index = 0; index < count; index++)
        {
            WriteSizedFile(Path.Combine(_root, $"chapter-{index}.tale"), bytes);
        }
    }

    private void AddEmptyFiles(int count)
    {
        for (var index = 0; index < count; index++)
        {
            File.Create(Path.Combine(_root, $"chapter-{index:D4}.tale")).Dispose();
        }
    }

    private void AddNestedFile(int depth)
    {
        var directory = _root;
        for (var index = 0; index < depth; index++)
        {
            directory = Path.Combine(directory, "d");
        }

        Directory.CreateDirectory(directory);
        File.Create(Path.Combine(directory, "main.tale")).Dispose();
    }

    private static void WriteSizedFile(string path, long bytes)
    {
        using var file = File.Create(path);
        file.SetLength(bytes);
    }

    private void CreateZip(IEnumerable<(string Path, long Bytes)> files)
    {
        using var archive = ZipFile.Open(_archivePath, ZipArchiveMode.Create);
        WriteZipEntry(archive, StorySource.ManifestFileName, 10);
        foreach (var (path, bytes) in files)
        {
            WriteZipEntry(archive, path, bytes);
        }
    }

    private static void WriteZipEntry(ZipArchive archive, string path, long bytes)
    {
        using var output = archive.CreateEntry(path, CompressionLevel.Optimal).Open();
        var buffer = new byte[81920];
        Array.Fill(buffer, (byte)'x');
        while (bytes > 0)
        {
            var write = (int)Math.Min(buffer.Length, bytes);
            output.Write(buffer, 0, write);
            bytes -= write;
        }
    }
}
