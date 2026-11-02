using System.Reflection;
using System.Text;
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
        ["tooling/authoring/src/Talewright.Authoring/StoryHover.cs"] =
            "Hover help keyed by spelling; sourced from the vocabulary in #471 part 3.",
        ["engine/src/Talewright.Talemark/Analysis/StoryAnalysis.cs"] =
            "Authoring symbol type labels; typed with authoring tokens in #471 part 3.",
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
        "engine/src/Talewright.Talemark/Formatting/ExpressionFormatter.cs",
        "engine/src/Talewright.Talemark/Analysis/CompletionSite.cs",
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
        var (words, punctuation) = Spellings();
        Assert.Contains("and", words);
        Assert.Contains("+=", words);
        Assert.Contains("var:", words);
        Assert.Contains("#", punctuation);

        var expressionFiles = ExpressionFiles.Select(path => Path.Combine(root, path)).ToHashSet(StringComparer.Ordinal);
        Assert.All(expressionFiles, path => Assert.True(File.Exists(path), $"{path} no longer exists."));
        var failures = new List<string>();
        var pendingWithHits = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in SourceRoots.SelectMany(sourceRoot => SourceFiles(Path.Combine(root, sourceRoot))))
        {
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            var checkPunctuation = expressionFiles.Contains(path);
            foreach (var (line, value) in Literals(File.ReadAllText(path)))
            {
                if (!words.Contains(value) && !(checkPunctuation && punctuation.Contains(value))) continue;
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
    public void Literal_scanner_checks_verbatim_and_raw_values_but_ignores_comments_and_interpolation()
    {
        const string source = """"
            // "and"
            var a = "and"; var b = ':'; var c = $"{x} and"; var d = @"or"; var raw = """and""";
            var e = "Expected ')' here"; /* "not" */ var f = '\'';
            var verbatimInterpolated = $@"{(a ? "x" : "y")} {{";
            var ordinaryInterpolated = $"{(a ? "x" : "y")} {{"; var h = "or";
            """";

        Assert.Equal([(2, "and"), (2, ":"), (2, "or"), (2, "and"), (3, "Expected ')' here"), (3, "'"), (5, "or")], Literals(source));
    }

    private static (HashSet<string> Words, HashSet<string> Punctuation) Spellings()
    {
        var values = typeof(StoryCompiler).Assembly.GetTypes()
            .Where(type => type.Namespace == VocabularyNamespace)
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .Select(field => field.IsLiteral ? field.GetRawConstantValue() : field.IsInitOnly ? field.GetValue(null) : null)
            .Select(value => value switch { string text => text, char character => character.ToString(), _ => null })
            .OfType<string>()
            .ToArray();
        return (values.Where(value => value.Length > 1).ToHashSet(StringComparer.Ordinal),
            values.Where(value => value.Length == 1).ToHashSet(StringComparer.Ordinal));
    }

    private static IEnumerable<string> SourceFiles(string sourceRoot) =>
        Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(sourceRoot, path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "bin" or "obj" or "Vocabulary"));

    /// <summary>Values of regular, verbatim, raw and character literals, skipping comments and interpolated strings.</summary>
    private static List<(int Line, string Value)> Literals(string source)
    {
        var literals = new List<(int, string)>();
        var line = 1;
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            if (c == '\n')
            {
                line++;
                i++;
            }
            else if (c == '/' && At(i + 1) == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
            }
            else if (c == '/' && At(i + 1) == '*')
            {
                i += 2;
                while (i < source.Length && !(source[i] == '*' && At(i + 1) == '/')) line += source[i++] == '\n' ? 1 : 0;
                i += 2;
            }
            else if (c == '@' && At(i + 1) == '"')
            {
                var start = line;
                var (value, end) = ReadVerbatim(i);
                literals.Add((start, value));
                i = end;
            }
            else if (c is '$' or '@')
            {
                i = SkipSpecialString(i);
            }
            else if (c == '"' && At(i + 1) == '"' && At(i + 2) == '"')
            {
                var start = line;
                var (value, end) = ReadRaw(i);
                literals.Add((start, value));
                i = end;
            }
            else if (c is '"' or '\'')
            {
                var start = line;
                var (value, end) = ReadQuoted(i, c);
                literals.Add((start, value));
                i = end;
            }
            else
            {
                i++;
            }
        }

        return literals;

        char At(int index) => index < source.Length ? source[index] : '\0';

        (string Value, int End) ReadVerbatim(int start)
        {
            var value = new StringBuilder();
            var index = start + 2;
            while (index < source.Length)
            {
                if (source[index] == '"' && At(index + 1) == '"')
                {
                    value.Append('"');
                    index += 2;
                }
                else if (source[index] == '"')
                {
                    return (value.ToString(), index + 1);
                }
                else
                {
                    if (source[index] == '\n') line++;
                    value.Append(source[index++]);
                }
            }

            return (value.ToString(), index);
        }

        (string Value, int End) ReadRaw(int start)
        {
            var delimiterLength = 0;
            while (At(start + delimiterLength) == '"') delimiterLength++;
            var contentStart = start + delimiterLength;
            for (var index = contentStart; index < source.Length;)
            {
                if (source[index] != '"')
                {
                    if (source[index++] == '\n') line++;
                    continue;
                }

                var quoteCount = 0;
                while (At(index + quoteCount) == '"') quoteCount++;
                if (quoteCount >= delimiterLength)
                {
                    return (source[contentStart..index], index + delimiterLength);
                }

                index += quoteCount;
            }

            return (source[contentStart..], source.Length);
        }

        (string Value, int End) ReadQuoted(int start, char quote)
        {
            var value = new StringBuilder();
            var index = start + 1;
            while (index < source.Length && source[index] != quote && source[index] != '\n')
            {
                if (source[index] == '\\' && index + 1 < source.Length)
                {
                    value.Append(source[index + 1] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '0' => '\0', var other => other });
                    index += 2;
                }
                else
                {
                    value.Append(source[index++]);
                }
            }

            return (value.ToString(), index + 1);
        }

        int SkipSpecialString(int start)
        {
            var index = start;
            var verbatim = false;
            while (index < source.Length && source[index] is '$' or '@')
            {
                verbatim |= source[index] == '@';
                index++;
            }

            if (index >= source.Length || source[index] != '"') return index;
            var quotes = 0;
            while (index < source.Length && source[index] == '"')
            {
                quotes++;
                index++;
            }

            if (quotes >= 3)
            {
                var close = source.IndexOf(new string('"', quotes), index, StringComparison.Ordinal);
                var stop = close < 0 ? source.Length : close + quotes;
                line += source.AsSpan(index, stop - index).Count('\n');
                return stop;
            }

            if (quotes == 2) return index;
            var interpolated = source[start] == '$' || At(start + 1) == '$';
            var depth = 0;
            while (index < source.Length)
            {
                var current = source[index];
                if (current == '\n') line++;
                if (interpolated && current == '{' && depth == 0 && At(index + 1) == '{') index += 2;
                else if (interpolated && current == '{') { depth++; index++; }
                else if (depth > 0 && current == '}') { depth--; index++; }
                else if (depth > 0 && current is '"' or '\'' or '$' or '@')
                    index = current is '"' or '\'' ? ReadQuoted(index, current).End : SkipSpecialString(index);
                else if (depth > 0) index++;
                else if (!verbatim && current == '\\') index += 2;
                else if (current == '"' && verbatim && At(index + 1) == '"') index += 2;
                else if (current == '"') return index + 1;
                else index++;
            }

            return index;
        }
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
