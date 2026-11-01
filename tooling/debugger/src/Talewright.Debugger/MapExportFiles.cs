namespace Talewright.Debugger;

internal static class MapExportFiles
{
    internal static async Task SaveAsync(string path, string format, Stream contents)
    {
        var extension = format switch
        {
            "svg" => ".svg",
            "png" => ".png",
            _ => throw new ArgumentException("Map exports must use SVG or PNG format.", nameof(format)),
        };
        var target = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(target), extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"The selected map export path must end in {extension}.", nameof(path));
        }

        var directory = Path.GetDirectoryName(target)
            ?? throw new ArgumentException("The selected map export path has no parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await contents.CopyToAsync(output);
                await output.FlushAsync();
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
