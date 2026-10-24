using System.Diagnostics;
using System.Text.Json;
using Talewright.Cli;
using Talewright.Tale.Compilation;

namespace Talewright.Cli.Tests;

public sealed class ValidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"talewright-cli-{Guid.NewGuid():N}");

    public ValidationTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "story.talewright.yaml"),
            "id: cli\ntitle: CLI\nversion: 1\nstart: a\ncharacters:\n  player: { first-name: Tester }\n");
        File.WriteAllText(Path.Combine(_root, "main.tale"), "=== a\nHello.\n-> END");
    }

    private (int Code, string Output, string Error) Run(params string[] options)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = CommandLine.Run(["validate", _root, .. options], output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void Valid_story_has_exact_json_contract()
    {
        var result = Run("--format", "json");
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        Assert.Equal("""{"schemaVersion":1,"success":true,"diagnostics":[]}""", result.Output.Trim());
    }

    [Theory]
    [InlineData("=== a\n-> unknown", "TW2002")]
    [InlineData("=== a\n{if player:hp}No.{/if}\n-> END", "TW2003")]
    [InlineData("=== a\n* missing label\n-> END", "TW1011")]
    [InlineData("=== a\nDead end.", "TW3002")]
    public void Compiler_diagnostics_match_library(string tale, string expectedCode)
    {
        File.WriteAllText(Path.Combine(_root, "main.tale"), tale);
        var expected = StoryCompiler.Compile(StorySource.FromDirectory(_root));
        var result = Run("--format", "json");
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal(1, result.Code);
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        var diagnostics = json.RootElement.GetProperty("diagnostics").EnumerateArray().ToArray();
        Assert.Contains(diagnostics, d => d.GetProperty("code").GetString() == expectedCode);
        Assert.Equal(expected.Diagnostics.Count, diagnostics.Length);
        for (var i = 0; i < diagnostics.Length; i++)
        {
            var actual = diagnostics[i];
            var diagnostic = expected.Diagnostics[i];
            Assert.Equal(["code", "column", "file", "line", "message", "severity"],
                actual.EnumerateObject().Select(p => p.Name).Order());
            Assert.Equal(diagnostic.Code, actual.GetProperty("code").GetString());
            Assert.Equal(diagnostic.Message, actual.GetProperty("message").GetString());
            Assert.Equal(diagnostic.Severity.ToString().ToLowerInvariant(), actual.GetProperty("severity").GetString());
            Assert.Equal(diagnostic.Location.File, actual.GetProperty("file").GetString());
            Assert.Equal(diagnostic.Location.Line, actual.GetProperty("line").GetInt32());
            Assert.Equal(diagnostic.Location.Column, actual.GetProperty("column").GetInt32());
        }
    }

    [Fact]
    public void Warnings_succeed_unless_explicitly_rejected()
    {
        File.AppendAllText(Path.Combine(_root, "main.tale"), "\n=== unused\n-> END");
        Assert.Equal(0, Run().Code);
        var result = Run("--format", "json", "--warnings-as-errors");
        Assert.Equal(1, result.Code);
        Assert.Contains("\"severity\":\"warning\"", result.Output);
    }

    [Fact]
    public void Missing_manifest_is_a_compiler_error()
    {
        File.Delete(Path.Combine(_root, "story.talewright.yaml"));
        var result = Run("--format", "json");
        Assert.Equal(1, result.Code);
        Assert.Contains("TW4002", result.Output);
    }

    [Theory]
    [InlineData("~ var:weather = \"stormy\"", 0)]
    [InlineData("~ var:weather = 3", 1)]
    [InlineData("~ var:weather += \"stormy\"", 1)]
    public void Explicit_text_validation_uses_existing_exit_and_diagnostic_contract(string effect, int exit)
    {
        File.AppendAllText(Path.Combine(_root, "story.talewright.yaml"), "vars:\n  weather: \"sunny\"\n");
        File.WriteAllText(Path.Combine(_root, "main.tale"), $"=== a\n{effect}\n{{var:weather}}\n-> END");
        var result = Run("--format", "json");
        Assert.Equal(exit, result.Code);
        Assert.Empty(result.Error);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal(exit == 0, json.RootElement.GetProperty("success").GetBoolean());
        if (exit != 0)
            Assert.Contains(json.RootElement.GetProperty("diagnostics").EnumerateArray(),
                diagnostic => diagnostic.GetProperty("code").GetString() == "TW2003");
    }

    [Fact]
    public void Missing_directory_is_an_explicit_operational_error()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, CommandLine.Run(["validate", Path.Combine(_root, "missing"), "--format", "json"], output, error));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("TWCLI002", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--format")]
    [InlineData("--format", "xml")]
    [InlineData("second-root")]
    public void Invalid_arguments_do_not_look_successful(params string[] options)
    {
        var result = Run(options);
        Assert.Equal(2, result.Code);
        Assert.Contains("TWCLI001", result.Error);
        Assert.Empty(result.Output);
    }

    [Fact]
    public async Task Executable_entry_point_returns_exit_code_and_json()
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(CommandLine).Assembly.Location);
        start.ArgumentList.Add("validate");
        start.ArgumentList.Add(_root);
        start.ArgumentList.Add("--format");
        start.ArgumentList.Add("json");
        start.ArgumentList.Add("--non-interactive");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        using var json = JsonDocument.Parse(await output);
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Empty(await error);
    }

    [Theory]
    [InlineData("--help", "", "validate")]
    [InlineData("validate", "--help", "--warnings-as-errors")]
    [InlineData("publish", "--help", "--all-in-one")]
    [InlineData("--help", "", "--non-interactive")]
    [InlineData("validate", "--help", "--non-interactive")]
    [InlineData("publish", "--help", "--non-interactive")]
    [InlineData("debug", "--help", "--non-interactive")]
    public void Framework_help_describes_commands_and_options(string first, string second, string expected)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var args = second.Length == 0 ? new[] { first } : new[] { first, second };
        Assert.Equal(0, CommandLine.Run(args, output, error));
        Assert.Contains(expected, output.ToString());
        Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData("--format", "json", "--bogus")]
    [InlineData("--bogus", "--format=json")]
    public void Framework_parse_errors_keep_machine_readable_failure_contract(params string[] options)
    {
        var result = Run(options);
        Assert.Equal(2, result.Code);
        Assert.Empty(result.Error);
        using var json = JsonDocument.Parse(result.Output);
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("TWCLI001", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("bogus", json.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public void Format_after_option_terminator_does_not_enable_json()
    {
        var result = Run("--", "--format=json");
        Assert.Equal(0, result.Code);
        Assert.Contains("Validation succeeded.", result.Output);
        Assert.Empty(result.Error);
    }

    [Fact]
    public void Framework_equals_syntax_preserves_json_output()
    {
        var result = Run("--format=json");
        Assert.Equal(0, result.Code);
        Assert.Equal("""{"schemaVersion":1,"success":true,"diagnostics":[]}""", result.Output.Trim());
        Assert.Empty(result.Error);
    }

    [Theory]
    [InlineData("--non-interactive", "--format", "json")]
    [InlineData("--format=json", "--non-interactive")]
    [InlineData("--non-interactive", "--bogus", "--format=json")]
    [InlineData("--bogus", "--format=json", "--non-interactive")]
    public void Noninteractive_validation_preserves_exact_json_and_parse_errors(params string[] options)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = CommandLine.Run(["validate", _root, .. options], output, error,
            new CliInput(new UnreadableInput(), false, false));
        using var json = JsonDocument.Parse(output.ToString());
        var invalid = options.Contains("--bogus");
        Assert.Equal(invalid ? 2 : 0, code);
        Assert.Equal(!invalid, json.RootElement.GetProperty("success").GetBoolean());
        if (invalid)
        {
            Assert.Equal("TWCLI001", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        else
        {
            Assert.Equal("""{"schemaVersion":1,"success":true,"diagnostics":[]}""", output.ToString().Trim());
        }
        Assert.Empty(error.ToString());
    }

    [Fact]
    public void Noninteractive_does_not_change_compiler_diagnostics_or_warnings_policy()
    {
        File.AppendAllText(Path.Combine(_root, "main.tale"), "\n=== unused\n-> END");
        Assert.Equal(Run("--format=json"), Run("--format=json", "--non-interactive"));
        Assert.Equal(Run("--warnings-as-errors", "--format=json"),
            Run("--non-interactive", "--warnings-as-errors", "--format=json"));
        File.WriteAllText(Path.Combine(_root, "main.tale"), "=== a\n-> missing");
        Assert.Equal(Run("--format=json"), Run("--format=json", "--non-interactive"));
        Assert.Equal(Run(), Run("--non-interactive"));
        Directory.Delete(_root, recursive: true);
        Assert.Equal(Run("--format=json"), Run("--format=json", "--non-interactive"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Debug_accepts_shared_option_without_changing_launch_validation()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, CommandLine.Run(["debug", Path.Combine(_root, "missing"), "--non-interactive"],
            output, error, new CliInput(new UnreadableInput(), false, false)));
        Assert.Contains("TWCLI002", error.ToString());
        Assert.Contains("does not exist", error.ToString());
        Assert.Empty(output.ToString());
    }

    [Theory]
    [InlineData("--non-interactiv")]
    [InlineData("--Non-Interactive")]
    [InlineData("--non-interactive", "--bogus")]
    [InlineData("--non-interactive", "--force")]
    public void Shared_option_preserves_strict_parsing(params string[] options)
    {
        var result = Run(options);
        Assert.Equal(2, result.Code);
        Assert.Contains("TWCLI001", result.Error);
        Assert.Empty(result.Output);
    }

    [Fact]
    public void Shared_option_after_terminator_is_not_reinterpreted()
    {
        Assert.Equal(Run("--", "--non-interactive"), Run("--", "--format=json"));
    }

    private sealed class UnreadableInput : TextReader
    {
        public override string? ReadLine() => throw new InvalidOperationException("Validation/debug must not read input.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shared_contract_does_not_change_missing_or_unknown_command_errors(bool unknown)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, CommandLine.Run(unknown ? ["unknown", "--non-interactive"] : [], output, error,
            new CliInput(new UnreadableInput(), false, false)));
        Assert.Empty(output.ToString());
        Assert.Contains("TWCLI001", error.ToString());
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
