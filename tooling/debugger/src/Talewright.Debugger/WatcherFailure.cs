namespace Talewright.Debugger;

/// <summary>Turns source-watcher exceptions into author-facing text.</summary>
public static class WatcherFailure
{
    // .NET raises Linux watcher limits as IOException with the errno as HResult.
    private const int TooManyOpenFilesInSystem = 23; // ENFILE
    private const int TooManyOpenFiles = 24; // EMFILE
    private const int NoWatchSpace = 28; // ENOSPC

    public static bool IsLimit(Exception exception) =>
        exception is IOException io
        && (io.HResult is TooManyOpenFilesInSystem or TooManyOpenFiles or NoWatchSpace
            || io.Message.Contains("inotify", StringComparison.OrdinalIgnoreCase)
            || io.Message.Contains("too many open files", StringComparison.OrdinalIgnoreCase));

    public static string Describe(Exception exception)
    {
        if (!IsLimit(exception))
        {
            return $"Source watcher failed: {exception.Message}. Select Rescan / retry watcher. The last good run is unchanged.";
        }
        var linux = OperatingSystem.IsLinux() || exception.Message.Contains("inotify", StringComparison.OrdinalIgnoreCase)
            ? " On Linux these limits are the fs.inotify.max_user_instances and fs.inotify.max_user_watches system settings."
            : "";
        return "The debugger can't watch your story folder for changes: your system's limit on watched files or open files "
            + "has been reached, usually because other editors or tools are watching files too. "
            + "Automatic reload is paused. Your current playtest is unchanged. "
            + "Close other editor windows or file-watching tools, then select Rescan / retry watcher, "
            + "or Restart from source to also start a fresh run with your edits."
            + linux
            + $" Details: {exception.Message}";
    }
}
