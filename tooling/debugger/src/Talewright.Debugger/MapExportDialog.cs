using Photino.NET;

namespace Talewright.Debugger;

internal sealed class MapExportDialog
{
    private PhotinoWindow? _window;

    internal void Attach(PhotinoWindow window) =>
        _window = window ?? throw new ArgumentNullException(nameof(window));

    internal async Task<string?> ChoosePathAsync(string format)
    {
        var (label, extension) = format switch
        {
            "svg" => ("SVG image", "svg"),
            "png" => ("PNG image", "png"),
            _ => throw new ArgumentException("Map exports must use SVG or PNG format.", nameof(format)),
        };
        var initialPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var selected = await (_window
            ?? throw new InvalidOperationException("The map export dialog is not attached to the debugger window."))
            .ShowSaveFileAsync($"Export story map as {extension.ToUpperInvariant()}",
                initialPath, [(label, [extension])]);
        if (string.IsNullOrWhiteSpace(selected))
        {
            return null;
        }

        return string.Equals(Path.GetExtension(selected), $".{extension}", StringComparison.OrdinalIgnoreCase)
            ? selected
            : Path.ChangeExtension(selected, extension);
    }
}
