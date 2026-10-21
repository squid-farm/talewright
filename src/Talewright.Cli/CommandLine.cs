using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using Spectre.Console.Cli;
using Talewright.Tale.Compilation;

namespace Talewright.Cli;

public static class CommandLine
{
    public const string Usage = """
        Usage:
          talewright validate <story-root> [--format human|json] [--warnings-as-errors]
          talewright publish <story-root> --output <zip> [-a|--all-in-one] [--target web|desktop] [--runtime <rid>] [--force]
        """;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        var json = args.Contains("--format=json") ||
            args.Zip(args.Skip(1)).Any(pair => pair.First == "--format" && pair.Second == "json");
        if (args.Length == 0)
        {
            return Failure("TWCLI001", Usage, json, output, error);
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
        });
        try
        {
            return app.Run(args);
        }
        catch (Exception ex) when (ex is CommandParseException or CommandRuntimeException)
        {
            return Failure("TWCLI001", ex.Message, json, output, error);
        }
    }

    internal static int Validate(ValidationSettings settings, TextWriter output, TextWriter error)
    {
        var json = settings.Format == "json";
        var root = settings.Root;
        StorySource source;
        try
        {
            source = StorySource.FromDirectory(root);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Failure("TWCLI002", $"Couldn't read story '{root}': {ex.Message}", json, output, error);
        }

        var result = StoryCompiler.Compile(source);
        var success = result.Success && (!settings.WarningsAsErrors || !result.Warnings.Any());
        if (json)
        {
            WriteJson(new ValidationOutput(1, success, result.Diagnostics.Select(d => new DiagnosticOutput(
                d.Code, d.Severity.ToString().ToLowerInvariant(), d.Message,
                d.Location.File, d.Location.Line, d.Location.Column)).ToArray()), output);
        }
        else
        {
            foreach (var diagnostic in result.Diagnostics)
            {
                output.WriteLine(diagnostic);
            }

            output.WriteLine(success ? "Validation succeeded." : "Validation failed.");
        }

        return success ? 0 : 1;
    }

    private static int Failure(string code, string message, bool json, TextWriter output, TextWriter error)
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

    private static void WriteJson(ValidationOutput result, TextWriter output) =>
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
        return CommandLine.Validate(settings, writers.Output, writers.Error);
    }
}

internal sealed record DiagnosticOutput(string Code, string Severity, string Message, string File, int Line, int Column);
internal sealed record ErrorOutput(string Code, string Message);
internal sealed record ValidationOutput(int SchemaVersion, bool Success, DiagnosticOutput[] Diagnostics,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ErrorOutput? Error = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ValidationOutput))]
internal partial class CliJsonContext : JsonSerializerContext;
