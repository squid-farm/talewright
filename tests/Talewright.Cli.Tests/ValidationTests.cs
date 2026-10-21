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
        File.WriteAllText(Path.Combine(_root, "story.yaml"),
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
        File.Delete(Path.Combine(_root, "story.yaml"));
        var result = Run("--format", "json");
        Assert.Equal(1, result.Code);
        Assert.Contains("TW4002", result.Output);
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
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        using var json = JsonDocument.Parse(await output);
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Empty(await error);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
