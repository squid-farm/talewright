using Spectre.Console.Cli;
using Talewright.Authoring;
using Talewright.Tale.Analysis;
using Talewright.Tale.Compilation;
using Talewright.Tale.Diagnostics;

namespace Talewright.Cli;

public sealed class LintSettings : CliSettings
{
    [CommandArgument(0, "<story-root>")]
    public string Root { get; set; } = "";

    [CommandOption("--format <FORMAT>")]
    [System.ComponentModel.DefaultValue("human")]
    public string Format { get; set; } = "human";

    [CommandOption("--warnings-as-errors")]
    public bool WarningsAsErrors { get; set; }

    [CommandOption("--rule <ID=SEVERITY>")]
    public string[] RuleOverrides { get; set; } = [];
}

public sealed class LintCommand : Command<LintSettings>
{
    public override int Execute(CommandContext context, LintSettings settings, CancellationToken cancellationToken)
    {
        var writers = (CommandWriters)context.Data!;
        if (settings.Format is not ("human" or "json"))
        {
            return CommandLine.Failure("TWCLI001", "--format requires 'human' or 'json'.",
                false, writers.Output, writers.Error);
        }

        var json = settings.Format == "json";
        StorySource source;
        try
        {
            source = StorySource.FromDirectory(settings.Root);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return CommandLine.Failure("TWCLI002", $"Couldn't read story '{settings.Root}': {ex.Message}",
                json, writers.Output, writers.Error);
        }

        LintConfiguration configuration;
        try
        {
            configuration = LintConfiguration.LoadFromDirectory(settings.Root)
                .WithRuleOverrides(settings.RuleOverrides);
        }
        catch (LintConfigurationException ex)
        {
            return CommandLine.Failure("TWCLI003", $"Invalid lint configuration: {ex.Message}",
                json, writers.Output, writers.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return CommandLine.Failure("TWCLI002", $"Couldn't read lint configuration in '{settings.Root}': {ex.Message}",
                json, writers.Output, writers.Error);
        }

        var analysis = StoryAnalyzer.Analyze(source);
        var result = StoryLintAnalyzer.Analyze(analysis, configuration);
        var compilerDiagnostics = result.CompilerDiagnostics.Select(diagnostic => new DiagnosticOutput(
            diagnostic.Code, diagnostic.Severity.ToString().ToLowerInvariant(), diagnostic.Message,
            diagnostic.Location.File, diagnostic.Location.Line, diagnostic.Location.Column));
        var lintDiagnostics = result.RuleDiagnostics.Select(diagnostic => new DiagnosticOutput(
            diagnostic.Code, diagnostic.Severity.ToString().ToLowerInvariant(), diagnostic.Message,
            diagnostic.Range.Start.File, diagnostic.Range.Start.Line, diagnostic.Range.Start.Column,
            diagnostic.Range.End.Line, diagnostic.Range.End.Column));
        var diagnostics = compilerDiagnostics.Concat(lintDiagnostics).ToArray();
        var hasErrors = result.CompilerDiagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            || result.RuleDiagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var hasWarnings = result.CompilerDiagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning)
            || result.RuleDiagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning);
        var success = !hasErrors && (!settings.WarningsAsErrors || !hasWarnings);

        if (json)
        {
            CommandLine.WriteJson(new ValidationOutput(1, success, diagnostics), writers.Output);
        }
        else
        {
            foreach (var diagnostic in result.CompilerDiagnostics)
            {
                writers.Output.WriteLine(diagnostic);
            }
            foreach (var diagnostic in result.RuleDiagnostics)
            {
                writers.Output.WriteLine(diagnostic);
            }
            writers.Output.WriteLine(success ? "Lint succeeded." : "Lint failed.");
        }

        return success ? 0 : 1;
    }
}
