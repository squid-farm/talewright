using System.Reflection;
using System.Text.RegularExpressions;
using Talewright.Authoring;
using Talewright.Cli;
using Talewright.LanguageServer;
using Talewright.Talemark.Diagnostics;

namespace Talewright.LanguageServer.Tests;

public sealed class DiagnosticCodeCatalogTests
{
    private static readonly Regex DocumentedCodePattern = new(
        @"(?m)^\|[^\w|]*(?<code>TW(?:[0-9]{4}|L[0-9]{3}|LSP[0-9]{3}|CLI[0-9]{3}))\s*\|",
        RegexOptions.CultureInvariant);
    private static readonly Regex CodeLiteralPattern = new(
        "\"(?<code>(?:TW[0-9]{4}|TWL[0-9]{3}|TWLSP[0-9]{3}|TWCLI[0-9]{3}))(?:\"|:)",
        RegexOptions.CultureInvariant);

    [Fact]
    public void Diagnostic_catalogs_are_unique_owned_and_match_the_reference()
    {
        var root = RepositoryRoot();
        var catalogs = new (Type Type, Regex Ownership)[]
        {
            (typeof(CompilerDiagnosticCodes), new Regex(@"\ATW[1-4][0-9]{3}\z", RegexOptions.CultureInvariant)),
            (typeof(AuthoringDiagnosticCodes), new Regex(@"\A(?:TW500[12]|TWL00[12])\z", RegexOptions.CultureInvariant)),
            (typeof(LanguageServerDiagnosticCodes), new Regex(@"\ATWLSP00[45]\z", RegexOptions.CultureInvariant)),
            (typeof(CommandLine).Assembly.GetType("Talewright.Cli.CliDiagnosticCodes")
                ?? throw new InvalidOperationException("The CLI diagnostic catalog was not found."),
                new Regex(@"\ATWCLI00[1-3]\z", RegexOptions.CultureInvariant)),
        };
        var catalogCodes = new List<string>();
        foreach (var (type, ownership) in catalogs)
        {
            var codes = ReadCodes(type);
            Assert.NotEmpty(codes);
            foreach (var code in codes)
            {
                Assert.Matches(ownership, code);
                catalogCodes.Add(code);
            }
        }

        var duplicates = catalogCodes.GroupBy(code => code, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Empty(duplicates);

        var documentedCodes = DocumentedCodePattern.Matches(ReadRepositoryFile(root, "docs/reference/diagnostics.md"))
            .Select(match => match.Groups["code"].Value)
            .ToHashSet(StringComparer.Ordinal);
        var catalogCodeSet = catalogCodes.ToHashSet(StringComparer.Ordinal);
        Assert.True(catalogCodeSet.SetEquals(documentedCodes),
            $"Undocumented catalog codes: {string.Join(", ", catalogCodeSet.Except(documentedCodes).Order())}; "
            + $"unowned documented codes: {string.Join(", ", documentedCodes.Except(catalogCodeSet).Order())}.");

        var logType = typeof(LanguageServerLogCodes);
        var logCodes = ReadCodes(logType);
        Assert.Equal(new[] { "TWLSP001", "TWLSP002", "TWLSP003" }, logCodes.Order(StringComparer.Ordinal));
        Assert.Empty(logCodes.Intersect(catalogCodeSet, StringComparer.Ordinal));
        Assert.Empty(logCodes.Intersect(documentedCodes, StringComparer.Ordinal));
    }

    [Fact]
    public void Production_code_literals_are_limited_to_catalog_files()
    {
        var root = RepositoryRoot();
        var catalogFiles = new HashSet<string>(StringComparer.Ordinal)
        {
            Path.Combine(root, "engine", "src", "Talewright.Talemark", "Diagnostics", "CompilerDiagnosticCodes.cs"),
            Path.Combine(root, "tooling", "authoring", "src", "Talewright.Authoring", "AuthoringDiagnosticCodes.cs"),
            Path.Combine(root, "tooling", "language-server", "src", "Talewright.LanguageServer", "LanguageServerDiagnosticCodes.cs"),
            Path.Combine(root, "tooling", "language-server", "src", "Talewright.LanguageServer", "LanguageServerLogCodes.cs"),
            Path.Combine(root, "tooling", "cli", "src", "Talewright.Cli", "CliDiagnosticCodes.cs"),
        };
        var sourceRoots = new[]
        {
            Path.Combine(root, "engine", "src", "Talewright.Talemark"),
            Path.Combine(root, "tooling", "authoring", "src", "Talewright.Authoring"),
            Path.Combine(root, "tooling", "language-server", "src", "Talewright.LanguageServer"),
            Path.Combine(root, "tooling", "cli", "src", "Talewright.Cli"),
        };
        var failures = new List<string>();
        foreach (var sourceRoot in sourceRoots)
        {
            foreach (var path in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
            {
                var parts = Path.GetRelativePath(sourceRoot, path)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (parts.Any(part => part is "bin" or "obj"))
                {
                    continue;
                }

                if (catalogFiles.Contains(path))
                {
                    continue;
                }

                foreach (Match match in CodeLiteralPattern.Matches(File.ReadAllText(path)))
                {
                    failures.Add($"{Path.GetRelativePath(root, path)}: stray code literal {match.Groups["code"].Value}.");
                }
            }
        }

        Assert.Empty(failures);
    }

    private static string[] ReadCodes(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

    private static string ReadRepositoryFile(string root, string relativePath) =>
        File.ReadAllText(Path.Combine(root, relativePath));

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Talewright.slnx")))
        {
            root = root.Parent;
        }
        return root?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
