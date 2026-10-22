namespace Talewright.Runtime.Saves;

public interface ISaveStore
{
    Task<IReadOnlyList<string>> ListAsync(string storyId);
    Task<byte[]> ReadAsync(string storyId, string slot);
    Task WriteAsync(string storyId, string slot, byte[] data, bool overwrite = false);
}
