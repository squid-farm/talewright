using System.Text;

namespace Talewright.Runtime.Saves;

public static class SaveNames
{
    public static void Validate(string storyId, string? slot = null)
    {
        if (string.IsNullOrWhiteSpace(storyId) || storyId.Length > 512)
        {
            throw new ArgumentException("This story cannot use saved games because its identifier is missing or too long. Contact the story author.");
        }

        if (slot is not null && (string.IsNullOrWhiteSpace(slot) || slot != slot.Trim() ||
            slot.Any(char.IsControl) || Encoding.UTF8.GetByteCount(slot) > 64))
        {
            throw new ArgumentException("Enter a save name without leading or trailing spaces, line breaks or control characters. Use at most 64 basic letters, numbers or symbols; some characters need more space.");
        }
    }

    public static void ValidateData(byte[] data)
    {
        if (data.Length == 0)
        {
            throw new InvalidDataException("This saved game is empty. Choose another save or start a new game.");
        }

        if (data.Length > StoryHistory.MaxSaveBytes)
        {
            throw new InvalidDataException("This saved game is too large to store or load (maximum 2 MiB). Choose another save.");
        }
    }
}
