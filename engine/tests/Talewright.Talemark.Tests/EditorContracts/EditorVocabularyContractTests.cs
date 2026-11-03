using System.Reflection;
using System.Text.Json;
using Talewright.Narrative.Templates;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Talemark.Tests;

public sealed class EditorVocabularyContractTests
{
    [Trait("Category", "EditorContract")]
    [Fact]
    public void Shared_editor_cases_cover_owned_spellings_and_the_syntax_reference()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Talewright.slnx")))
            root = root.Parent;
        Assert.NotNull(root);

        var reference = File.ReadAllText(Path.Combine(root.FullName, "docs", "reference", "talemark-syntax.md"));
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName,
            "tooling", "vscode-talewright", "tests", "fixtures", "vocabulary.json")));
        var types = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["expression"] = typeof(ExpressionVocabulary),
            ["scene"] = typeof(SceneVocabulary),
            ["text"] = typeof(TextVocabulary),
        };
        var seen = types.Keys.Append("modifier").ToDictionary(owner => owner,
            _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);

        foreach (var item in fixture.RootElement.EnumerateArray())
        {
            var owner = item.GetProperty("owner").GetString()!;
            var member = item.GetProperty("member").GetString()!;
            var spelling = item.GetProperty("spelling").GetString()!;
            Assert.True(seen.TryGetValue(owner, out var members), $"Unknown vocabulary owner '{owner}'.");
            Assert.True(members!.Add(member), $"Duplicate contract for {owner}.{member}.");

            if (owner == "modifier")
            {
                Assert.Equal(member, spelling);
            }
            else
            {
                var field = types[owner].GetField(member, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                Assert.True(field is { IsLiteral: true }, $"Missing vocabulary constant {owner}.{member}.");
                var value = field!.GetRawConstantValue() switch
                {
                    string text => text,
                    char character => character.ToString(),
                    _ => throw new InvalidOperationException($"{owner}.{member} is not a text spelling."),
                };
                Assert.Equal(value, spelling);
            }

            var example = item.GetProperty("line").GetString()!;
            var documented = item.GetProperty("reference").GetString()!;
            Assert.Contains(spelling, example, StringComparison.Ordinal);
            Assert.Contains(spelling, documented, StringComparison.Ordinal);
            Assert.Contains(documented, reference, StringComparison.Ordinal);
        }

        Assert.Equal(Constants(typeof(ExpressionVocabulary)), seen["expression"].Order(StringComparer.Ordinal));
        Assert.Equal(Constants(typeof(TextVocabulary)), seen["text"].Order(StringComparer.Ordinal));

        // These are derived fragments or characters used only by prose and legacy Markdown, not standalone syntax.
        var sceneExemptions = new[]
        {
            nameof(SceneVocabulary.DifficultyPrefix), nameof(SceneVocabulary.DifficultySeparator),
            nameof(SceneVocabulary.IfPrefix), nameof(SceneVocabulary.ChoiceProseQuote),
            nameof(SceneVocabulary.LegacyImageEscape), nameof(SceneVocabulary.LegacyImageQuote),
        };
        Assert.Equal(Constants(typeof(SceneVocabulary)).Except(sceneExemptions).Order(StringComparer.Ordinal),
            seen["scene"].Order(StringComparer.Ordinal));
        Assert.Equal(TextModifierRegistry.CreateDefault().Names.Append(OneManyTextModifier.ModifierName)
                .Order(StringComparer.Ordinal),
            seen["modifier"].Order(StringComparer.Ordinal));
    }

    private static IEnumerable<string> Constants(Type type) =>
        type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => field.IsLiteral && (field.FieldType == typeof(string) || field.FieldType == typeof(char)))
            .Select(field => field.Name)
            .Order(StringComparer.Ordinal);
}
