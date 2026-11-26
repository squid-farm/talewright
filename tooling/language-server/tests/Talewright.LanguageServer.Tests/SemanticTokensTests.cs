using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;

namespace Talewright.LanguageServer.Tests;

public sealed partial class ServerTests
{
    private readonly record struct DecodedToken(int Line, int Start, int Length, string Type, int Modifiers, string Text);

    [Fact]
    public async Task Structural_tokens_keep_themeable_roles_including_unsaved_incomplete_diverts()
    {
        const string tale = """
            === start
            @title The End
            Plain narrative.
            * [Go] -> ending
            {player:first-name#upper}
            \{literal\} **bold**
            === ending
            -> END
            ->
            """;
        await using var client = new Client();
        var initialized = await client.Initialize();
        await client.Open("start.tale", tale, 1);
        var response = await client.Request("textDocument/semanticTokens/full", new
        {
            textDocument = new { uri = client.UriOf("start.tale") }
        });
        Assert.False(response.TryGetProperty("error", out _));
        var data = response.GetProperty("result").GetProperty("data")
            .EnumerateArray().Select(value => value.GetInt32()).ToArray();
        var legend = initialized.GetProperty("result").GetProperty("capabilities")
            .GetProperty("semanticTokensProvider").GetProperty("legend").GetProperty("tokenTypes")
            .EnumerateArray().Select(value => value.GetString()!).ToArray();
        var tokens = Decode(data, legend, tale);

        Assert.Contains(new DecodedToken(0, 4, 5, "talemarkScene", 1, "start"), tokens);
        Assert.Contains(new DecodedToken(1, 7, 7, "string", 0, "The End"), tokens);
        Assert.Contains(new DecodedToken(3, 0, 1, "talemarkStructure", 0, "*"), tokens);
        Assert.Contains(new DecodedToken(3, 2, 1, "talemarkPunctuation", 0, "["), tokens);
        Assert.Contains(new DecodedToken(3, 7, 2, "talemarkDivert", 0, "->"), tokens);
        Assert.Contains(new DecodedToken(3, 10, 6, "talemarkScene", 0, "ending"), tokens);
        Assert.Contains(new DecodedToken(4, 19, 5, "modifier", 0, "upper"), tokens);
        Assert.Contains(new DecodedToken(5, 0, 2, "talemarkEscape", 0, "\\{"), tokens);
        Assert.Contains(new DecodedToken(5, 12, 2, "talemarkFormatting", 0, "**"), tokens);
        Assert.Contains(new DecodedToken(7, 3, 3, "keyword", 0, "END"), tokens);
        Assert.Contains(new DecodedToken(8, 0, 2, "talemarkDivert", 0, "->"), tokens);
        Assert.DoesNotContain(tokens, token => token.Line == 2);
    }

    [Fact]
    public async Task Inventory_references_highlight_the_namespace_item_and_count_roles()
    {
        const string tale = """
            === start
            {player:inventory:first-name:count}
            {guide:inventory:first-name:count}
            {player:inventory:sword:sharpness}
            -> END
            """;
        await using var client = new Client();
        File.WriteAllText(client.PathOf(StorySource.ManifestFileName), """
            game: { id: semantic-items, title: Semantic items, start: start, talemark: 2 }
            characters:
              player: { first-name: Ada }
              guide: { first-name: Bea }
            items:
              first-name: { name: Token }
              sword: { name: Sword, sharpness: 5 }
            """);
        var initialized = await client.Initialize();
        await client.Open("start.tale", tale, 1);
        var response = await client.Request("textDocument/semanticTokens/full", new
        {
            textDocument = new { uri = client.UriOf("start.tale") }
        });
        var data = response.GetProperty("result").GetProperty("data")
            .EnumerateArray().Select(value => value.GetInt32()).ToArray();
        var legend = initialized.GetProperty("result").GetProperty("capabilities")
            .GetProperty("semanticTokensProvider").GetProperty("legend").GetProperty("tokenTypes")
            .EnumerateArray().Select(value => value.GetString()!).ToArray();
        var tokens = Decode(data, legend, tale);

        Assert.Equal(3, tokens.Count(token => token.Text == "inventory" && token.Type == "keyword"));
        Assert.Equal(2, tokens.Count(token => token.Text == "first-name" && token.Type == "variable"));
        Assert.Equal(2, tokens.Count(token => token.Text == "count" && token.Type == "property"));
        Assert.Contains(tokens, token => token.Text == "sharpness" && token.Type == "property");
    }

    [Theory]
    [InlineData("item")]
    [InlineData("flag")]
    [InlineData("var")]
    [InlineData("faction")]
    [InlineData("scene")]
    public async Task Inventory_count_stays_a_property_when_the_item_id_is_a_reference_prefix(string itemId)
    {
        var tale = $$"""
            === start
            {player:inventory:{{itemId}}:count}
            -> END
            """;
        await using var client = new Client();
        var initialized = await client.Initialize();
        await client.Open("start.tale", tale, 1);
        var response = await client.Request("textDocument/semanticTokens/full", new
        {
            textDocument = new { uri = client.UriOf("start.tale") }
        });
        var data = response.GetProperty("result").GetProperty("data")
            .EnumerateArray().Select(value => value.GetInt32()).ToArray();
        var legend = initialized.GetProperty("result").GetProperty("capabilities")
            .GetProperty("semanticTokensProvider").GetProperty("legend").GetProperty("tokenTypes")
            .EnumerateArray().Select(value => value.GetString()!).ToArray();
        var tokens = Decode(data, legend, tale);

        Assert.Contains(tokens, token => token.Text == "inventory" && token.Type == "keyword");
        Assert.Contains(tokens, token => token.Text == itemId && token.Type == "variable");
        Assert.Contains(tokens, token => token.Text == "count" && token.Type == "property");
    }

    [Fact]
    public async Task Full_semantic_tokens_match_classifier_ranges_in_utf16_delta_encoding()
    {
        const string tale = """
            // comment
            === start
            @title "Start"
            @priority 10
            @requires player:strength >= 10 and @CanPickLock
            {if true}
            😀 {player:first-name#upper} Ready.
            word*inner*word
            **bold**text
            __snake__case__
            *a **b* c**
            {else}
            {/if}
            -> END
            """;

        await using var client = new Client();
        var initialized = await client.Initialize();
        await client.Open("start.tale", tale, 1);
        var response = await client.Request("textDocument/semanticTokens/full", new
        {
            textDocument = new { uri = client.UriOf("start.tale") }
        });

        Assert.False(response.TryGetProperty("error", out _));
        var data = response.GetProperty("result").GetProperty("data")
            .EnumerateArray().Select(value => value.GetInt32()).ToArray();
        Assert.Equal(0, data.Length % 5);

        var legend = initialized.GetProperty("result").GetProperty("capabilities")
            .GetProperty("semanticTokensProvider").GetProperty("legend").GetProperty("tokenTypes")
            .EnumerateArray().Select(value => value.GetString()!).ToArray();
        var actual = Decode(data, legend, tale);
        var expected = TalemarkClassifier.Classify("start.tale", tale)
            .Select(classification =>
            {
                var range = classification.Range;
                var line = range.Start.Line - 1;
                var start = range.Start.Column - 1;
                var length = range.End.Column - range.Start.Column;
                var text = tale.ReplaceLineEndings("\n").Split('\n')[line].Substring(start, length);
                return new DecodedToken(line, start, length, ExpectedType(classification.Kind),
                    ExpectedModifiers(classification.Modifiers), text);
            }).ToArray();
        Assert.Equal(expected, actual);

        Assert.Equal(["__", "__", "**", "**"], actual
            .Where(token => token.Type == "talemarkFormatting" && (token.Text is "__" or "**"))
            .Select(token => token.Text));
        Assert.DoesNotContain(actual, token =>
            token.Type == "talemarkFormatting" && token.Text == "*" && token.Line == 7);
    }

    [Fact]
    public async Task Choice_label_incomplete_substitution_recovers_diagnostics_and_semantic_tokens()
    {
        const string valid = """
            === start
            * [my {var:choice}] (requires flag:ready) -> finish
            === finish
            -> END
            """;
        var incomplete = valid.Replace("{var:choice}", "{var:choice", StringComparison.Ordinal);
        await using var client = new Client();
        File.WriteAllText(client.PathOf("story.talewright.yaml"),
            Manifest + "flags: [ready]\nvars: {choice: 1}\n");
        var initialized = await client.Initialize();
        var legend = initialized.GetProperty("result").GetProperty("capabilities")
            .GetProperty("semanticTokensProvider").GetProperty("legend").GetProperty("tokenTypes")
            .EnumerateArray().Select(value => value.GetString()!).ToArray();

        async Task<DecodedToken[]> ReadTokens(string source)
        {
            var response = await client.Request("textDocument/semanticTokens/full", new
            {
                textDocument = new { uri = client.UriOf("start.tale") }
            });
            Assert.False(response.TryGetProperty("error", out _));
            var data = response.GetProperty("result").GetProperty("data")
                .EnumerateArray().Select(value => value.GetInt32()).ToArray();
            Assert.Equal(0, data.Length % 5);
            var tokens = Decode(data, legend, source);
            for (var index = 1; index < tokens.Length; index++)
                Assert.False(tokens[index].Line == tokens[index - 1].Line &&
                    tokens[index].Start < tokens[index - 1].Start + tokens[index - 1].Length);
            Assert.Contains(tokens, token => token.Text == "requires" && token.Type == "keyword");
            Assert.Contains(tokens, token => token.Text == "->" && token.Type == "talemarkDivert");
            return tokens;
        }

        await client.Open("start.tale", valid, 1);
        Assert.Empty(await client.Diagnostics("start.tale", 1));
        await ReadTokens(valid);

        await client.Change("start.tale", incomplete, 2);
        var diagnostic = Assert.Single(await client.Diagnostics("start.tale", 2));
        Assert.Equal("TW1004", diagnostic.GetProperty("code").GetString());
        var diagnosticRange = diagnostic.GetProperty("range");
        Assert.Equal(1, diagnosticRange.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(incomplete.Split('\n')[1].IndexOf('{'),
            diagnosticRange.GetProperty("start").GetProperty("character").GetInt32());
        Assert.True(diagnosticRange.GetProperty("end").GetProperty("character").GetInt32() <=
            incomplete.Split('\n')[1].IndexOf("(requires", StringComparison.Ordinal));
        await ReadTokens(incomplete);

        await client.Change("start.tale", valid, 3);
        Assert.Empty(await client.Diagnostics("start.tale", 3));
        await ReadTokens(valid);
    }

    private static DecodedToken[] Decode(int[] data, string[] legend, string source)
    {
        var lines = source.ReplaceLineEndings("\n").Split('\n');
        var tokens = new List<DecodedToken>(data.Length / 5);
        var line = 0;
        var previousStart = 0;
        for (var index = 0; index < data.Length; index += 5)
        {
            var lineDelta = data[index];
            line += lineDelta;
            var start = lineDelta == 0 ? previousStart + data[index + 1] : data[index + 1];
            var length = data[index + 2];
            var tokenType = data[index + 3];
            var modifiers = data[index + 4];
            tokens.Add(new(line, start, length, legend[tokenType], modifiers,
                lines[line].Substring(start, length)));
            previousStart = start;
        }
        return tokens.ToArray();
    }

    private static string ExpectedType(TalemarkClassificationKind kind) => kind switch
    {
        TalemarkClassificationKind.Comment => "comment",
        TalemarkClassificationKind.Keyword => "keyword",
        TalemarkClassificationKind.Operator => "operator",
        TalemarkClassificationKind.Number => "number",
        TalemarkClassificationKind.String => "string",
        TalemarkClassificationKind.Identifier => "variable",
        TalemarkClassificationKind.Namespace => "namespace",
        TalemarkClassificationKind.Property => "property",
        TalemarkClassificationKind.Function => "function",
        TalemarkClassificationKind.Modifier => "modifier",
        TalemarkClassificationKind.SceneIdentifier => "talemarkScene",
        TalemarkClassificationKind.Divert => "talemarkDivert",
        TalemarkClassificationKind.Punctuation => "talemarkPunctuation",
        TalemarkClassificationKind.Formatting => "talemarkFormatting",
        TalemarkClassificationKind.Escape => "talemarkEscape",
        TalemarkClassificationKind.StructuralMarker => "talemarkStructure",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static int ExpectedModifiers(TalemarkClassificationModifiers modifiers) =>
        (modifiers & TalemarkClassificationModifiers.Declaration) != 0 ? 1 : 0;
}
