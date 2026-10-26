using System.Text;
using Talewright.Talemark.Compilation;

namespace Talewright.Cli;

internal sealed record WebHostingSize(long TotalBytes, long LargestFileBytes, int FileCount)
{
    // itch.io documents MB without a byte definition; decimal MB is the conservative advisory comparison.
    internal const long FileWarningBytes = 200_000_000;
    internal const long TotalWarningBytes = 500_000_000;
    internal const int FileWarningCount = 1_000;

    internal static WebHostingSize Measure(IEnumerable<long> sizes)
    {
        long total = 0;
        long largest = 0;
        var count = 0;
        foreach (var size in sizes)
        {
            total = checked(total + size);
            largest = Math.Max(largest, size);
            ++count;
        }
        return new(total, largest, count);
    }

    internal static WebHostingSize Estimate(StorySource source, string root) =>
        Measure(source.Files.Values.Select(text => (long)Encoding.UTF8.GetByteCount(text))
            .Concat(StoryImageResolver.CompletionPaths(source)
                .Select(path => new FileInfo(Path.Combine(root, "images", path)).Length)));

    internal static WebHostingSize FromDirectory(string directory) =>
        Measure(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path).Length));

    internal void WriteWarning(TextWriter output, bool estimate)
    {
        if (TotalBytes <= TotalWarningBytes && LargestFileBytes <= FileWarningBytes && FileCount <= FileWarningCount)
            return;

        var basis = estimate
            ? "source-only estimate (UTF-8 text and supported images; excludes generated runtime and compression copies)"
            : "final extracted web payload (includes generated runtime and precompressed copies)";
        output.WriteLine(FormattableString.Invariant(
            $"Warning: itch.io HTML5 hosting guidance exceeded; {basis}: total {TotalBytes} bytes, largest file {LargestFileBytes} bytes, {FileCount} files. Recorded guidance: 500 MB total, 200 MB per file, 1,000 files (MB compared as decimal bytes)."));
        output.WriteLine("Advisory only: this warning does not prevent publication. Check your host's current requirements: https://itch.io/docs/creators/html5");
    }
}
