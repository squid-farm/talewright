using System.Text.Json;
using System.Text.Json.Serialization;
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
        var json = args.Zip(args.Skip(1)).Any(pair => pair.First == "--format" && pair.Second == "json");
        if (args.FirstOrDefault() == "publish")
        {
            return PublishCommand.Run(args.Skip(1).ToArray(), output, error);
        }
        if (args is ["--help"] or ["-h"])
        {
            output.WriteLine(Usage);
            return 0;
        }

        var warningsAsErrors = false;
        string? root = null;
        if (args.Length == 0 || args[0] != "validate")
        {
            return Failure("TWCLI001", Usage, json, output, error);
        }

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--format":
                    if (++i >= args.Length || args[i] is not ("human" or "json"))
                    {
                        return Failure("TWCLI001", "--format requires 'human' or 'json'.", json, output, error);
                    }

                    json = args[i] == "json";
                    break;
                case "--warnings-as-errors":
                    warningsAsErrors = true;
                    break;
                default:
                    if (args[i].StartsWith('-') || root is not null)
                    {
                        return Failure("TWCLI001", $"Unexpected argument '{args[i]}'. {Usage}", json, output, error);
                    }

                    root = args[i];
                    break;
            }
        }

        if (root is null)
        {
            return Failure("TWCLI001", Usage, json, output, error);
        }

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
        var success = result.Success && (!warningsAsErrors || !result.Warnings.Any());
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

internal sealed record DiagnosticOutput(string Code, string Severity, string Message, string File, int Line, int Column);
internal sealed record ErrorOutput(string Code, string Message);
internal sealed record ValidationOutput(int SchemaVersion, bool Success, DiagnosticOutput[] Diagnostics,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ErrorOutput? Error = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ValidationOutput))]
internal partial class CliJsonContext : JsonSerializerContext;
