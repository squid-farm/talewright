using System.ComponentModel;
using System.Diagnostics;

namespace Talewright.Debugger;

/// <summary>The author guide section that explains each debugger tool.</summary>
public static class DebuggerGuide
{
    public const string Url =
        "https://github.com/squid-farm/talewright/blob/main/docs/guide/testing-your-story.md#3-jump-to-any-scene-with-the-author-debugger";

    /// <summary>Opens the guide in the default browser and returns an error message if that isn't possible.</summary>
    public static string? Open()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return $"Couldn't open a browser: {ex.Message}. Copy the address instead.";
        }
    }
}
