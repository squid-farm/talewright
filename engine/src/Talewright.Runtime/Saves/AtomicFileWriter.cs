namespace Talewright.Runtime.Saves;

/// <summary>Writes a file through a temporary sibling before replacing its destination.</summary>
public static class AtomicFileWriter
{
    public static async Task WriteAsync(string target, byte[] data, bool overwrite)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(target))!;
        var temporary = Path.Combine(directory, $".talewright-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(data);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, target, overwrite);
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
