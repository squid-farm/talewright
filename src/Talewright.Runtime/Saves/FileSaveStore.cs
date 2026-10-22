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
        var name = Convert.ToHexString(Encoding.UTF8.GetBytes(slot));
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
            var name = Path.GetFileNameWithoutExtension(path);
            try
            {
                var slot = new UTF8Encoding(false, true).GetString(Convert.FromHexString(name));
                SaveNames.Validate(storyId, slot);
                slots.Add(slot);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                throw new InvalidDataException("A saved game has an unrecognized filename. Move it out of your save folder and refresh the list.", ex);
            }
        }

        return Task.FromResult<IReadOnlyList<string>>(slots.Order(StringComparer.Ordinal).ToArray());
    }

    public async Task<byte[]> ReadAsync(string storyId, string slot)
    {
        await using var stream = new FileStream(SlotPath(storyId, slot), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is 0 or > StoryHistory.MaxSaveBytes)
        {
            throw new InvalidDataException("This saved game is empty or too large to load (maximum 2 MiB). Choose another save.");
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
