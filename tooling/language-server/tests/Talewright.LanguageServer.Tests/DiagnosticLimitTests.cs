using System.Text;
using System.Text.Json;
using Talewright.Cli;
using Talewright.Testing;

namespace Talewright.LanguageServer.Tests;

public sealed partial class ServerTests
{
    private const int EditorDiagnosticLimit = 200;

    [Theory]
    [InlineData(0)]
    [InlineData(199)]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(203)]
    public async Task Editor_diagnostics_are_capped_in_source_order_without_capping_cli_results(int count)
    {
        await using var client = new Client();
        var tale = new StringBuilder().Append(SceneFaker.Header("start")).Append("\r\n");
        for (var index = 0; index < count; index++)
        {
            tale.Append("😀 ").Append(SubstitutionFaker.Variable(new TaleVariableRef($"missing{index}")));
            tale.Append((index % 3) switch { 0 => "\r\n", 1 => "\n", _ => "\r" });
        }
        tale.Append(DivertFaker.To("END")).Append('\n');
        var text = tale.ToString();
        File.WriteAllText(client.PathOf("start.tale"), text);

        var output = new StringWriter();
        Assert.Equal(count == 0 ? 0 : 1, CommandLine.Run(["validate", client.Root, "--format", "json"], output, new StringWriter()));
        using var cli = JsonDocument.Parse(output.ToString());
        var expected = cli.RootElement.GetProperty("diagnostics").EnumerateArray()
            .Where(diagnostic => diagnostic.GetProperty("file").GetString() == "start.tale").ToArray();
        Assert.Equal(count, expected.Length);

        await client.Initialize();
        await client.Open("start.tale", text, 1);
        var actual = await client.Diagnostics("start.tale", 1);
        Assert.Equal(count <= EditorDiagnosticLimit ? count : EditorDiagnosticLimit + 1, actual.Length);
        var ordered = count <= EditorDiagnosticLimit ? expected : expected
            .OrderBy(diagnostic => diagnostic.GetProperty("line").GetInt32())
            .ThenBy(diagnostic => diagnostic.GetProperty("column").GetInt32()).ToArray();
        for (var index = 0; index < Math.Min(count, EditorDiagnosticLimit); index++)
            AssertDiagnostic(ordered[index], actual[index]);
        if (count <= EditorDiagnosticLimit)
        {
            Assert.DoesNotContain(actual, diagnostic => diagnostic.GetProperty("code").GetString() == "TWLSP005");
            return;
        }
        AssertLimitNotice(actual[^1], ordered[EditorDiagnosticLimit], count - EditorDiagnosticLimit);
    }

    [Theory]
    [InlineData(true, 199)]
    [InlineData(true, 200)]
    [InlineData(false, 200)]
    public async Task Cap_combines_compiler_and_lint_diagnostics_without_capping_cli(bool lintFirst, int compilerCount)
    {
        await using var client = new Client();
        var tale = new StringBuilder().Append(SceneFaker.Header("start")).Append('\n');
        if (lintFirst)
            tale.Append(DivertFaker.To("END")).Append('\n').Append(SceneFaker.Header("Bad_Scene")).Append('\n');
        for (var index = 0; index < compilerCount; index++)
            tale.Append(SubstitutionFaker.Variable(new TaleVariableRef($"missing{index}"))).Append('\n');
        tale.Append(DivertFaker.To("END")).Append('\n');
        if (!lintFirst)
            tale.Append(SceneFaker.Header("Bad_Scene")).Append('\n').Append(DivertFaker.To("END")).Append('\n');
        var text = tale.ToString();
        File.WriteAllText(client.PathOf("start.tale"), text);

        var output = new StringWriter();
        Assert.Equal(1, CommandLine.Run(["lint", client.Root, "--format", "json"], output, new StringWriter()));
        using var cli = JsonDocument.Parse(output.ToString());
        var expected = cli.RootElement.GetProperty("diagnostics").EnumerateArray()
            .Where(diagnostic => diagnostic.GetProperty("file").GetString() == "start.tale").ToArray();
        Assert.Equal(compilerCount + 1, expected.Length);
        Assert.Single(expected, diagnostic => diagnostic.GetProperty("code").GetString() == "TWL001");

        await client.Initialize();
        await client.Open("start.tale", text, 1);
        var actual = await client.Diagnostics("start.tale", 1);
        var ordered = expected.Length <= EditorDiagnosticLimit ? expected : expected
            .OrderBy(diagnostic => diagnostic.GetProperty("line").GetInt32())
            .ThenBy(diagnostic => diagnostic.GetProperty("column").GetInt32()).ToArray();
        Assert.Equal(expected.Length <= EditorDiagnosticLimit ? expected.Length : EditorDiagnosticLimit + 1, actual.Length);
        for (var index = 0; index < Math.Min(expected.Length, EditorDiagnosticLimit); index++)
            AssertDiagnostic(ordered[index], actual[index]);
        if (expected.Length <= EditorDiagnosticLimit)
        {
            Assert.DoesNotContain(actual, diagnostic => diagnostic.GetProperty("code").GetString() == "TWLSP005");
            return;
        }
        Assert.Equal(lintFirst, ordered[0].GetProperty("code").GetString() == "TWL001");
        Assert.Equal(!lintFirst, ordered[^1].GetProperty("code").GetString() == "TWL001");
        AssertLimitNotice(actual[^1], ordered[EditorDiagnosticLimit], 1);
    }

    private static void AssertDiagnostic(JsonElement expected, JsonElement actual)
    {
        Assert.Equal(expected.GetProperty("code").GetString(), actual.GetProperty("code").GetString());
        Assert.Equal(expected.GetProperty("message").GetString(), actual.GetProperty("message").GetString());
        Assert.Equal(expected.GetProperty("severity").GetString() == "error" ? 1 : 2, actual.GetProperty("severity").GetInt32());
        var range = actual.GetProperty("range");
        Assert.Equal(expected.GetProperty("line").GetInt32() - 1, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(expected.GetProperty("column").GetInt32() - 1, range.GetProperty("start").GetProperty("character").GetInt32());
        if (expected.TryGetProperty("endLine", out var endLine))
        {
            Assert.Equal(endLine.GetInt32() - 1, range.GetProperty("end").GetProperty("line").GetInt32());
            Assert.Equal(expected.GetProperty("endColumn").GetInt32() - 1, range.GetProperty("end").GetProperty("character").GetInt32());
        }
    }

    private static void AssertLimitNotice(JsonElement notice, JsonElement firstHidden, int omitted)
    {
        Assert.Equal("TWLSP005", notice.GetProperty("code").GetString());
        Assert.Equal(3, notice.GetProperty("severity").GetInt32());
        Assert.Equal("talewright", notice.GetProperty("source").GetString());
        Assert.Equal($"Showing the first {EditorDiagnosticLimit} problems in this file; {omitted} more are hidden. Fix these to see the rest.",
            notice.GetProperty("message").GetString());
        var point = notice.GetProperty("range").GetProperty("start");
        Assert.Equal(firstHidden.GetProperty("line").GetInt32() - 1, point.GetProperty("line").GetInt32());
        Assert.Equal(firstHidden.GetProperty("column").GetInt32() - 1, point.GetProperty("character").GetInt32());
        Assert.Equal(point.GetRawText(), notice.GetProperty("range").GetProperty("end").GetRawText());
    }
}
