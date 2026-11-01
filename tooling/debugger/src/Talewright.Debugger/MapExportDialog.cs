using Photino.NET;

namespace Talewright.Debugger;

internal sealed class MapExportDialog
{
    // DEVIATION FROM OUR E2E STANDARD (drive the real UI, no test hooks in shipped code).
    // Photino runs an in-process GtkFileChooserDialog that has no portal to answer, and
    // UI automation (AT-SPI here, Playwright in the browser suites) cannot reliably accept
    // a system save dialog. Only player/tests/run_e2e.py sets this variable; when set, exports
    // skip the dialog and save to story-map.<ext> in that directory. The real dialog is
    // checked manually in docs/guide/debugger-test-plan.md. Do not copy this pattern.
    internal const string TestExportDirectoryVariable = "TALEWRIGHT_TEST_ONLY_MAP_EXPORT_DIRECTORY";

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
        if (Environment.GetEnvironmentVariable(TestExportDirectoryVariable) is { Length: > 0 } testDirectory)
        {
            return Path.Combine(testDirectory, $"story-map.{extension}");
        }

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
