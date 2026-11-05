using Talewright.Narrative.Templates;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Talemark.Tests;

public sealed class AuthoringSyntaxVocabularyTests
{
    [Fact]
    public void Every_recognized_authoring_kind_has_spelling_and_help()
    {
        foreach (var kind in Enum.GetValues<AuthoringTokenKind>())
        {
            var found = AuthoringSyntaxVocabulary.TryGetHelp(kind, out var help);
            if (kind is AuthoringTokenKind.Other or AuthoringTokenKind.ManifestProperty)
            {
                Assert.False(found);
                continue;
            }

            Assert.True(found, $"{kind} has no syntax help.");
            Assert.NotEmpty(help.Spelling);
            Assert.NotEmpty(help.Description);
        }
    }

    [Theory]
    [InlineData("flag:", AuthoringTokenKind.Flag)]
    [InlineData("var:", AuthoringTokenKind.Variable)]
    [InlineData("faction:", AuthoringTokenKind.Faction)]
    [InlineData("item:", AuthoringTokenKind.Item)]
    [InlineData("scene:", AuthoringTokenKind.SceneContext)]
    [InlineData("player:", AuthoringTokenKind.Visits)]
    [InlineData("image(", AuthoringTokenKind.Image)]
    public void Completion_prefixes_share_their_syntax_entry(string completion, AuthoringTokenKind kind)
    {
        Assert.True(AuthoringSyntaxVocabulary.TryGetHelp(kind, out var expected));
        Assert.True(AuthoringSyntaxVocabulary.TryGetKeywordHelp(completion, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Help_examples_use_owned_syntax_and_modifier_names()
    {
        var separator = ExpressionVocabulary.Separator;
        var argumentSeparator = ExpressionVocabulary.ArgumentSeparator;
        var modifier = ExpressionVocabulary.ModifierMarker;
        var quote = TextVocabulary.QuoteMark;
        var oneMany = $"{modifier}{OneManyTextModifier.ModifierName}{ExpressionVocabulary.OpenParen}" +
                      $"{quote}singular{quote}{argumentSeparator} {quote}plural{quote}{ExpressionVocabulary.CloseParen}";
        var examples = new (AuthoringTokenKind Kind, string Example)[]
        {
            (AuthoringTokenKind.SceneHeader, $"{SceneVocabulary.SceneHeader} scene.id"),
            (AuthoringTokenKind.Divert, SceneVocabulary.End),
            (AuthoringTokenKind.Hub, $"camp {ExpressionVocabulary.And} night"),
            (AuthoringTokenKind.Choice, $"{SceneVocabulary.ChoiceMarker} {SceneVocabulary.Divert} target"),
            (AuthoringTokenKind.SkillCheckBlock, $"{SceneVocabulary.SkillCheckBlock} {SceneVocabulary.OpenBracket}actor{SceneVocabulary.CloseBracket} stat {SceneVocabulary.Difficulty} number"),
            (AuthoringTokenKind.If, $"{SceneVocabulary.InlineOpen}{SceneVocabulary.EndIf}{SceneVocabulary.InlineClose}"),
            (AuthoringTokenKind.Visited, $"{ExpressionVocabulary.Player} {ExpressionVocabulary.Visited} scene"),
            (AuthoringTokenKind.SceneContext, $"{ExpressionVocabulary.ScenePrefix}{ExpressionVocabulary.Player}:{ExpressionVocabulary.Visits}"),
            (AuthoringTokenKind.Visits, ExpressionVocabulary.Visits),
            (AuthoringTokenKind.Variable, $"{ExpressionVocabulary.VarPrefix}id"),
            (AuthoringTokenKind.Faction, $"{ExpressionVocabulary.FactionPrefix}id"),
            (AuthoringTokenKind.Faction, $"{ExpressionVocabulary.FactionPrefix}id{separator}{ExpressionVocabulary.Name}"),
            (AuthoringTokenKind.Faction, $"{ExpressionVocabulary.FactionPrefix}id{separator}{ExpressionVocabulary.Standing}"),
            (AuthoringTokenKind.Faction, $"{ExpressionVocabulary.FactionPrefix}id{separator}{ExpressionVocabulary.Tier}"),
            (AuthoringTokenKind.Standing, ExpressionVocabulary.Standing),
            (AuthoringTokenKind.Item, $"{ExpressionVocabulary.ItemPrefix}id{separator}{ExpressionVocabulary.Name}"),
            (AuthoringTokenKind.Item, $"{modifier}{TextModifierRegistry.IndefiniteArticleName}"),
            (AuthoringTokenKind.Item, $"{modifier}{TextModifierRegistry.DefiniteArticleName}"),
            (AuthoringTokenKind.Item, $"{modifier}{TextModifierRegistry.ThisName}"),
            (AuthoringTokenKind.Item, $"{modifier}{TextModifierRegistry.ThatName}"),
            (AuthoringTokenKind.Item, $"{modifier}{TextModifierRegistry.PossessiveName}"),
            (AuthoringTokenKind.Image, $"{ExpressionVocabulary.ImageCall}{quote}path{quote}"),
            (AuthoringTokenKind.Set, $"{ExpressionVocabulary.Set} {ExpressionVocabulary.FlagPrefix}id"),
            (AuthoringTokenKind.Pronoun, $"{separator}{ExpressionVocabulary.PossessivePronoun}{separator}{ExpressionVocabulary.Determiner}"),
            (AuthoringTokenKind.Pronoun, oneMany),
            (AuthoringTokenKind.OneMany, oneMany),
        };
        foreach (var (kind, example) in examples)
        {
            Assert.True(AuthoringSyntaxVocabulary.TryGetHelp(kind, out var help));
            Assert.Contains(example, help.Description, StringComparison.Ordinal);
        }

        Assert.True(AuthoringSyntaxVocabulary.TryGetHelp(AuthoringTokenKind.Choice, out var choice));
        Assert.Equal("Choice: * [label] with optional conditions and effects, followed by a divert or skill check. Omit the label with * -> target to show Continue.", choice.Description);
    }

    [Theory]
    [InlineData("flag:", "flag", true)]
    [InlineData("scene:", "scene", true)]
    [InlineData("pronoun", "pronoun", true)]
    [InlineData("flag:", "var", false)]
    public void Completion_spelling_matches_the_undelimited_source_token(string spelling, string token, bool expected) =>
        Assert.Equal(expected, AuthoringSyntaxVocabulary.CompletionMatchesToken(spelling, token));

    [Fact]
    public void Formatted_authoring_syntax_keeps_existing_output()
    {
        Assert.Equal("#upper", AuthoringSyntaxVocabulary.FormatModifier("upper"));
        Assert.Equal("camp and records", AuthoringSyntaxVocabulary.FormatHubSelector(["camp", "records"], true));
        Assert.Equal("camp or records", AuthoringSyntaxVocabulary.FormatHubSelector(["camp", "records"], false));
        Assert.Equal("camp", AuthoringSyntaxVocabulary.FormatHubSelector(["camp"], true));
        Assert.Equal([":", "@", "#", ">", "(", "\"", "/"], AuthoringSyntaxVocabulary.CompletionTriggerCharacters);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("plain", "plain")]
    [InlineData("a\"b", "a\\\"b")]
    [InlineData("a\\b", "a\\\\b")]
    [InlineData("\\\"", "\\\\\\\"")]
    public void Quoted_completion_content_uses_the_shared_string_escape_rules(string text, string expected) =>
        Assert.Equal(expected, AuthoringSyntaxVocabulary.EscapeQuotedText(text));

    [Fact]
    public void Parsed_tokens_distinguish_syntax_that_shares_a_character()
    {
        const string tale = """
            === start
            > player: Hello.
            ~ var:count = 2 * 3
            {if var:count > 0}Yes{else}No{/if}
            * [Next] -> END
            """;
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = """
                game: {id: token-kinds, title: Token kinds, start: start}
                characters: {player: {first-name: Ada}}
                vars: {count: 0}
                """,
            ["main.tale"] = tale,
        });
        var tokens = StoryAnalyzer.Analyze(source).Tokens;

        var choice = Assert.Single(tokens, token => token.Kind == AuthoringTokenKind.Choice);
        var multiply = Assert.Single(tokens, token => token.Kind == AuthoringTokenKind.Multiply);
        var speech = Assert.Single(tokens, token => token.Kind == AuthoringTokenKind.Speech);
        var greater = Assert.Single(tokens, token => token.Kind == AuthoringTokenKind.Greater);
        Assert.Equal("*", choice.Text);
        Assert.Equal(choice.Text, multiply.Text);
        Assert.Equal(">", speech.Text);
        Assert.Equal(speech.Text, greater.Text);
        Assert.Equal(5, choice.Range.Start.Line);
        Assert.Equal(3, multiply.Range.Start.Line);
        Assert.Equal(2, speech.Range.Start.Line);
        Assert.Equal(4, greater.Range.Start.Line);
        Assert.Contains(tokens, token => token.Kind == AuthoringTokenKind.If);
        Assert.Contains(tokens, token => token.Kind == AuthoringTokenKind.Else);
        Assert.Contains(tokens, token => token.Kind == AuthoringTokenKind.EndIf);
        Assert.Contains(tokens, token => token.Kind == AuthoringTokenKind.ManifestProperty &&
            token.ManifestPath is ["game", "start"]);
        Assert.Contains(tokens, token => token.Kind == AuthoringTokenKind.Other && token.Text == "count");
    }
}
