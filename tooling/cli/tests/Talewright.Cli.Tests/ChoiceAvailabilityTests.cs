using System.Text.Json;
using Talewright.Cli;

namespace Talewright.Cli.Tests;

public sealed class ChoiceAvailabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"talewright-choices-{Guid.NewGuid():N}");

    public ChoiceAvailabilityTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "story.talewright.yaml"), """
            game: {id: cli, title: CLI, start: start}
            characters:
              player: {first-name: Ada}
            flags: [ready]
            """);
        File.WriteAllText(Path.Combine(_root, "main.tale"), """
            === start
            * [Maybe] (requires: flag:ready) -> END
            """);
    }

    private (int Code, string Output, string Error) Run(string command, string format, bool strict)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var args = new List<string> { command, _root, "--format", format };
        if (strict) args.Add("--warnings-as-errors");
        var code = CommandLine.Run(args.ToArray(), output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Theory]
    [InlineData("validate", "human")]
    [InlineData("validate", "json")]
    [InlineData("lint", "human")]
    [InlineData("lint", "json")]
    public void Availability_warning_succeeds_normally_and_fails_when_warnings_are_errors(string command, string format)
    {
        var normal = Run(command, format, strict: false);
        var strict = Run(command, format, strict: true);

        Assert.Equal(0, normal.Code);
        Assert.Equal(1, strict.Code);
        Assert.Empty(normal.Error);
        Assert.Empty(strict.Error);
        if (format == "human")
        {
            Assert.Contains("main.tale:1:1: warning TW3013:", normal.Output);
            Assert.Contains("main.tale:1:1: warning TW3013:", strict.Output);
            Assert.Contains("'start'", normal.Output);
            Assert.Contains("* -> target", normal.Output);
        }
        else
        {
            using var normalJson = JsonDocument.Parse(normal.Output);
            using var strictJson = JsonDocument.Parse(strict.Output);
            Assert.True(normalJson.RootElement.GetProperty("success").GetBoolean());
            Assert.False(strictJson.RootElement.GetProperty("success").GetBoolean());
            var warning = Assert.Single(normalJson.RootElement.GetProperty("diagnostics").EnumerateArray());
            var strictWarning = Assert.Single(strictJson.RootElement.GetProperty("diagnostics").EnumerateArray());
            Assert.Equal(warning.GetRawText(), strictWarning.GetRawText());
            Assert.Equal("TW3013", warning.GetProperty("code").GetString());
            Assert.Equal("warning", warning.GetProperty("severity").GetString());
            Assert.Equal("main.tale", warning.GetProperty("file").GetString());
            Assert.Equal(1, warning.GetProperty("line").GetInt32());
            Assert.Equal(1, warning.GetProperty("column").GetInt32());
            Assert.Contains("'start'", warning.GetProperty("message").GetString());
            Assert.Contains("* -> target", warning.GetProperty("message").GetString());
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
