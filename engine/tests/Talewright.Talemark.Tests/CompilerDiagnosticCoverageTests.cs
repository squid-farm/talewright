using System.Text.RegularExpressions;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Testing;

namespace Talewright.Talemark.Tests;

public sealed class CompilerDiagnosticCoverageTests
{
    private const string MainFile = "main.tale";
    private const string MinimalYaml = """
        game: {id: coverage, title: Coverage, start: start}
        characters:
          player: {first-name: Ada}
        """;

    private const string NoChecksYaml = """
        game:
          id: coverage
          title: Coverage
          start: start
          ruleset: {base: none}
        characters:
          player: {first-name: Ada}
        """;

    private static readonly Regex CompilerCodePattern = new("\"(?<code>TW[0-9]{4})\"", RegexOptions.CultureInvariant);
    private static readonly Regex ReferenceRowPattern = new(
        @"(?m)^\|[^\w|]*(?<code>TW[0-9]{4})\s*\|\s*(?<meaning>[^|\r\n]+)\|",
        RegexOptions.CultureInvariant);

    private sealed record DiagnosticCase(
        string Code,
        StorySource Invalid,
        DiagnosticSeverity Severity,
        SourceLocation? Location = null,
        StorySource? Valid = null);

    private static readonly StorySource ValidCheck = Scene(
        "::: skill-check athletics dc 1\nsuccess: -> END\nfailure: -> END\n:::");

    private static readonly DiagnosticCase[] Cases =
    [
        Error("TW1001", Scene("* { ~ set flag:left } -> END")),
        Error("TW1002", Scene("~ var:visits = 2147483648\n-> END")),
        Error("TW1003", Scene("~ set var:visits\n-> END"), new(MainFile, 2, 7),
            Scene("~ var:visits += 1\n-> END")),
        Error("TW1004", Scene("A {unclosed\n-> END")),
        Error("TW1005", Scene("{if true}\nx\n-> END")),
        Error("TW1006", Scene("@hub-limit 0\n-> END")),
        Error("TW1007", Scene("> player without a colon\n-> END"), new(MainFile, 2, 1),
            Scene("> player: Hello.\n-> END")),
        Error("TW1008", Scene("-> bad..id")),
        Error("TW1009", Scene("success: -> END\n-> END"), new(MainFile, 2, 1), ValidCheck),
        Error("TW1010", Scene("* ->")),
        Error("TW1011", Scene("* missing label")),
        Error("TW1012", Scene("::: unknown\n-> END"), new(MainFile, 2, 1), ValidCheck),
        Error("TW1013", Scene("::: skill-check athletics dc\nsuccess:\nfailure:\n:::\n-> END")),
        Error("TW1014", Scene("{image()}\n-> END")),
        Error("TW1015", Scene("![Alt](map)\n-> END")),
        Error("TW1016", Scene($"@requires {new string('(', 257)}true{new string(')', 257)}\n-> END")),

        Error("TW2001", Scene("{player:strenght}\n-> END")),
        Error("TW2002", Scene("-> nowhere")),
        Error("TW2003", Scene("~ var:visits = \"three\"\n-> END")),
        Error("TW2004", Scene("{if flag:never-set}x{/if}\n-> END")),
        Error("TW2005", Scene("{if @Lucky}x{/if}\n-> END")),
        Error("TW2006", Scene("{player:name#shout}\n-> END")),
        Error("TW2007", Scene("~ var:x = var:y\n~ var:y = var:x\n-> END")),
        Error("TW2010", Scene("{image(\"../map.png\", \"Map\")}\n-> END")),
        Error("TW2011", Scene("{image(\"map.bmp\", \"Map\")}\n-> END", imagePaths: ["images/map.bmp"])),
        Error("TW2012", Scene("{image(\"missing.png\", \"Map\")}\n-> END")),
        Warning("TW2013", Scene("{image(\"map\", \"Map\")}\n-> END",
            imagePaths: ["images/map.png", "images/map.jpg"])),
        Warning("TW2014", Scene("{image(\"map\")}\n-> END", imagePaths: ["images/map.png"])),

        Error("TW3001", Story("Text before a scene.\n=== start\n-> END"), new(MainFile, 1, 1),
            Scene("-> END")),
        Error("TW3002", Scene("Just text.")),
        Error("TW3003", Scene("* [Wait]")),
        Error("TW3005", Scene("::: skill-check athletics dc 1\nsuccess: -> END\nfailure: -> END\n:::",
            NoChecksYaml)),
        Error("TW3007", Story("=== elsewhere\n-> END")),
        Warning("TW3008", Scene("@hub nonexistent\n-> END")),
        Warning("TW3009", Story("=== start\n-> END\n=== orphan\n-> END")),
        Error("TW3010", Story("=== start\n-> END\n=== start\n-> END")),
        Error("TW3011", Scene("* [Leave] -> END\nProse.")),
        Warning("TW3012", Scene("-> END\nAfter.")),
        Warning("TW3013", Scene("* [Maybe] (requires: player:hp > 0) -> END")),

        Error("TW4001", Story("=== start\n-> END", "game: [")),
        Error("TW4002", new StorySource(new Dictionary<string, string> { [MainFile] = "=== start\n-> END" })),
        Error("TW4003", Story("=== start\n-> END", MinimalYaml + "\nflags: not-a-list")),
        Error("TW4004", Story("=== start\n-> END", """
            game: {id: coverage, title: Coverage, start: start, ruleset: {base: missing}}
            characters: {player: {first-name: Ada}}
            """)),
        Error("TW4005", Story("=== start\n-> END", """
            game: {id: coverage, title: Coverage, start: start}
            characters: {player: {first-name: Ada, stats: {bogus: 1}}}
            """)),
        Warning("TW4006", Story("=== start\n-> END", MinimalYaml + "\nunknown: true")),
        Error("TW4007", new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = TestStories.DefaultYaml,
            [MainFile] = "=== start\n-> END",
            ["extra/duplicate.talewright.yaml"] = "characters: {player: {first-name: Other}}",
        })),
    ];

    [Fact]
    public void Compiler_codes_have_documented_meanings_and_passing_behavioral_cases()
    {
        var root = RepositoryRoot();
        var compilerDirectory = Path.Combine(root, "engine", "src", "Talewright.Talemark");
        var compilerCodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(compilerDirectory, "*.cs", SearchOption.AllDirectories))
        {
            var parts = Path.GetRelativePath(compilerDirectory, path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Any(part => part is "bin" or "obj"))
            {
                continue;
            }
            compilerCodes.UnionWith(ExtractCompilerCodes(File.ReadAllText(path)));
        }

        Assert.NotEmpty(compilerCodes);
        var reference = File.ReadAllText(Path.Combine(root, "docs", "reference", "diagnostics.md"));
        var failures = CheckContract(compilerCodes, ExtractDocumentedCodes(reference), Cases);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Theory]
    [InlineData("TW1003")]
    [InlineData("TW1007")]
    [InlineData("TW1009")]
    [InlineData("TW1012")]
    [InlineData("TW3001")]
    public void Previously_uncovered_codes_have_a_location_and_a_valid_counterpart(string code)
    {
        var fixture = Assert.Single(Cases, item => item.Code == code);
        var result = StoryCompiler.Compile(fixture.Invalid);
        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.NotNull(fixture.Location);
        Assert.Equal(fixture.Location, diagnostic.Location);

        var validSource = fixture.Valid ?? throw new InvalidOperationException($"{code} needs a valid counterpart.");
        var valid = StoryCompiler.Compile(validSource);
        Assert.True(valid.Success, string.Join("\n", valid.Diagnostics));
        Assert.DoesNotContain(valid.Diagnostics, item => item.Code == code);
    }

    [Fact]
    public void No_tale_files_reports_tw3001_at_the_manifest()
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = TestStories.DefaultYaml,
        }));
        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "TW3001");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(new SourceLocation(StorySource.ManifestFileName, 0, 0), diagnostic.Location);
    }

    [Fact]
    public void Unclosed_check_block_reports_tw1012_at_its_opening()
    {
        var result = StoryCompiler.Compile(Scene(
            "::: skill-check athletics dc 1\nsuccess: -> END\nfailure: -> END"));
        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "TW1012");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(new SourceLocation(MainFile, 2, 1), diagnostic.Location);
    }

    private static IReadOnlyList<string> CheckContract(
        IReadOnlySet<string> compilerCodes,
        IReadOnlySet<string> documentedCodes,
        IEnumerable<DiagnosticCase> cases)
    {
        var failures = new List<string>();
        var covered = new HashSet<string>(StringComparer.Ordinal);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fixture in cases)
        {
            var result = StoryCompiler.Compile(fixture.Invalid);
            emitted.UnionWith(result.Diagnostics.Select(diagnostic => diagnostic.Code));
            if (result.Diagnostics.Any(diagnostic => diagnostic.Code == fixture.Code
                && diagnostic.Severity == fixture.Severity
                && (fixture.Location is null || diagnostic.Location == fixture.Location)))
            {
                covered.Add(fixture.Code);
            }
            else
            {
                failures.Add($"{fixture.Code}: fixture did not emit the expected {fixture.Severity} diagnostic"
                    + (fixture.Location is null ? "" : $" at {fixture.Location}")
                    + $". Actual diagnostics: {string.Join("; ", result.Diagnostics)}");
            }

            if (fixture.Valid is { } validSource)
            {
                var valid = StoryCompiler.Compile(validSource);
                emitted.UnionWith(valid.Diagnostics.Select(diagnostic => diagnostic.Code));
                if (!valid.Success || valid.Diagnostics.Any(diagnostic => diagnostic.Code == fixture.Code))
                {
                    failures.Add($"{fixture.Code}: valid counterpart failed or emitted the diagnostic: "
                        + string.Join("; ", valid.Diagnostics));
                }
            }
        }

        foreach (var code in compilerCodes.Order(StringComparer.Ordinal))
        {
            if (!documentedCodes.Contains(code))
            {
                failures.Add($"{code}: no documented meaning in docs/reference/diagnostics.md.");
            }
            if (!covered.Contains(code))
            {
                failures.Add($"{code}: no passing explicit compiler diagnostic regression case.");
            }
        }
        foreach (var code in emitted.Except(compilerCodes).Order(StringComparer.Ordinal))
        {
            failures.Add($"{code}: emitted by a fixture but absent from the compiler code inventory.");
        }
        return failures;
    }

    private static HashSet<string> ExtractCompilerCodes(string source) =>
        CompilerCodePattern.Matches(source).Select(match => match.Groups["code"].Value)
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> ExtractDocumentedCodes(string reference) =>
        ReferenceRowPattern.Matches(reference)
            .Where(match => !string.IsNullOrWhiteSpace(match.Groups["meaning"].Value))
            .Select(match => match.Groups["code"].Value)
            .ToHashSet(StringComparer.Ordinal);

    private static DiagnosticCase Error(string code, StorySource invalid, SourceLocation? location = null,
        StorySource? valid = null) => new(code, invalid, DiagnosticSeverity.Error, location, valid);

    private static DiagnosticCase Warning(string code, StorySource invalid) =>
        new(code, invalid, DiagnosticSeverity.Warning);

    private static StorySource Scene(string body, string? yaml = null, params string[] imagePaths) =>
        Story("=== start\n" + body, yaml, imagePaths);

    private static StorySource Story(string tale, string? yaml = null, params string[] imagePaths) =>
        new(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = yaml ?? TestStories.DefaultYaml,
            [MainFile] = tale,
        }, imagePaths);

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Talewright.slnx")))
        {
            root = root.Parent;
        }
        Assert.NotNull(root);
        return root.FullName;
    }
}
