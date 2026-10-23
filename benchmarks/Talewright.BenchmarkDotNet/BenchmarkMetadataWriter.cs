using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Talewright.Testing;

namespace Talewright.BenchmarkDotNet;

internal static class BenchmarkMetadataWriter
{
    public static void Write(PerformanceCorpusMetadata corpus, long? peakWorkingSetBytes = null)
    {
        var output = Environment.GetEnvironmentVariable("TALEWRIGHT_BENCHMARK_OUTPUT")
            ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "real-file-benchmarks");
        Directory.CreateDirectory(output);
        var metadata = new
        {
            corpus,
            commit = ReadCommit(),
            workingTreeDirty = ReadGitOutput("status", "--porcelain", "--untracked-files=all").Length != 0,
            runtime = RuntimeInformation.FrameworkDescription,
            benchmarkDotNet = typeof(BenchmarkAttribute).Assembly.GetName().Version?.ToString(),
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            processorCount = Environment.ProcessorCount,
            cpuModel = ReadCpuModel(),
            physicalMemoryBytes = ReadPhysicalMemoryBytes(),
            runtimeAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            powerAndCacheSettings = Environment.GetEnvironmentVariable("TALEWRIGHT_BENCHMARK_POWER_CACHE")
                ?? "not recorded",
            benchmarkWorkerPeakWorkingSetBytes = peakWorkingSetBytes,
            peakWorkingSetScope = "whole BenchmarkDotNet worker process; includes setup and harness",
        };
        var path = Path.Combine(output, $"metadata-{corpus.Tier}-{Environment.ProcessId}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string ReadCommit()
    {
        var configured = Environment.GetEnvironmentVariable("GITHUB_SHA")
            ?? Environment.GetEnvironmentVariable("TALEWRIGHT_BENCHMARK_COMMIT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var commit = ReadGitOutput("rev-parse", "HEAD");
        if (string.IsNullOrWhiteSpace(commit))
        {
            throw new InvalidOperationException("Git returned an empty commit for the benchmark metadata.");
        }

        return commit;
    }

    private static string ReadGitOutput(params string[] arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("git", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = FindRepositoryRoot(),
        }) ?? throw new InvalidOperationException("Could not start git to record the benchmark commit.");
        var commit = process.StandardOutput.ReadToEnd().Trim();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Could not read Git metadata: {error}");
        }

        return commit;
    }

    private static string FindRepositoryRoot()
    {
        var configured = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")
            ?? Environment.GetEnvironmentVariable("TALEWRIGHT_BENCHMARK_REPOSITORY");
        if (!string.IsNullOrWhiteSpace(configured) && IsRepositoryRoot(configured))
        {
            return configured;
        }

        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (IsRepositoryRoot(directory.FullName))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new InvalidOperationException(
            "Could not locate the repository root; set GITHUB_WORKSPACE or TALEWRIGHT_BENCHMARK_REPOSITORY.");
    }

    private static bool IsRepositoryRoot(string path) =>
        Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"));

    private static long? ReadPhysicalMemoryBytes()
    {
        const string memoryInfo = "/proc/meminfo";
        if (!File.Exists(memoryInfo))
        {
            return null;
        }

        var line = File.ReadLines(memoryInfo)
            .FirstOrDefault(value => value.StartsWith("MemTotal:", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], out var kibibytes)
            ? kibibytes * 1024
            : null;
    }

    private static string ReadCpuModel()
    {
        const string cpuInfo = "/proc/cpuinfo";
        if (!File.Exists(cpuInfo))
        {
            return "not available";
        }

        return File.ReadLines(cpuInfo)
            .FirstOrDefault(line => line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
            ?.Split(':', 2).Last().Trim() ?? "not available";
    }
}
