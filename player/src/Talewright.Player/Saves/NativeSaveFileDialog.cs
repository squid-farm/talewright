namespace Talewright.Player.Saves;

/// <summary>Shows a native desktop save dialog with a suggested filename.</summary>
public static class NativeSaveFileDialog
{
    /// <param name="suggestedName">The initial filename, including its extension.</param>
    /// <param name="dispatch">Schedules work on the desktop window's UI thread.</param>
    /// <param name="getWindowsOwner">Returns the window handle on Windows.</param>
    public static Task<string?> ChooseAsync(string suggestedName, Action<Action> dispatch, Func<nint>? getWindowsOwner = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(suggestedName);
        ArgumentNullException.ThrowIfNull(dispatch);

        return DispatchAsync(suggestedName, dispatch, name =>
        {
            if (OperatingSystem.IsLinux())
            {
                return GtkSaveFileDialog.Choose(name);
            }
            if (OperatingSystem.IsWindows())
            {
                return NfdSaveFileDialog.Choose(name, getWindowsOwner?.Invoke() ?? nint.Zero);
            }
            if (OperatingSystem.IsMacOS())
            {
                return NfdSaveFileDialog.Choose(name, nint.Zero);
            }
            throw new PlatformNotSupportedException("The native save dialog is not supported on this platform.");
        });
    }

    internal static Task<string?> DispatchAsync(string suggestedName, Action<Action> dispatch, Func<string, string?> choose)
    {
        return Task.Run(() =>
        {
            var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            dispatch(() =>
            {
                try
                {
                    completion.TrySetResult(choose(suggestedName));
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });
            return completion.Task;
        });
    }
}
