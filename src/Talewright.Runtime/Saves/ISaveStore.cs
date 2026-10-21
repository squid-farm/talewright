using System.Text;

namespace Talewright.Runtime.Saves;

public interface ISaveStore
{
    Task<IReadOnlyList<string>> ListAsync(string storyId);
    Task<byte[]> ReadAsync(string storyId, string slot);
    Task WriteAsync(string storyId, string slot, byte[] data, bool overwrite = false);
}

public static class SaveNames
{
    public static void Validate(string storyId, string? slot = null)
    {
        if (string.IsNullOrWhiteSpace(storyId) || storyId.Length > 512)
        {
            throw new ArgumentException("A story save identity must contain 1-512 characters.", nameof(storyId));
        }

        if (slot is not null && (string.IsNullOrWhiteSpace(slot) || slot != slot.Trim() ||
            slot.Any(char.IsControl) || Encoding.UTF8.GetByteCount(slot) > 64))
        {
            throw new ArgumentException("A save name must contain 1-64 UTF-8 bytes, without control characters or surrounding spaces.", nameof(slot));
        }
    }

    public static void ValidateData(byte[] data)
    {
        if (data.Length is 0 or > StoryHistory.MaxSaveBytes)
        {
            throw new InvalidDataException("Save data must contain 1 byte to 2 MiB.");
        }
    }
}
