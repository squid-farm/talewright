using System.ComponentModel;
using Spectre.Console.Cli;

namespace Talewright.Cli;

public sealed class DebugSettings : CliSettings
{
    [CommandArgument(0, "<story-root>")]
    public string Root { get; set; } = "";
}

public sealed class DebugCommand : Command<DebugSettings>
{
    public override int Execute(CommandContext context, DebugSettings settings, CancellationToken cancellationToken)
    {
        var writers = (CommandWriters)context.Data!;
        try
        {
            var root = Path.GetFullPath(settings.Root);
            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException($"Story folder '{root}' does not exist.");
            }
            return writers.NativeLaunch.Launch(NativeHostMode.Debugger, root, writers.Output, writers.Error,
                cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or Win32Exception)
        {
            return CommandLine.Failure("TWCLI002", $"Debugger launch failed: {ex.Message}", false, writers.Output, writers.Error);
        }
    }
}
