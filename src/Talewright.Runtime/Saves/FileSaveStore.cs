using System.Security.Cryptography;
using System.Text;

namespace Talewright.Runtime.Saves;

/// <summary>Per-user desktop saves. Each replacement is a same-directory atomic rename.</summary>
public sealed class FileSaveStore(string root) : ISaveStore
{
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Talewright", "saves");

    private string StoryDirectory(string storyId)
    {
        SaveNames.Validate(storyId);
        return Path.Combine(root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(storyId))));
    }

    private string SlotPath(string storyId, string slot)
    {
        SaveNames.Validate(storyId, slot);
        var name = Convert.ToBase64String(Encoding.UTF8.GetBytes(slot)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Path.Combine(StoryDirectory(storyId), name + ".json");
    }

    public Task<IReadOnlyList<string>> ListAsync(string storyId)
    {
        var directory = StoryDirectory(storyId);
        string[] paths;
        try
        {
            paths = Directory.GetFiles(directory, "*.json");
        }
        catch (DirectoryNotFoundException)
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var slots = new List<string>();
        foreach (var path in paths)
        {
            var name = Path.GetFileNameWithoutExtension(path).Replace('-', '+').Replace('_', '/');
            name = name.PadRight((name.Length + 3) / 4 * 4, '=');
            try
            {
                var slot = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(name));
                SaveNames.Validate(storyId, slot);
                slots.Add(slot);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                throw new InvalidDataException($"Invalid save filename '{Path.GetFileName(path)}'.", ex);
            }
        }

        return Task.FromResult<IReadOnlyList<string>>(slots.Order(StringComparer.Ordinal).ToArray());
    }

    public async Task<byte[]> ReadAsync(string storyId, string slot)
    {
        await using var stream = new FileStream(SlotPath(storyId, slot), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is 0 or > StoryHistory.MaxSaveBytes)
        {
            throw new InvalidDataException("Save data must contain 1 byte to 2 MiB.");
        }

        var data = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(data);
        return data;
    }

    public async Task WriteAsync(string storyId, string slot, byte[] data, bool overwrite = false)
    {
        SaveNames.ValidateData(data);
        var target = SlotPath(storyId, slot);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + $".{Guid.NewGuid():N}.tmp";
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
