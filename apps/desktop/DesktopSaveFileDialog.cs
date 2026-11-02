using Photino.NET;

namespace Talewright.Host.Desktop;

internal static class DesktopSaveFileDialog
{
    internal static async Task<string?> ChooseAsync(PhotinoWindow window, string suggestedName)
    {
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return await window.ShowSaveFileAsync($"Export saved game as {suggestedName}", folder,
            [("JSON saved game", ["json"])]);
    }
}
