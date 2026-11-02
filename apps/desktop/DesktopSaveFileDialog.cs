using Photino.NET;
using Talewright.Player.Saves;

namespace Talewright.Host.Desktop;

internal static class DesktopSaveFileDialog
{
    // Test-only deviation: the native UI suite cannot reliably accept the GTK save dialog.
    // The test runner sets this directory; the real dialog is covered by the manual test plan.
    internal const string TestExportDirectoryVariable = "TALEWRIGHT_TEST_ONLY_SAVE_EXPORT_DIRECTORY";

    internal static Task<string?> ChooseAsync(PhotinoWindow window, string suggestedName)
    {
        if (Environment.GetEnvironmentVariable(TestExportDirectoryVariable) is { Length: > 0 } directory)
        {
            return Task.FromResult<string?>(Path.Combine(directory, suggestedName));
        }

        return NativeSaveFileDialog.ChooseAsync(suggestedName, action => window.Invoke(action),
            OperatingSystem.IsWindows() ? () => window.WindowHandle : null);
    }
}
