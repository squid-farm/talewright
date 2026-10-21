using Microsoft.Playwright;

namespace Talewright.Player.E2E.Tests;

public static class PlayerTarget
{
    public static string Name { get; } = Environment.GetEnvironmentVariable("TALEWRIGHT_TARGET") ?? "web";
    public static bool IsDesktop => Name == "linux";
}

public sealed record PlayerBounds(double X, double Y, double Width, double Height);

public sealed record PlayerObservation(
    string Passage,
    string Transcript,
    string Sheet,
    string Library,
    string[] Choices,
    bool Ended,
    bool PassageVisible,
    bool PassageFocused,
    bool PassageInWindow,
    bool SheetStacked,
    double WindowWidth);

public interface IPlayerDriver : IAsyncDisposable
{
    Task StartAsync(params string[] paths);
    Task UploadAsync(string name, byte[] content);
    Task OpenBundledAsync();
    Task ClickAsync(string name);
    Task ChooseAsync(string label);
    Task PressAsync(string key);
    Task ResizeAsync(int width, int height);
    Task<PlayerObservation> ObserveAsync();
}

public sealed class BrowserPlayerDriver(IPage page) : IPlayerDriver
{
    private ILocator Latest => page.GetByRole(AriaRole.Region, new() { Name = "Current passage" });

    public Task StartAsync(params string[] paths)
    {
        if (paths.Length != 0)
        {
            throw new NotSupportedException("Command-line story paths are a desktop-only workflow.");
        }

        return Task.CompletedTask;
    }

    public Task UploadAsync(string name, byte[] content) =>
        page.GetByLabel("Story archive").SetInputFilesAsync(new FilePayload
        {
            Name = name, MimeType = "application/zip", Buffer = content,
        });

    public Task OpenBundledAsync() => page.Locator(".tw-story-list").GetByRole(AriaRole.Button).First.ClickAsync();
    public Task ClickAsync(string name) => page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true }).ClickAsync();
    public Task ChooseAsync(string label) => page.GetByRole(AriaRole.List, new() { Name = "Story choices" })
        .GetByRole(AriaRole.Button).Filter(new()
        {
            HasTextRegex = new System.Text.RegularExpressions.Regex(@"^\s*\d+\.\s*" + System.Text.RegularExpressions.Regex.Escape(label) + @"\s*$"),
        }).ClickAsync();
    public Task PressAsync(string key) => page.Keyboard.PressAsync(key);
    public Task ResizeAsync(int width, int height) => page.SetViewportSizeAsync(width, height);

    public async Task<PlayerObservation> ObserveAsync()
    {
        var visible = await Latest.CountAsync() != 0;
        var transcript = page.GetByRole(AriaRole.Main, new() { Name = "Story transcript" });
        var sheet = page.GetByRole(AriaRole.Complementary, new() { Name = "Character sheet" });
        var library = page.Locator(".tw-library");
        var choices = page.GetByRole(AriaRole.List, new() { Name = "Story choices" }).GetByRole(AriaRole.Button);
        var passageBounds = visible ? await Latest.BoundingBoxAsync() : null;
        var transcriptBounds = visible ? await transcript.BoundingBoxAsync() : null;
        var sheetBounds = visible ? await sheet.BoundingBoxAsync() : null;
        return new(
            visible ? await Latest.InnerTextAsync() : "",
            visible ? await transcript.InnerTextAsync() : "",
            visible ? await sheet.InnerTextAsync() : "",
            await library.IsVisibleAsync() ? await library.InnerTextAsync() : "",
            visible ? (await choices.AllTextContentsAsync()).Select(text =>
                System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"^\d+\.\s*", "")).ToArray() : [],
            visible && await page.GetByRole(AriaRole.Button, new() { Name = "Play again", Exact = true }).IsVisibleAsync(),
            visible,
            visible && await Latest.EvaluateAsync<bool>("e => e === document.activeElement"),
            passageBounds is not null && passageBounds.Y >= 0 &&
                passageBounds.Y + passageBounds.Height <= page.ViewportSize!.Height,
            sheetBounds is not null && transcriptBounds is not null &&
                sheetBounds.Y >= transcriptBounds.Y + transcriptBounds.Height - 1,
            page.ViewportSize!.Width);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
