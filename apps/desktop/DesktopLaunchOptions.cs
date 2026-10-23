namespace Talewright.Host.Desktop;

internal sealed record DesktopLaunchOptions(IReadOnlyList<string> Paths, bool LibraryEnabled, bool ShowHelp)
{
    public const string Usage = "Usage: Talewright.Host.Desktop [--no-library] [--] [story-folder-or-zip ...]";

    public static DesktopLaunchOptions Parse(string[] args)
    {
        List<string> paths = [];
        var libraryEnabled = true;
        var showHelp = false;
        var pathsOnly = false;
        foreach (var argument in args)
        {
            if (pathsOnly || !argument.StartsWith('-'))
            {
                paths.Add(argument);
            }
            else
            {
                switch (argument)
                {
                    case "--":
                        pathsOnly = true;
                        break;
                    case "--no-library":
                        libraryEnabled = false;
                        break;
                    case "--help" or "-h":
                        showHelp = true;
                        break;
                    default:
                        throw new ArgumentException($"Unknown option '{argument}'. {Usage}");
                }
            }
        }
        if (!showHelp && !libraryEnabled && paths.Count != 1)
        {
            throw new ArgumentException("--no-library requires exactly one story folder or ZIP path.");
        }
        return new(paths, libraryEnabled, showHelp);
    }
}
