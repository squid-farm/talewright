using System.Text;
using Talewright.Talemark.Compilation;

namespace Talewright.Testing;

public static class PerformanceSource
{
    /// <summary>Counts UTF-8 bytes of every supplied source text, including the manifest but not paths or images.</summary>
    public static long Utf8ByteCount(StorySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        long bytes = 0;
        foreach (var text in source.Files.Values)
        {
            bytes = checked(bytes + Encoding.UTF8.GetByteCount(text));
        }
        return bytes;
    }
}
