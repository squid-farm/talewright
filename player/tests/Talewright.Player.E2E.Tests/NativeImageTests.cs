using System.Diagnostics;

namespace Talewright.Player.E2E.Tests;

[Trait("Host", "Linux")]
[Collection(BrowserCollection.Name)]
public sealed class NativeImageTests
{
    [Fact]
    public async Task Native_built_in_sample_opens_real_embedded_image_bytes()
    {
        await using var driver = new NativePlayerDriver(Artifacts("embedded-images"));
        await driver.StartAsync();
        await driver.OpenBundledAsync();
        await driver.ClickAsync("Start story");
        const string alt = "A riverside expedition camp with canvas tents and supplies at the edge of dense jungle.";
        await driver.ScrollToImageAsync(alt);
        await WaitFor(driver, snapshot => snapshot.Descendants().Any(node =>
            node.Role == "image" && node.Name == alt && node.Bounds.Width > 100 && node.Bounds.Height > 100));
    }

    [Fact]
    public async Task Actual_zip_player_keeps_split_configuration_and_decodes_retained_images()
    {
        using var files = new ImageStoryFiles();
        var path = Path.Combine(files.Root, "illustrated.zip");
        await File.WriteAllBytesAsync(path, files.Zip("wrapped/"));
        await using var driver = new NativePlayerDriver(Artifacts("archive-images"));
        await driver.StartAsync(path);
        await driver.ScrollToImageAsync("A camp");
        await WaitFor(driver, snapshot => snapshot.Descendants().Any(node =>
            node.Role == "image" && node.Name == "A camp" && node.Bounds.Height > 100));
        await ActivateChoice(driver);
        await driver.ScrollToImageAsync("Next camp");
        var history = await WaitFor(driver, snapshot => snapshot.Descendants().Any(node =>
            node.Role == "image" && node.Name == "Next camp" && node.Bounds.Height > 100));
        Assert.Contains(history.Descendants(), node => node.Role == "image" && node.Name == "A camp");
        Assert.DoesNotContain("unavailable", history.Content);
    }

    [Fact]
    public async Task Actual_folder_player_renders_all_surfaces_formats_history_and_keyboard_choices()
    {
        using var files = new ImageStoryFiles();
        await using var driver = new NativePlayerDriver(Artifacts("folder-images"));
        await driver.StartAsync(files.Root);
        await WaitFor(driver, snapshot => snapshot.Content.Contains("Before"));
        foreach (var name in new[] { "A camp", "A pixel", "Strong camp", "Emphasized camp",
                     "Code pixel", "Conditional camp" })
        {
            await driver.ScrollToImageAsync(name);
            await WaitFor(driver, snapshot => snapshot.Descendants().Any(node =>
                node.Role == "image" && node.Name == name &&
                (name is "A pixel" or "Code pixel"
                    ? node.Bounds.Width is > 0 and <= 2 && node.Bounds.Height is > 0 and <= 2
                    : node.Bounds.Width > 100 && node.Bounds.Height > 100)));
        }
        var before = await driver.SnapshotAsync();
        Assert.Equal(2, before.Descendants().Count(node => node.Role == "image" && node.Name == "Repeated camp"));
        Assert.DoesNotContain("could not be", before.Content);
        var choice = before.Descendants().Single(node =>
            node.Role == "push button" && node.Name.StartsWith("1.", StringComparison.Ordinal));
        Assert.Contains("Choice camp", choice.Name);
        await driver.ScrollToButtonAsync(choice.Name);
        await WaitFor(driver, snapshot => snapshot.Descendants().Any(node =>
            node.Role == "push button" && node.Name == choice.Name && node.Bounds.Height > 100));
        await driver.ResizeAsync(520, 800);
        await driver.ScrollToButtonAsync(choice.Name);
        var narrow = await WaitFor(driver, snapshot => snapshot.Descendants().Any(node =>
            node.Role == "push button" && node.Name == choice.Name && node.Bounds.Width <= 520));
        Assert.DoesNotContain("unavailable", narrow.Content);
        await ActivateChoice(driver);
        await WaitFor(driver, snapshot => snapshot.Content.Contains("Next passage."));
        var history = await driver.SnapshotAsync();
        Assert.Contains(history.Descendants(), node => node.Role == "image" && node.Name == "A camp");
        Assert.Contains(history.Descendants(), node => node.Role == "image" && node.Name == "Choice camp");
        await driver.ScrollToImageAsync("Next camp");
        await WaitFor(driver, snapshot => snapshot.Descendants().Any(node =>
            node.Name == "Next camp" && node.Bounds.Height > 100));
        Assert.Equal(0, await driver.CloseWindowAsync());
    }

    [Fact]
    public async Task Actual_folder_player_reuses_live_image_until_final_release_then_reports_deleted_file()
    {
        using var files = new ImageStoryFiles();
        await File.WriteAllTextAsync(files.TalePath, """
            === start
            {image("photo.jpeg", "Mounted camp")}
            * [Next] -> next
            === next
            {image("photo.jpeg", "Later camp")}
            * [Finish] -> END
            """);
        await using var driver = new NativePlayerDriver(Artifacts("folder-image-failure"));
        await driver.StartAsync(files.Root);
        await WaitFor(driver, snapshot => snapshot.Descendants().Any(node =>
            node.Role == "image" && node.Name == "Mounted camp" && node.Bounds.Height > 100));
        File.Delete(files.ImagePath);
        await ActivateChoice(driver);
        var snapshot = await WaitFor(driver, snapshot => snapshot.Descendants().Any(node =>
            node.Role == "image" && node.Name == "Later camp" && node.Bounds.Height > 100));
        Assert.Contains(snapshot.Descendants(), node =>
            node.Role == "image" && node.Name == "Mounted camp" && node.Bounds.Height > 100);
        Assert.Contains(snapshot.Descendants(), node => node.Role == "image" && node.Name == "Later camp");
        Assert.DoesNotContain("An unhandled error", snapshot.Content);
        await driver.ClickAsync("More actions");
        await driver.ClickAsync("Restart");
        await driver.ClickAsync("Restart story");
        await WaitFor(driver, state => state.Content.Contains("This image could not be loaded."));
    }

    [Fact]
    public async Task Actual_folder_debugger_refreshes_images_and_preserves_rejected_source()
    {
        var executable = Environment.GetEnvironmentVariable("TALEWRIGHT_DEBUGGER_EXECUTABLE")
            ?? throw new InvalidOperationException("Set TALEWRIGHT_DEBUGGER_EXECUTABLE.");
        using var files = new ImageStoryFiles();
        const string tale = """
            === start
            {image("photo.jpeg", "Debugger camp")}
            * [Finish {image("pixel.png", "Debugger choice")}] -> END
            """;
        await File.WriteAllTextAsync(files.TalePath, tale);
        await using var driver = new NativePlayerDriver(Artifacts("debugger-images"), executable);
        await driver.StartAsync(files.Root);
        await WaitFor(driver, snapshot => snapshot.Content.Contains("Scene setup"));
        await driver.ClickAsync("Start selected scene");
        await driver.ScrollToImageAsync("Debugger camp");
        await WaitFor(driver, snapshot => snapshot.Descendants().Any(node =>
            node.Name == "Debugger camp" && node.Role == "image" && node.Bounds.Height > 100));
        await File.WriteAllTextAsync(files.TalePath, "=== start\n-> missing");
        await WaitFor(driver, snapshot => snapshot.Content.Contains("Source not adopted.")
            && snapshot.Descendants().Any(node => node.Name == "Debugger camp" && node.Bounds.Height > 100));
        await File.WriteAllTextAsync(files.TalePath, tale);
        await WaitFor(driver, snapshot => !snapshot.Content.Contains("Source not adopted."));
        await File.WriteAllBytesAsync(files.ImagePath, [1, 2, 3]);
        await driver.ScrollToImageAsync("Debugger camp");
        await WaitFor(driver, snapshot => snapshot.Content.Contains("This image could not be displayed."));
        using (var image = typeof(NativeImageTests).Assembly.GetManifestResourceStream("stories/LostRelic/images/camp-art.jpeg")!)
        await using (var target = File.Create(files.ImagePath))
            await image.CopyToAsync(target);
        await driver.ScrollToImageAsync("Debugger camp");
        await WaitFor(driver, snapshot => snapshot.Descendants().Any(node =>
            node.Name == "Debugger camp" && node.Role == "image" && node.Bounds.Height > 100)
            && !snapshot.Content.Contains("This image could not"));
        var choices = await driver.SnapshotAsync();
        Assert.Contains(choices.Descendants(), node =>
            node.Role == "push button" && node.Name.Contains("Debugger choice", StringComparison.Ordinal));
        await ActivateChoice(driver);
        await WaitFor(driver, snapshot => snapshot.Content.Contains("Play again"));
    }

    private static string Artifacts(string name) => Path.Combine(
        Environment.GetEnvironmentVariable("TALEWRIGHT_TEST_RESULTS") ?? Path.GetTempPath(),
        name, Guid.NewGuid().ToString("N"));

    private static async Task ActivateChoice(NativePlayerDriver driver)
    {
        var choice = (await driver.SnapshotAsync()).Descendants().Single(node =>
            node.Role == "push button" && node.Name.StartsWith("1.", StringComparison.Ordinal));
        await driver.ScrollToButtonAsync(choice.Name);
        await driver.ActivateAsync(choice.Name);
    }

    private static async Task<AccessibleNode> WaitFor(NativePlayerDriver driver, Func<AccessibleNode, bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var snapshot = await driver.SnapshotAsync();
            if (condition(snapshot)) return snapshot;
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(20), snapshot.Content);
            await Task.Delay(100);
        }
    }
}
