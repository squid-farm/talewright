using Talewright.Debugger;

namespace Talewright.Cli.Tests;

public sealed class WatcherFailureTests
{
    private const string InstanceLimit = "The configured user limit (128) on the number of inotify instances has been reached, "
        + "or the per-process limit on the number of open file descriptors has been reached.";
    private const string WatchLimit = "The configured user limit (8192) on the number of inotify watches has been reached, "
        + "or the operating system failed to allocate a required resource.";

    [Theory]
    [InlineData(InstanceLimit, 24)]
    [InlineData(WatchLimit, 28)]
    [InlineData("The system limit on the number of inotify instances has been reached.", 23)]
    public void Linux_watcher_limits_get_author_wording_and_keep_the_original_detail(string message, int errno)
    {
        var model = new WorkbenchModel(Path.GetTempPath());

        model.WatchFailed(new IOException(message, errno));

        Assert.NotNull(model.Error);
        Assert.DoesNotContain("Source watcher failed", model.Error);
        Assert.Contains("can't watch your story folder", model.Error);
        Assert.Contains("Automatic reload is paused", model.Error);
        Assert.Contains("current playtest is unchanged", model.Error);
        Assert.Contains("Close other editor windows", model.Error);
        Assert.Contains("Rescan / retry watcher", model.Error);
        Assert.Contains("Restart from source", model.Error);
        Assert.Contains("fs.inotify.max_user_instances", model.Error);
        Assert.Contains("fs.inotify.max_user_watches", model.Error);
        Assert.EndsWith($"Details: {message}", model.Error);
        Assert.True(model.RestartRequired);
    }

    [Fact]
    public void Limit_is_recognized_by_errno_when_the_message_is_not_the_known_text()
    {
        Assert.True(WatcherFailure.IsLimit(new IOException("Too many open files", 24)));
        Assert.True(WatcherFailure.IsLimit(new IOException("localized text", 28)));
    }

    [Theory]
    [InlineData("Access to the path is denied.")]
    [InlineData("The directory name /story is invalid.")]
    public void Other_watcher_failures_keep_the_generic_message(string message)
    {
        var model = new WorkbenchModel(Path.GetTempPath());

        model.WatchFailed(new IOException(message));

        Assert.Equal($"Source watcher failed: {message}. Select Rescan / retry watcher. The last good run is unchanged.", model.Error);
        Assert.False(WatcherFailure.IsLimit(new ArgumentException(message)));
        Assert.False(WatcherFailure.IsLimit(new UnauthorizedAccessException(message)));
    }
}
