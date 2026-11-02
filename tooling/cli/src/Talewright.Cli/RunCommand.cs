using System.ComponentModel;
using Spectre.Console.Cli;

namespace Talewright.Cli;

public sealed class RunSettings : CliSettings
{
    [CommandArgument(0, "[story-folder]")]
    public string Root { get; set; } = ".";
}

public sealed class RunCommand : Command<RunSettings>
{
    public override int Execute(CommandContext context, RunSettings settings, CancellationToken cancellationToken)
    {
        var writers = (CommandWriters)context.Data!;
        var validation = new ValidateCommand().Execute(context, new ValidationSettings { Root = settings.Root },
            cancellationToken);
        if (validation != 0)
        {
            return validation;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return writers.NativeLaunch.Launch(NativeHostMode.Player, Path.GetFullPath(settings.Root),
                writers.Output, writers.Error, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or Win32Exception)
        {
            return CommandLine.Failure("TWCLI002", $"Player launch failed: {ex.Message}", false,
                writers.Output, writers.Error);
        }
    }
}
