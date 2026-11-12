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
    double WindowWidth,
    string WindowTitle,
    bool TranscriptFullWidth = false);

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
    public async Task ChooseAsync(string label)
    {
        var previous = await page.GetByRole(AriaRole.Region, new() { Name = "Current passage" }).ElementHandleAsync()
            ?? throw new InvalidOperationException("Cannot choose without a current passage.");
        try
        {
            var previousIndex = await previous.GetAttributeAsync("data-tw-passage-index")
                ?? throw new InvalidOperationException("Current passage is missing its history index.");
            var choice = page.GetByRole(AriaRole.List, new() { Name = "Story choices" })
                .GetByRole(AriaRole.Button).Filter(new()
                {
                    HasTextRegex = new System.Text.RegularExpressions.Regex(@"^\s*\d+\.\s*" + System.Text.RegularExpressions.Regex.Escape(label) + @"\s*$"),
                });
            await WaitForChoiceMediaAsync(choice);
            await choice.ClickAsync();
            // A completed mouse click does not mean Blazor has rendered the next passage.
            await page.WaitForFunctionAsync("""
                previous => {
                    const current = document.querySelector('[aria-label="Current passage"]');
                    return current !== null &&
                        current.getAttribute('data-tw-passage-index') !== previous.index;
                }
                """, new { index = previousIndex });
        }
        finally
        {
            await previous.DisposeAsync();
        }
    }

    private async Task WaitForChoiceMediaAsync(ILocator choice)
    {
        const string settled = """
            media => {
                const image = media.querySelector('img');
                const failure = media.querySelector('.tw-media-error [role="status"]');
                return image !== null && (
                    (image.hidden && failure !== null && failure.checkVisibility()) ||
                    (image.currentSrc !== '' && image.complete && image.naturalWidth > 0));
            }
            """;
        var button = await choice.ElementHandleAsync()
            ?? throw new InvalidOperationException("Cannot find the requested story choice.");
        IReadOnlyList<IElementHandle> media = [];
        try
        {
            media = await page.Locator("[aria-label=\"Story transcript\"]:visible .tw-media").ElementHandlesAsync();
            foreach (var element in media)
            {
                if (!await element.EvaluateAsync<bool>("""
                    (media, button) => media.checkVisibility() &&
                        media.closest('[aria-label="Story transcript"]') === button.closest('[aria-label="Story transcript"]') &&
                        Boolean(media.compareDocumentPosition(button) & Node.DOCUMENT_POSITION_FOLLOWING)
                    """, button))
                {
                    continue;
                }

                if (await element.EvaluateAsync<bool>(settled)) continue;
                // Trigger viewport-lazy loading before waiting; no-src images report complete.
                await element.ScrollIntoViewIfNeededAsync();
                await page.WaitForFunctionAsync(settled, element);
            }
        }
        finally
        {
            foreach (var element in media) await element.DisposeAsync();
            await button.DisposeAsync();
        }
    }

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
                PassageInWindow: passageBounds != null && passageBounds.top >= 0 &&
                    passageBounds.bottom <= innerHeight,
                SheetStacked: sheetBounds != null && transcriptBounds != null &&
                    sheetBounds.top >= transcriptBounds.bottom - 1,
                TranscriptFullWidth: active && transcript !== null &&
                    getComputedStyle(transcript.parentElement).gridTemplateColumns.split(' ').length === 1,
                WindowWidth: innerWidth,
                WindowTitle: document.title
            };
        }
        """);
        return snapshot.Deserialize<PlayerObservation>()
            ?? throw new InvalidDataException("Missing browser observation.");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
