using System.Text.Json;
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

    // One synchronous DOM read prevents observations from straddling a Blazor render.
    public async Task<PlayerObservation> ObserveAsync()
    {
        var snapshot = await page.EvaluateAsync<JsonElement>("""
        () => {
            const passage = document.querySelector('[aria-label="Current passage"]');
            const transcript = document.querySelector('[aria-label="Story transcript"]');
            const sheet = document.querySelector('[aria-label="Character sheet"]');
            const library = document.querySelector('.tw-library');
            const displayed = element => element !== null && element.checkVisibility();
            const active = displayed(transcript);
            const passageBounds = active ? passage?.getBoundingClientRect() : null;
            const transcriptBounds = active ? transcript.getBoundingClientRect() : null;
            const sheetBounds = active ? sheet?.getBoundingClientRect() : null;
            return {
                Passage: active ? passage?.innerText ?? '' : '',
                Transcript: active ? transcript.innerText : '',
                Sheet: active ? sheet?.innerText ?? '' : '',
                Library: displayed(library) ? library.innerText : '',
                Choices: active ? Array.from(document.querySelectorAll('[aria-label="Story choices"] button'),
                    button => button.textContent.trim().replace(/^\d+\.\s*/, '')) : [],
                Ended: active && Array.from(transcript.querySelectorAll('button'))
                    .some(button => button.textContent.trim() === 'Play again' && displayed(button)),
                PassageVisible: active && passage !== null,
                PassageFocused: active && passage !== null && passage === document.activeElement,
                PassageInWindow: passageBounds !== null && passageBounds.top >= 0 &&
                    passageBounds.bottom <= innerHeight,
                SheetStacked: sheetBounds !== null && transcriptBounds !== null &&
                    sheetBounds.top >= transcriptBounds.bottom - 1,
                WindowWidth: innerWidth
            };
        }
        """);
        return snapshot.Deserialize<PlayerObservation>()
            ?? throw new InvalidDataException("Missing browser observation.");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
