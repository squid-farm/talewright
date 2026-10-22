using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using Spectre.Console.Cli;
using Talewright.Tale.Compilation;

namespace Talewright.Cli;

public static class CommandLine
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0)
        {
            return Failure("TWCLI001", "Choose a command. Use 'talewright --help' for available commands.", false, output, error);
        }

        var app = new CommandApp();
        app.Configure(config =>
        {
            config.SetApplicationName("talewright");
            config.CaseSensitivity(CaseSensitivity.All);
            config.UseStrictParsing();
            config.PropagateExceptions();
            config.ConfigureConsole(AnsiConsole.Create(new AnsiConsoleSettings
            {
                Out = new AnsiConsoleOutput(output),
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
            }));
            var writers = new CommandWriters(output, error);
            config.AddCommand<ValidateCommand>("validate")
                .WithDescription("Validate a story folder with stable human or JSON diagnostics.")
                .WithData(writers);
            config.AddCommand<PublishCommand>("publish")
                .WithDescription("Publish a validated story archive or complete deployable game.")
                .WithData(writers);
            config.AddCommand<DebugCommand>("debug")
                .WithDescription("Open the local native author workbench for a story folder.")
                .WithData(writers);
        });
        try
        {
            return app.Run(args);
        }
        catch (Exception ex) when (ex is CommandParseException or CommandRuntimeException)
        {
            return ReportInvocationFailure(args, ex.Message, output, error);
        }
    }

    private static int ReportInvocationFailure(string[] args, string message, TextWriter output, TextWriter error)
    {
        // A failed strict parse does not expose settings. Let Spectre recover the
        // diagnostic format without interpreting option spellings a second way.
        var app = new CommandApp();
        app.Configure(config =>
        {
            config.PropagateExceptions();
            config.CaseSensitivity(CaseSensitivity.All);
            config.AddCommand<InvocationFailureCommand>("validate")
                .WithData(new InvocationFailure(message, output, error));
        });
        try
        {
            return app.Run(args);
        }
        catch (Exception ex) when (ex is CommandParseException or CommandRuntimeException)
        {
            return Failure("TWCLI001", message, false, output, error);
        }
    }

    internal static int Failure(string code, string message, bool json, TextWriter output, TextWriter error)
    {
        if (json)
        {
            WriteJson(new ValidationOutput(1, false, [], new ErrorOutput(code, message)), output);
        }
        else
        {
            error.WriteLine($"{code}: {message}");
        }

        return 2;
    }

    internal static void WriteJson(ValidationOutput result, TextWriter output) =>
        output.WriteLine(JsonSerializer.Serialize(result, CliJsonContext.Default.ValidationOutput));
}

internal sealed record CommandWriters(TextWriter Output, TextWriter Error);

public sealed class ValidationSettings : CommandSettings
{
    [CommandArgument(0, "<story-root>")]
    public string Root { get; set; } = "";

    [CommandOption("--format <FORMAT>")]
    [System.ComponentModel.DefaultValue("human")]
    public string Format { get; set; } = "human";

    [CommandOption("--warnings-as-errors")]
    public bool WarningsAsErrors { get; set; }

    public override ValidationResult Validate() => Format is "human" or "json"
        ? ValidationResult.Success() : ValidationResult.Error("--format requires 'human' or 'json'.");
}

public sealed class ValidateCommand : Command<ValidationSettings>
{
    public override int Execute(CommandContext context, ValidationSettings settings, CancellationToken cancellationToken)
    {
        var writers = (CommandWriters)context.Data!;
        var json = settings.Format == "json";
        StorySource source;
        try
        {
            source = StorySource.FromDirectory(settings.Root);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return CommandLine.Failure("TWCLI002", $"Couldn't read story '{settings.Root}': {ex.Message}", json, writers.Output, writers.Error);
        }

        var result = StoryCompiler.Compile(source);
        var success = result.Success && (!settings.WarningsAsErrors || !result.Warnings.Any());
        if (json)
        {
            CommandLine.WriteJson(new ValidationOutput(1, success, result.Diagnostics.Select(d => new DiagnosticOutput(
                d.Code, d.Severity.ToString().ToLowerInvariant(), d.Message,
                d.Location.File, d.Location.Line, d.Location.Column)).ToArray()), writers.Output);
        }
        else
        {
            foreach (var diagnostic in result.Diagnostics)
            {
                writers.Output.WriteLine(diagnostic);
            }
            writers.Output.WriteLine(success ? "Validation succeeded." : "Validation failed.");
        }

        return success ? 0 : 1;
    }
}

internal sealed record InvocationFailure(string Message, TextWriter Output, TextWriter Error);

public sealed class InvocationFailureSettings : CommandSettings
{
    [CommandArgument(0, "[arguments]")]
    public string[] Arguments { get; set; } = [];

    [CommandOption("--format <FORMAT>")]
    public string? Format { get; set; }
}

public sealed class InvocationFailureCommand : Command<InvocationFailureSettings>
{
    public override int Execute(CommandContext context, InvocationFailureSettings settings, CancellationToken cancellationToken)
    {
        var failure = (InvocationFailure)context.Data!;
        return CommandLine.Failure("TWCLI001", failure.Message, settings.Format == "json", failure.Output, failure.Error);
    }
}

internal sealed record DiagnosticOutput(string Code, string Severity, string Message, string File, int Line, int Column);
internal sealed record ErrorOutput(string Code, string Message);
internal sealed record ValidationOutput(int SchemaVersion, bool Success, DiagnosticOutput[] Diagnostics,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ErrorOutput? Error = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ValidationOutput))]
internal partial class CliJsonContext : JsonSerializerContext;
