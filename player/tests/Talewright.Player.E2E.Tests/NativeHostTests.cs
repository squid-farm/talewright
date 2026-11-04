using Talewright.Testing;
using Xunit.Abstractions;

namespace Talewright.Player.E2E.Tests;

[Trait("Host", "Linux")]
public sealed class NativeHostTests(WebHostFixture host, ITestOutputHelper output) : PlayerTest(host, output)
{
    private readonly string _fixtures = Path.Combine(Path.GetTempPath(), "talewright-native-" + Guid.NewGuid().ToString("N"));
    protected override bool AutoStartDesktop => false;

    public static IEnumerable<object[]> InvalidPaths =>
        TestArchives.Errors.Where(pair => pair.Key is "zip" or "manifest" or "yaml" or "tale")
            .Select(pair => new object[] { pair.Key, pair.Key == "zip" ? "Couldn't open" : pair.Value });

    [Fact]
    [Trait("Category", "Probe")]
    public async Task Accessibility_probe_opens_bundled_and_uploaded_stories()
    {
        await Driver.StartAsync();
        await Driver.OpenBundledAsync();
        await Driver.ClickAsync("Start story");
        await AssertState(state =>
        {
            Assert.True(state.PassageVisible);
            Assert.NotEmpty(state.Sheet);
        });
        await Library();
        await Upload(TestStories.Linear);
        await Choose("Walk onward");
        await Observe(new(null, "Second passage text."));
        await AssertState(state => Assert.True(state.PassageFocused));
        await Library();
        await Upload(TestStories.Custom, "A story with spaces/");
        await Observe(new(null, "Alex's quiet story."));
        await Choose("Make a friend");
        await Observe(new(null, "Friendship grows.", Ended: true));
        await Driver.ClickAsync("Play again");
        await Observe(new(null, "Alex's quiet story.", Sheet: ["3"]));
    }

    [Fact]
    public async Task Settings_dialog_exposes_focused_theme_control_on_desktop()
    {
        await Driver.StartAsync();
        await Driver.OpenBundledAsync();
        await Driver.ClickAsync("Start story");
        await Driver.ClickAsync("More actions");
        await Driver.ClickAsync("Settings");

        var native = Assert.IsType<NativePlayerDriver>(Driver);
        var snapshot = await native.SnapshotAsync();
        var dialog = Assert.Single(snapshot.Descendants(), node => node.Role == "dialog" && node.Name == "Settings");
        var theme = Assert.Single(dialog.Descendants(), node => node.Role == "combo box" && node.Name == "Theme");
        Assert.True(theme.Focused);
        Assert.Contains(dialog.Descendants(), node => node.Role == "push button" && node.Name == "Close settings");

        await Driver.PressAsync("Escape");
        await AssertState(state => Assert.True(state.PassageVisible));
    }

    [Fact]
    public async Task Maximum_text_size_reflows_the_desktop_player_and_keeps_toolbar_controls_visible()
    {
        await Driver.StartAsync();
        await Driver.OpenBundledAsync();
        await Driver.ClickAsync("Start story");
        var native = Assert.IsType<NativePlayerDriver>(Driver);
        var before = await native.SnapshotAsync();
        var passageBefore = Assert.Single(before.Descendants(), node => node.Name == "Current passage");

        await Driver.ClickAsync("More actions");
        await Driver.ClickAsync("Settings");
        var dialog = Assert.Single((await native.SnapshotAsync()).Descendants(),
            node => node.Role == "dialog" && node.Name == "Settings");
        var textSize = Assert.Single(dialog.Descendants(),
            node => node.Role == "combo box" && node.Name == "Text size");
        Assert.False(textSize.Focused);
        await Driver.PressAsync("Tab");
        Assert.True(Assert.Single((await native.SnapshotAsync()).Descendants(),
            node => node.Role == "combo box" && node.Name == "Text size").Focused);
        await Driver.PressAsync("End");
        await Driver.ClickAsync("Close settings");

        var after = await native.SnapshotAsync();
        var passageAfter = Assert.Single(after.Descendants(), node => node.Name == "Current passage");
        Assert.True(passageAfter.Bounds.Height > passageBefore.Bounds.Height,
            "The rendered passage should grow when text size increases.");

        await Driver.ResizeAsync(640, 760);
        Assert.True((await Driver.ObserveAsync()).SheetStacked);
        var resized = await native.SnapshotAsync();
        var window = Assert.Single(resized.Descendants(), node => node.Role == "frame");
        foreach (var name in new[] { "Back", "Forward", "Quick Save", "Saves", "More actions" })
        {
            var control = Assert.Single(resized.Descendants(),
                node => node.Role == "push button" && node.Name == name);
            Assert.True(control.Bounds.X >= window.Bounds.X
                && control.Bounds.X + control.Bounds.Width <= window.Bounds.X + window.Bounds.Width,
                $"{name} remains within the desktop window at maximum text size.");
        }
    }

    [Fact]
    public async Task Folder_and_multiple_cli_paths_are_available_in_library()
    {
        var directory = Path.Combine(_fixtures, "linear");
        TestStories.Linear.WriteDirectory(directory);
        var zip = await WriteArchive("custom.zip", TestStories.Custom.Zip());
        await Driver.StartAsync(directory, zip);
        await Observe(new(null, "First passage"));
        await Library();
        await AssertState(state => Assert.Contains($"Player Test {directory}", state.Library));
        await Driver.ClickAsync($"Custom Player {zip}");
        await Observe(new(null, "Alex's quiet story."));
    }

    [Fact]
    public async Task Explicit_no_library_flag_hides_navigation_during_creation_and_play()
    {
        var fixture = TestStories.Linear with
        {
            Yaml = TestStories.DefaultManifest(creationName: true).Build(),
        };
        var path = await WriteArchive("creation.zip", fixture.Zip());
        await Driver.StartAsync("--no-library", path);
        await AssertLibraryUnavailable(duringCreation: true);
        await Driver.ClickAsync("Start story");
        await Observe(new(null, "First passage"));
        await AssertLibraryUnavailable();
        await Choose("Walk onward");
        await Restart();
        await Observe(new(null, "First passage"));
        await AssertLibraryUnavailable();
    }

    [Fact]
    public async Task No_library_startup_error_can_retry_the_same_story()
    {
        var path = await WriteArchive("repair.zip", TestArchives.Invalid("zip"));
        await Driver.StartAsync("--no-library", path);
        await AssertState(state => Assert.Contains("Couldn't open", state.Library));
        await AssertLibraryUnavailable();
        await File.WriteAllBytesAsync(path, TestStories.Linear.Zip());
        await Driver.ClickAsync("Retry opening story");
        await Observe(new(null, "First passage"));
        await AssertLibraryUnavailable();
    }

    [Fact]
    public async Task Unicode_and_spaces_in_cli_path_are_supported()
    {
        var path = await WriteArchive("histoire caf\u00e9.zip", TestStories.Linear.Zip());
        await Driver.StartAsync(path);
        await Observe(new(null, "First passage"));
    }

    [Theory]
    [MemberData(nameof(InvalidPaths))]
    public async Task Invalid_cli_archive_reports_error_and_allows_recovery(string kind, string expected)
    {
        var path = await WriteArchive(kind + ".zip", TestArchives.Invalid(kind));
        await Driver.StartAsync(path);
        await AssertState(state => Assert.Contains(expected, state.Library));
        await Driver.OpenBundledAsync();
        await Driver.ClickAsync("Start story");
        await AssertState(state => Assert.True(state.PassageVisible));
    }

    [Fact]
    public async Task Missing_cli_path_reports_error_and_allows_recovery()
    {
        await Driver.StartAsync(Path.Combine(_fixtures, "does not exist.zip"));
        await AssertState(state => Assert.Contains("Couldn't open", state.Library));
        await Driver.OpenBundledAsync();
        await Driver.ClickAsync("Start story");
        await AssertState(state => Assert.True(state.PassageVisible));
    }

    private async Task<string> WriteArchive(string name, byte[] content)
    {
        Directory.CreateDirectory(_fixtures);
        var path = Path.Combine(_fixtures, name);
        await File.WriteAllBytesAsync(path, content);
        return path;
    }

    public override async Task DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(_fixtures))
            {
                Directory.Delete(_fixtures, recursive: true);
            }
        }
    }
}
