using System.Runtime.InteropServices;

namespace Talewright.Player.Saves;

internal static class GtkSaveFileDialog
{
    private const int Save = 1;
    private const int Accept = -3;
    private const int DeleteEvent = -4;
    private const int Cancel = -6;
    private const int Close = -7;

    internal static string? Choose(string suggestedName)
    {
        var dialog = gtk_file_chooser_dialog_new("Export saved game", nint.Zero, Save, nint.Zero);
        if (dialog == nint.Zero)
        {
            throw new InvalidOperationException("The desktop save dialog could not be created.");
        }

        try
        {
            gtk_dialog_add_button(dialog, "_Cancel", Cancel);
            gtk_dialog_add_button(dialog, "_Save", Accept);
            gtk_file_chooser_set_do_overwrite_confirmation(dialog, 1);
            var folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                gtk_file_chooser_set_current_folder(dialog, folder);
            }
            gtk_file_chooser_set_current_name(dialog, suggestedName);

            var filter = gtk_file_filter_new();
            if (filter == nint.Zero)
            {
                throw new InvalidOperationException("The desktop save file filter could not be created.");
            }
            gtk_file_filter_set_name(filter, "JSON saved game");
            gtk_file_filter_add_pattern(filter, "*.json");
            gtk_file_chooser_add_filter(dialog, filter);

            var response = gtk_dialog_run(dialog);
            if (response is Cancel or DeleteEvent or Close)
            {
                return null;
            }
            if (response != Accept)
            {
                throw new InvalidOperationException($"The desktop save dialog returned an unexpected response ({response}).");
            }

            var path = gtk_file_chooser_get_filename(dialog);
            if (path == nint.Zero)
            {
                throw new InvalidOperationException("The desktop save dialog did not return a filename.");
            }
            try
            {
                return Marshal.PtrToStringUTF8(path)
                    ?? throw new InvalidOperationException("The desktop save dialog returned an invalid filename.");
            }
            finally
            {
                g_free(path);
            }
        }
        finally
        {
            gtk_widget_destroy(dialog);
        }
    }

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint gtk_file_chooser_dialog_new(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string title, nint parent, int action, nint firstButton);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint gtk_dialog_add_button(nint dialog, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, int response);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_file_chooser_set_do_overwrite_confirmation(nint dialog, int enabled);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern int gtk_file_chooser_set_current_folder(nint dialog, [MarshalAs(UnmanagedType.LPUTF8Str)] string folder);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_file_chooser_set_current_name(nint dialog, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint gtk_file_filter_new();

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_file_filter_set_name(nint filter, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_file_filter_add_pattern(nint filter, [MarshalAs(UnmanagedType.LPUTF8Str)] string pattern);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_file_chooser_add_filter(nint dialog, nint filter);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern int gtk_dialog_run(nint dialog);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint gtk_file_chooser_get_filename(nint dialog);

    [DllImport("libgtk-3.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void gtk_widget_destroy(nint dialog);

    [DllImport("libglib-2.0.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void g_free(nint memory);
}
