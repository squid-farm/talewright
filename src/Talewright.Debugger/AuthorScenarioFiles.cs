using System.Security.Cryptography;
using System.Text;
using Talewright.Runtime;
using Talewright.Runtime.Saves;

namespace Talewright.Debugger;

/// <summary>Explicit author files, never player save slots or story source. Replacements use same-directory rename.</summary>
public static class AuthorScenarioFiles
{
    public static string DefaultDirectory(string storyId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Talewright", "author-scenarios",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(storyId))));

    public static async Task<byte[]> ReadAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is 0 or > AuthorScenario.MaxBytes)
        {
            throw new InvalidDataException("Import must be nonempty and at most 2 MiB.");
        }
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes);
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException("Import changed while reading; retry.");
        }
        return bytes;
    }

    public static async Task ExportAsync(string path, byte[] bytes, string storyRoot, bool overwriteConfirmed)
    {
        if (bytes.Length is 0 or > AuthorScenario.MaxBytes)
        {
            throw new InvalidDataException("Invalid scenario export size.");
        }
        var target = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(target), AuthorScenario.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Author exports must use the .twscenario extension, not a player .json save.");
        }
        var resolved = Resolve(target);
        if (!string.Equals(Path.GetExtension(resolved), AuthorScenario.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The scenario path resolves to a non-scenario file. Choose a separate .twscenario destination.");
        }
        if (Within(resolved, Resolve(storyRoot)) || Within(resolved, Resolve(FileSaveStore.DefaultRoot)))
        {
            throw new ArgumentException("Choose an author scenario path outside the story source and player save folders.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
        var temporary = resolved + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, resolved, overwriteConfirmed);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static bool Within(string path, string directory) =>
        path.Equals(directory, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string Resolve(string path)
    {
        var full = Path.GetFullPath(path);
        for (var links = 0; ; links++)
        {
            var current = Path.GetPathRoot(full)!;
            var parts = full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var foundLink = false;
            for (var index = 0; index < parts.Length; index++)
            {
                current = Path.Combine(current, parts[index]);
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (info.LinkTarget is null)
                {
                    continue;
                }
                if (links == 64)
                {
                    throw new IOException("Scenario path contains a link cycle or exceeds the 64-link resolution limit.");
                }
                var target = info.ResolveLinkTarget(returnFinalTarget: false)
                    ?? throw new IOException("Cannot resolve scenario path link.");
                full = target.FullName;
                for (var remaining = index + 1; remaining < parts.Length; remaining++)
                {
                    full = Path.Combine(full, parts[remaining]);
                }
                // Restart at the root: a link's target can itself have linked ancestors.
                full = Path.GetFullPath(full);
                foundLink = true;
                break;
            }
            if (!foundLink)
            {
                return current;
            }
        }
    }
}
