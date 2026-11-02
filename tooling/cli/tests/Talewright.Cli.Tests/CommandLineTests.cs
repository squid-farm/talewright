using System.Reflection;
using Talewright.Cli;

namespace Talewright.Cli.Tests;

public sealed class CommandLineTests
{
    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    public void Version_prints_the_cli_assembly_informational_version(string option)
    {
        var version = typeof(CommandLine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        Assert.NotNull(version);
        Assert.False(string.IsNullOrWhiteSpace(version.InformationalVersion));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var code = CommandLine.Run([option], output, error);

        Assert.Equal(0, code);
        Assert.Equal(version.InformationalVersion + Environment.NewLine, output.ToString());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public void Root_help_lists_version_options()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var code = CommandLine.Run(["--help"], output, error);

        Assert.Equal(0, code);
        Assert.Contains("--version", output.ToString());
        Assert.Matches(@"(?<!-)-v(?:,|\s)", output.ToString());
        Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData("--VERSION")]
    [InlineData("-V")]
    [InlineData("--version", "--bogus")]
    [InlineData("-v", "--bogus")]
    [InlineData("validate", "unused", "--version")]
    [InlineData("lint", "unused", "-v")]
    [InlineData("debug", "unused", "--version")]
    [InlineData("run", "unused", "--version")]
    [InlineData("publish", "unused", "-v")]
    public void Invalid_version_invocations_are_rejected(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var code = CommandLine.Run(args, output, error);

        Assert.Equal(2, code);
        Assert.Empty(output.ToString());
        Assert.Contains("TWCLI001", error.ToString());
    }
}
