using Talewright.Host.Desktop;

namespace Talewright.Cli.Tests;

public sealed class DesktopLaunchOptionsTests
{
    [Fact]
    public void Library_is_enabled_by_default_with_or_without_story_paths()
    {
        Assert.True(DesktopLaunchOptions.Parse([]).LibraryEnabled);
        var options = DesktopLaunchOptions.Parse(["one", "two.zip"]);
        Assert.True(options.LibraryEnabled);
        Assert.Equal(["one", "two.zip"], options.Paths);
    }

    [Theory]
    [InlineData("--no-library", "story.zip")]
    [InlineData("story.zip", "--no-library")]
    public void Explicit_flag_disables_library_for_one_path(params string[] args)
    {
        var options = DesktopLaunchOptions.Parse(args);
        Assert.False(options.LibraryEnabled);
        Assert.Equal(["story.zip"], options.Paths);
    }

    [Theory]
    [InlineData("--no-library")]
    [InlineData("--no-library", "one", "two")]
    [InlineData("--typo", "one")]
    public void Invalid_arguments_are_reported(params string[] args) =>
        Assert.Throws<ArgumentException>(() => DesktopLaunchOptions.Parse(args));

    [Fact]
    public void Separator_preserves_paths_starting_with_a_dash()
    {
        var options = DesktopLaunchOptions.Parse(["--no-library", "--", "-story.zip"]);
        Assert.Equal(["-story.zip"], options.Paths);
        Assert.False(options.LibraryEnabled);
    }

    [Fact]
    public void Help_does_not_require_a_story() =>
        Assert.True(DesktopLaunchOptions.Parse(["--no-library", "--help"]).ShowHelp);
}
