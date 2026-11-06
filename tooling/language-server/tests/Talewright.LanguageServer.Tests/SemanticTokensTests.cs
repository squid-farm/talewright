using Talewright.Talemark.Analysis;

namespace Talewright.LanguageServer.Tests;

public sealed partial class ServerTests
{
    private readonly record struct DecodedToken(int Line, int Start, int Length, string Type, int Modifiers, string Text);

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
            .Where(token => token.Type == "operator" && (token.Text is "__" or "**"))
            .Select(token => token.Text));
        Assert.DoesNotContain(actual, token =>
            token.Type == "operator" && token.Text == "*" && token.Line == 7);
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
        TalemarkClassificationKind.Operator or TalemarkClassificationKind.Punctuation or
            TalemarkClassificationKind.Formatting => "operator",
        TalemarkClassificationKind.Number => "number",
        TalemarkClassificationKind.String or TalemarkClassificationKind.Escape => "string",
        TalemarkClassificationKind.Identifier => "variable",
        TalemarkClassificationKind.Namespace => "namespace",
        TalemarkClassificationKind.Property => "property",
        TalemarkClassificationKind.Function => "function",
        TalemarkClassificationKind.Modifier => "modifier",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static int ExpectedModifiers(TalemarkClassificationModifiers modifiers) =>
        (modifiers & TalemarkClassificationModifiers.Declaration) != 0 ? 1 : 0;
}
