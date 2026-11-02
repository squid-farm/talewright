using System.Runtime.InteropServices;

namespace Talewright.Player.Saves;

internal static class NfdSaveFileDialog
{
    private const int Error = 0;
    private const int Okay = 1;
    private const int Cancel = 2;
    private const nuint InterfaceVersion = 1;
    private const nuint WindowsHandle = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Filter
    {
        public nint Name;
        public nint Extensions;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Options
    {
        public nint Filters;
        public uint FilterCount;
        public nint DefaultFolder;
        public nint DefaultName;
        public nuint ParentType;
        public nint ParentHandle;
    }

    internal static string? Choose(string suggestedName, nint owner)
    {
        if (NFD_Init() != Okay)
        {
            throw Failure("initialize");
        }

        nint filterName = nint.Zero, extensions = nint.Zero, filter = nint.Zero;
        nint folder = nint.Zero, name = nint.Zero;
        try
        {
            filterName = Marshal.StringToCoTaskMemUTF8("JSON saved game");
            extensions = Marshal.StringToCoTaskMemUTF8("json");
            filter = Marshal.AllocHGlobal(Marshal.SizeOf<Filter>());
            Marshal.StructureToPtr(new Filter { Name = filterName, Extensions = extensions }, filter, false);
            var defaultFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(defaultFolder) && Directory.Exists(defaultFolder))
            {
                folder = Marshal.StringToCoTaskMemUTF8(defaultFolder);
            }
            name = Marshal.StringToCoTaskMemUTF8(suggestedName);
            var options = new Options
            {
                Filters = filter,
                FilterCount = 1,
                DefaultFolder = folder,
                DefaultName = name,
                ParentType = owner == nint.Zero ? 0 : WindowsHandle,
                ParentHandle = owner,
            };

            var result = NFD_SaveDialogU8_With_Impl(InterfaceVersion, out var path, in options);
            if (result == Cancel)
            {
                return null;
            }
            if (result != Okay)
            {
                throw result == Error ? Failure("open") :
                    new InvalidOperationException($"The desktop save dialog returned an unexpected response ({result}).");
            }
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
                NFD_FreePathU8(path);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(filterName);
            Marshal.FreeCoTaskMem(extensions);
            Marshal.FreeHGlobal(filter);
            Marshal.FreeCoTaskMem(folder);
            Marshal.FreeCoTaskMem(name);
            NFD_Quit();
        }
    }

    private static InvalidOperationException Failure(string operation)
    {
        var detail = Marshal.PtrToStringUTF8(NFD_GetError()) ?? "No further details are available.";
        return new InvalidOperationException($"Could not {operation} the desktop save dialog: {detail}");
    }

    [DllImport("nfd", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NFD_Init();

    [DllImport("nfd", CallingConvention = CallingConvention.Cdecl)]
    private static extern void NFD_Quit();

    [DllImport("nfd", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint NFD_GetError();

    [DllImport("nfd", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NFD_SaveDialogU8_With_Impl(nuint version, out nint path, in Options options);

    [DllImport("nfd", CallingConvention = CallingConvention.Cdecl)]
    private static extern void NFD_FreePathU8(nint path);
}
