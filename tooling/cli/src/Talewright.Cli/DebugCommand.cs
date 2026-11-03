using System.ComponentModel;
using Spectre.Console.Cli;
using Talewright.Talemark.Compilation;

namespace Talewright.Cli;

public sealed class DebugSettings : CliSettings
{
    [CommandArgument(0, "<story-root>")]
    public string Root { get; set; } = "";

    [CommandOption("--scene <ID>")]
    [Description("Preselect a scene in the Author Debugger without starting it.")]
    public string? Scene { get; set; }
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
            if (settings.Scene is { } requestedScene)
            {
                var compilation = StoryCompiler.Compile(StorySource.FromDirectory(root));
                if (compilation.Story is { } story &&
                    !story.Scenes.Any(scene => scene.Id.Value == requestedScene))
                {
                    return CommandLine.Failure(CliDiagnosticCodes.InvalidCommandOrOptions,
                        $"Scene '{requestedScene}' was not found in story '{root}'.", false,
                        writers.Output, writers.Error);
                }
            }
            return writers.NativeLaunch.Launch(NativeHostMode.Debugger, root, settings.Scene, writers.Output,
                writers.Error, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or Win32Exception)
        {
            return CommandLine.Failure(CliDiagnosticCodes.IoFailure, $"Debugger launch failed: {ex.Message}", false, writers.Output, writers.Error);
        }
    }
}
