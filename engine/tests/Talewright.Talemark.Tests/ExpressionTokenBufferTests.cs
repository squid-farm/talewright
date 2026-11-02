using Talewright.Core.Identity;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Parsing;

namespace Talewright.Talemark.Tests;

public sealed class ExpressionTokenBufferTests
{
    [Fact]
    public void Tokens_retain_source_ranges_and_decode_values_when_requested()
    {
        const string input = " \t@Can.Open-door var:gold+=42 \"hi \\\"friend\\\" \\\\ 😀\" != #";
        var expected = new (TokenKind Kind, string Raw, string Value)[]
        {
            (TokenKind.NamedPredicate, "@Can.Open-door", "Can.Open-door"),
            (TokenKind.Identifier, "var", "var"),
            (TokenKind.Colon, ":", ":"),
            (TokenKind.Identifier, "gold", "gold"),
            (TokenKind.PlusAssign, "+=", "+="),
            (TokenKind.Number, "42", "42"),
            (TokenKind.String, "\"hi \\\"friend\\\" \\\\ 😀\"", "hi \"friend\" \\ 😀"),
            (TokenKind.NotEqual, "!=", "!="),
            (TokenKind.Hash, "#", "#"),
        };
        var tokens = Lexer.Tokenize(input);
        Assert.Equal(expected.Length + 1, tokens.Count);
        var next = 0;
        for (var index = 0; index < expected.Length; index++)
        {
            var (kind, raw, value) = expected[index];
            var offset = input.IndexOf(raw, next, StringComparison.Ordinal);
            Assert.True(offset >= 0);
            var token = tokens[index];
            Assert.Same(input, token.Source);
            Assert.Equal(kind, token.Kind);
            Assert.Equal(offset, token.Offset);
            Assert.Equal(raw.Length, token.Length);
            Assert.Equal(value, token.Text.ToString());
            Assert.Equal(value, token.Value);
            next = offset + raw.Length;
        }
        var end = tokens[tokens.Count - 1];
        Assert.Equal(TokenKind.End, end.Kind);
        Assert.Equal(input.Length, end.Offset);
        Assert.Equal(0, end.Length);
        Assert.Equal("", end.Value);
    }

    [Fact]
    public void Invalid_names_and_non_ascii_input_keep_utf16_offsets_and_original_token_text()
    {
        const string input = "😀 @ @2Bad good. x..y";
        var tokens = Lexer.Tokenize(input);
        Assert.Equal(TokenKind.Error, tokens[0].Kind);
        Assert.Equal(input[..1], tokens[0].Value);
        Assert.Equal(TokenKind.Error, tokens[1].Kind);
        Assert.Equal(input[1..2], tokens[1].Value);
        Assert.Equal((TokenKind.Error, 3, 1, ""), (tokens[2].Kind, tokens[2].Offset, tokens[2].Length, tokens[2].Value));
        Assert.Equal((TokenKind.Error, 5, 5, "2Bad"), (tokens[3].Kind, tokens[3].Offset, tokens[3].Length, tokens[3].Value));
        Assert.Equal((TokenKind.Identifier, "good"), (tokens[4].Kind, tokens[4].Value));
        Assert.Equal((TokenKind.Error, "."), (tokens[5].Kind, tokens[5].Value));
        Assert.Equal((TokenKind.Error, "x..y"), (tokens[6].Kind, tokens[6].Value));
    }

    [Fact]
    public void String_errors_keep_their_message_and_exact_boundaries()
    {
        const string input = "\"ok\" \"bad\\q\" \"unclosed";
        var tokens = Lexer.Tokenize(input);
        Assert.Equal(TokenKind.String, tokens[0].Kind);
        Assert.Equal("ok", tokens[0].Value);
        Assert.Equal(4, tokens[0].StringEnd);
        Assert.Equal(TokenKind.Error, tokens[1].Kind);
        Assert.Equal(QuotedStringSyntax.InvalidEscape, tokens[1].Value);
        Assert.Equal(input.IndexOf("\\q", StringComparison.Ordinal), tokens[1].ErrorOffset);
        Assert.Equal(input.IndexOf("\" \"unclosed", StringComparison.Ordinal) + 1, tokens[1].StringEnd);
        Assert.Equal(QuotedStringSyntax.Unterminated, tokens[2].Value);
        Assert.Equal(input.IndexOf("\"unclosed", StringComparison.Ordinal), tokens[2].ErrorOffset);
        Assert.Equal(input.Length, tokens[2].StringEnd);
    }

    [Fact]
    public void Early_and_later_registrations_share_tokens_without_removing_duplicate_sites()
    {
        var source = new StorySource(new Dictionary<string, string> { ["main.tale"] = "var:   " });
        var collector = new AnalysisCollector(source, CompilerOptions.Default);
        var location = new SourceLocation("main.tale", 1, 1);
        var first = collector.Expression(ExpressionContextKind.Number, "var:", location, cacheForReuse: true);
        var second = collector.Expression(ExpressionContextKind.Number, "var:", location);

        Assert.Same(first, second);
        Assert.Equal("var:   ", first.Source);
        Assert.Equal(2, collector.CompletionSites.Count);
        Assert.All(collector.CompletionSites, site => Assert.Same(first, site.Expression!.Tokens));
        var symbols = new[] { new AuthoringSymbol("gold", AuthoringSymbolKind.Variable, "integer", null) };
        var columns = collector.CompletionSites[0].Sites(symbols)
            .Where(site => site.Text == "" && site.Kinds.Contains(AuthoringSymbolKind.Variable))
            .Select(site => site.Range.Start.Column);
        Assert.Equal([5, 6, 7, 8], columns);
    }

    [Fact]
    public void Declaration_collection_uses_the_expression_boundary_not_completion_whitespace()
    {
        const string expression = "set flag:seen; var:score += 2; var:word = \"hi\"";
        var tokens = Lexer.Tokenize(expression + "   ");
        var flags = new HashSet<FlagId>();
        var variables = new Dictionary<VarId, int>();
        var declarations = new List<VariableDeclaration>();
        ExpressionParser.CollectDeclarations(tokens, expression.Length, flags, variables,
            typedDeclaration: declarations.Add, location: new SourceLocation("main.tale", 1, 1));

        Assert.Contains(new FlagId("seen"), flags);
        Assert.Equal([new VarId("score"), new VarId("word")], variables.Keys);
        Assert.Equal(["2", "\"hi\""], declarations.Select(declaration => declaration.Expression));
        Assert.Equal([true, false], declarations.Select(declaration => declaration.Numeric));
    }

    [Theory]
    [InlineData("* [Go] (requires: \"bad\\q")]
    [InlineData("* [Go] { var:word = \"bad\\q")]
    [InlineData("{if \"bad\\q")]
    [InlineData("{\"bad\\q")]
    public void Incomplete_expression_analysis_keeps_compiler_diagnostics_and_order(string line)
    {
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = """
                game: {id: tokens, title: Tokens, start: start}
                characters:
                  player: {first-name: Tester}
                vars: {word: ""}
                """,
            ["main.tale"] = "=== start\r\n" + line,
        });
        var compilation = StoryCompiler.Compile(source);
        var analysis = StoryAnalyzer.Analyze(source);

        Assert.Equal(compilation.Diagnostics, analysis.Diagnostics);
        var invalid = Assert.Single(analysis.Diagnostics, diagnostic => diagnostic.Code == "TW1001");
        Assert.Equal(new SourceLocation("main.tale", 2, line.IndexOf('\\') + 1), invalid.Location);
        Assert.Contains(QuotedStringSyntax.InvalidEscape, invalid.Message);
        Assert.NotEmpty(analysis.Expressions);
    }
}
