namespace Talewright.Debugger;

public sealed record MapPngExportSize(bool Allowed, int Width, int Height, string Message);

internal static class MapExportLimits
{
    internal const int Padding = 40;
    internal const int MaxPngDimension = 8192;
    internal const long MaxPngPixels = 16_777_216;

    internal static MapPngExportSize CheckPngSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return Rejected("PNG export dimensions must be positive. Use SVG export instead.");
        }

        if (width > MaxPngDimension || height > MaxPngDimension || (long)width * height > MaxPngPixels)
        {
            return Rejected(
                $"PNG export is limited to {MaxPngDimension:N0} pixels per side and {MaxPngPixels:N0} total pixels. " +
                "Use SVG export for a full-resolution image of this map.");
        }

        return new MapPngExportSize(true, width, height, "");
    }

    private static MapPngExportSize Rejected(string message) => new(false, 0, 0, message);
}
