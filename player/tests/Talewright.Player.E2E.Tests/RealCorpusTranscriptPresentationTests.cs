using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Talewright.Player.E2E.Tests;

[Trait("Host", "Browser")]
public sealed class RealCorpusTranscriptPresentationTests : PlayerTest
{
    private readonly WebHostFixture _host;
    private readonly ITestOutputHelper _output;

    public RealCorpusTranscriptPresentationTests(WebHostFixture host, ITestOutputHelper output) : base(host, output)
    {
        _host = host;
        _output = output;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Report_mounted_passages_and_distinct_image_lifetimes(bool illustrated)
    {
        await Page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
        await Page.EvaluateAsync("""
            () => {
                window.transcriptUrls = { created: 0, revoked: 0 };
                const create = URL.createObjectURL.bind(URL), revoke = URL.revokeObjectURL.bind(URL);
                URL.createObjectURL = blob => { window.transcriptUrls.created++; return create(blob); };
                URL.revokeObjectURL = url => { window.transcriptUrls.revoked++; revoke(url); };
            }
            """);
        using var files = new ImageStoryFiles();
        if (illustrated)
        {
            var resized = await Page.EvaluateAsync<string>("""
                async base64 => {
                    const image = new Image();
                    image.src = 'data:image/jpeg;base64,' + base64;
                    await image.decode();
                    const canvas = document.createElement('canvas');
                    canvas.width = 320; canvas.height = 180;
                    canvas.getContext('2d').drawImage(image, 0, 0, 320, 180);
                    return canvas.toDataURL('image/jpeg', 0.8).split(',')[1];
                }
                """, Convert.ToBase64String(File.ReadAllBytes(files.ImagePath)));
            File.WriteAllBytes(files.ImagePath, Convert.FromBase64String(resized));
        }
        var tale = new StringBuilder();
        for (var index = 0; index <= 120; index++)
        {
            tale.AppendLine($"=== passage{index}");
            tale.AppendLine($"Passage number {index}.");
            for (var paragraph = 0; paragraph < 4; paragraph++)
                tale.AppendLine("\n" + string.Join(" ", Enumerable.Repeat(
                    "The expedition follows the river past trees and stones, recording the changing trail.", 8)));
            if (illustrated)
            {
                File.Copy(files.ImagePath, Path.Combine(files.Root, "images", $"camp-{index}.jpeg"));
                tale.AppendLine($"\n{{image(\"camp-{index}.jpeg\", \"Camp {index}\")}}");
            }
            tale.AppendLine(index == 120 ? "\n-> END" : $"\n* [Next] -> passage{index + 1}");
        }
        File.WriteAllText(files.TalePath, tale.ToString());
        var manifest = Path.Combine(files.Root, "story.talewright.yaml");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("start: start", "start: passage0"));
        await UploadBytes("transcript-measurement.zip", files.Zip());
        var measurements = new List<object>();
        for (var index = 0; index <= 120; index++)
        {
            await Expect(Latest).ToContainTextAsync($"Passage number {index}.");
            if (illustrated)
            {
                var image = Latest.GetByAltText($"Camp {index}", new() { Exact = true });
                await image.ScrollIntoViewIfNeededAsync();
                await Page.WaitForFunctionAsync("image => image.complete && image.naturalWidth === 320",
                    await image.ElementHandleAsync());
            }
            if (index == 120) break;
            var start = Stopwatch.GetTimestamp();
            await Choices.First.ClickAsync();
            await Expect(Latest).ToContainTextAsync($"Passage number {index + 1}.");
            await Expect(Page.Locator(".tw-frame")).ToHaveCountAsync(Math.Min(index + 2, 21));
            if (index is 19 or 49 or 99 or 119)
                measurements.Add(new
                {
                    passages = index + 2,
                    mounted = await Page.Locator(".tw-frame").CountAsync(),
                    urls = await Page.EvaluateAsync<JsonElement>("() => window.transcriptUrls"),
                    choiceToDomMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                });
        }
        await Page.WaitForFunctionAsync("() => window.transcriptUrls.created - window.transcriptUrls.revoked <= 21");
        _output.WriteLine(JsonSerializer.Serialize(new
        {
            illustrated, browser = _host.BrowserName, version = _host.Browser.Version, measurements,
            final = new
            {
                mounted = await Page.Locator(".tw-frame").CountAsync(),
                urls = await Page.EvaluateAsync<JsonElement>("() => window.transcriptUrls"),
            },
            scope = "120 choices; four paragraphs per passage; distinct paths with real camp artwork resized to 320x180 JPEGs when illustrated; " +
                "reduced motion, standard harness tracing; lifecycle counts are not retained-memory measurements; " +
                "single-run choice-to-DOM samples are not percentiles",
        }));
    }
}
