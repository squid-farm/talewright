using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Talewright.Narrative.Templates;
using Talewright.Talemark.Compilation;

namespace Talewright.LanguageServer.Tests;

/// <summary>
/// Grammar text becomes a typed value once, in <c>Talewright.Talemark.Vocabulary</c>. Production code must not compare
/// against or emit hand-typed copies of those spellings.
/// </summary>
public sealed class TalemarkVocabularyGuardTests
{
    private const string VocabularyNamespace = "Talewright.Talemark.Vocabulary";

    // Files that still own grammar text until the issue that moves it lands. Each must still contain a stray spelling,
    // so finished migrations remove their exemption.
    private static readonly Dictionary<string, string> PendingFiles = new(StringComparer.Ordinal)
    {
        ["engine/src/Talewright.Talemark/Analysis/StoryAnalysis.cs"] =
            "Manifest symbol types are a separate vocabulary owned in #474.",
        ["engine/src/Talewright.Talemark/Compilation/StorySource.cs"] =
            "Manifest keys are a separate vocabulary owned in #474.",
        ["engine/src/Talewright.Talemark/Parsing/StoryYamlReader.cs"] =
            "Manifest keys are a separate vocabulary owned in #474.",
        ["engine/src/Talewright.Talemark/Parsing/StoryYamlWorldReader.cs"] = "Manifest keys; owned in #474.",
        ["engine/src/Talewright.Talemark/Parsing/StoryYamlRulesetReader.cs"] = "Manifest keys; owned in #474.",
        ["tooling/authoring/src/Talewright.Authoring/ManifestListEditor.cs"] = "YAML scalars; owned in #474.",
    };

    // Single punctuation characters are also paths, URIs and Markdown, so they are enforced only where the
    // expression language is parsed, bound and printed.
    private static readonly string[] ExpressionFiles =
    [
        "engine/src/Talewright.Talemark/Parsing/Lexer.cs",
        "engine/src/Talewright.Talemark/Parsing/QuotedStringSyntax.cs",
        "engine/src/Talewright.Talemark/Parsing/ExpressionSyntax.cs",
        "engine/src/Talewright.Talemark/Parsing/ExpressionSyntaxParser.cs",
        "engine/src/Talewright.Talemark/Parsing/ExpressionBinder.cs",
        "engine/src/Talewright.Talemark/Parsing/ExpressionParser.cs",
        "engine/src/Talewright.Talemark/Parsing/ImageSyntax.cs",
        "engine/src/Talewright.Talemark/Parsing/ChoiceSyntax.cs",
        "engine/src/Talewright.Talemark/Formatting/ExpressionFormatter.cs",
        "engine/src/Talewright.Talemark/Analysis/CompletionSite.cs",
        "engine/src/Talewright.Talemark/Vocabulary/AuthoringSyntaxVocabulary.cs",
        "tooling/authoring/src/Talewright.Authoring/StoryCompletion.cs",
        "tooling/authoring/src/Talewright.Authoring/StoryPreview.cs",
    ];

    private static readonly string[] LineMarkerFiles =
    [
        "engine/src/Talewright.Talemark/Parsing/TaleBinder.cs",
        "engine/src/Talewright.Talemark/Parsing/ChoiceSyntax.cs",
        "engine/src/Talewright.Talemark/Parsing/DelimiterScanner.cs",
        "engine/src/Talewright.Talemark/Parsing/ImageSyntax.cs",
        "engine/src/Talewright.Talemark/Parsing/InlineParser.cs",
    ];

    private static readonly string[] SourceRoots =
    [
        "engine/src/Talewright.Talemark",
        "tooling/authoring/src/Talewright.Authoring",
        "tooling/language-server/src/Talewright.LanguageServer",
    ];

    [Fact]
    public void Production_code_uses_vocabulary_names_instead_of_grammar_spellings()
    {
        var root = RepositoryRoot();
        var spellings = Spellings();
        var (words, punctuation, lineMarkers) = spellings;
        Assert.Contains("and", words);
        Assert.Contains("+=", words);
        Assert.Contains("var:", words);
        Assert.Contains("===", words);
        Assert.Contains("->", words);
        Assert.Contains("(requires", words);
        Assert.Contains("skill-check", words);
        Assert.Contains("one-many", words);
        Assert.Contains("capitalize", words);
        Assert.Contains("a", punctuation);
        Assert.Contains("#", punctuation);
        Assert.Contains("\\", punctuation);
        Assert.Contains("\"", punctuation);
        Assert.Contains("@", lineMarkers);
        Assert.Contains("{", lineMarkers);
        Assert.Contains("}", lineMarkers);
        Assert.Contains("[", lineMarkers);
        Assert.Contains("]", lineMarkers);
        Assert.Contains("!", lineMarkers);
        Assert.Contains("_", lineMarkers);
        Assert.Contains("\\", lineMarkers);
        Assert.Contains("\"", lineMarkers);

        var expressionFiles = ExpressionFiles.Select(path => Path.Combine(root, path)).ToHashSet(StringComparer.Ordinal);
        var lineMarkerFiles = LineMarkerFiles.Select(path => Path.Combine(root, path)).ToHashSet(StringComparer.Ordinal);
        Assert.All(expressionFiles, path => Assert.True(File.Exists(path), $"{path} no longer exists."));
        Assert.All(lineMarkerFiles, path => Assert.True(File.Exists(path), $"{path} no longer exists."));
        var failures = new List<string>();
        var pendingWithHits = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in SourceRoots.SelectMany(sourceRoot => SourceFiles(Path.Combine(root, sourceRoot))))
        {
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            var checkPunctuation = expressionFiles.Contains(path);
            var checkLineMarker = lineMarkerFiles.Contains(path);
            foreach (var (line, value) in Literals(File.ReadAllText(path)))
            {
                if (!IsGuardedLiteral(value, spellings, checkPunctuation, checkLineMarker)) continue;
                if (PendingFiles.ContainsKey(relative))
                {
                    pendingWithHits.Add(relative);
                    continue;
                }

                failures.Add($"{relative}:{line}: \"{value}\" is a Talemark spelling; use {VocabularyNamespace}.");
            }
        }

        failures.AddRange(PendingFiles.Keys.Except(pendingWithHits).Select(path => $"{path} no longer needs its exemption."));
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Literal_scanner_checks_decoded_values_but_ignores_comments_and_interpolated_text()
    {
        const string source = """"
            // "and"
            var a = "and"; var b = ':'; var c = $"{x} and"; var d = @"or"; var raw = """and""";
            var e = "Expected ')' here"; /* "not" */ var f = '\'';
            var g = $"{(a ? "x" : "y")} {{"; var h = "\u0061nd"; var i = "\x6Fr";
            var j = """
                not
                """;
            """";

        Assert.Equal(
            [(2, "and"), (2, ":"), (2, "or"), (2, "and"), (3, "Expected ')' here"), (3, "'"),
                (4, "x"), (4, "y"), (4, "and"), (4, "or"), (5, "not")],
            Literals(source));
    }

    [Theory]
    [InlineData("""var character = '\\'; var text = "\\"; var unicode = "\u005c";""", "\\")]
    [InlineData("""var character = '"'; var text = "\""; var unicode = "\u0022";""", "\"")]
    public void Text_delimiter_literals_are_guarded_in_talemark_syntax_files(string source, string spelling)
    {
        var literals = Literals(source);
        var spellings = Spellings();
        Assert.Equal(3, literals.Count);
        Assert.All(literals, literal =>
        {
            Assert.Equal(spelling, literal.Value);
            Assert.True(IsGuardedLiteral(literal.Value, spellings, checkPunctuation: true, checkLineMarker: false));
            Assert.True(IsGuardedLiteral(literal.Value, spellings, checkPunctuation: false, checkLineMarker: true));
            Assert.False(IsGuardedLiteral(literal.Value, spellings, checkPunctuation: false, checkLineMarker: false));
        });
    }

    private static bool IsGuardedLiteral(string value,
        (HashSet<string> Words, HashSet<string> Punctuation, HashSet<string> LineMarkers) spellings,
        bool checkPunctuation, bool checkLineMarker) =>
        spellings.Words.Contains(value) ||
        (checkPunctuation && spellings.Punctuation.Contains(value)) ||
        (checkLineMarker && spellings.LineMarkers.Contains(value));

    private static (HashSet<string> Words, HashSet<string> Punctuation, HashSet<string> LineMarkers) Spellings()
    {
        var vocabularyTypes = typeof(StoryCompiler).Assembly.GetTypes()
            .Where(type => type.Namespace == VocabularyNamespace)
            .ToArray();
        var values = vocabularyTypes
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .Select(field => field.IsLiteral ? field.GetRawConstantValue() : field.IsInitOnly ? field.GetValue(null) : null)
            .Select(value => value switch { string text => text, char character => character.ToString(), _ => null })
            .OfType<string>()
            .Concat(TextModifierRegistry.CreateDefault().Names)
            .Append(OneManyTextModifier.ModifierName)
            .ToArray();
        var lineMarkers = vocabularyTypes
            .Where(type => type.Name is "SceneVocabulary" or "TextVocabulary")
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .Where(field => field.IsLiteral && field.FieldType == typeof(char))
            .Select(field => ((char)field.GetRawConstantValue()!).ToString())
            .ToHashSet(StringComparer.Ordinal);
        return (values.Where(value => value.Length > 1).ToHashSet(StringComparer.Ordinal),
            values.Where(value => value.Length == 1).ToHashSet(StringComparer.Ordinal), lineMarkers);
    }

    private static IEnumerable<string> SourceFiles(string sourceRoot) =>
        Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                var parts = Path.GetRelativePath(sourceRoot, path)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var definition = parts.Contains("Vocabulary") && Path.GetFileName(path) is
                    "ExpressionVocabulary.cs" or "SceneVocabulary.cs" or "TextVocabulary.cs";
                return !definition && !parts.Any(part => part is "bin" or "obj");
            });

    /// <summary>Decoded values of every string and character literal token, as the C# compiler sees them.</summary>
    /// <remarks>Comments are trivia and interpolated-string text is not a literal token, so neither is reported.</remarks>
    private static List<(int Line, string Value)> Literals(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        return tree.GetRoot().DescendantTokens()
            .Where(token => token.Kind() is SyntaxKind.StringLiteralToken or SyntaxKind.Utf8StringLiteralToken
                or SyntaxKind.CharacterLiteralToken or SyntaxKind.SingleLineRawStringLiteralToken
                or SyntaxKind.MultiLineRawStringLiteralToken or SyntaxKind.Utf8SingleLineRawStringLiteralToken
                or SyntaxKind.Utf8MultiLineRawStringLiteralToken)
            .Select(token => (token.GetLocation().GetLineSpan().StartLinePosition.Line + 1, token.ValueText))
            .ToList();
    }

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
