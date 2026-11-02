using Talewright.Core.Identity;
using Talewright.Core.World;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Parsing;

namespace Talewright.Talemark.Tests;

public sealed class ExpressionSyntaxBindingTests
{
    [Fact]
    public void One_syntax_tree_can_bind_against_different_variable_types()
    {
        const string text = "var:score";
        var parseDiagnostics = new DiagnosticBag();
        var syntax = ExpressionSyntaxParser.ParseExpression(Lexer.Tokenize(text), text.Length,
            offset => new SourceLocation("main.tale", 1, offset + 1), parseDiagnostics);

        Assert.False(syntax.HasErrors);
        Assert.Empty(parseDiagnostics.Items);

        var integerDiagnostics = new DiagnosticBag();
        var integer = ExpressionBinder.Bind(syntax, Symbols(textVariable: false), integerDiagnostics,
            offset => new SourceLocation("main.tale", 1, offset + 1));
        var textDiagnostics = new DiagnosticBag();
        var textValue = ExpressionBinder.Bind(syntax, Symbols(textVariable: true), textDiagnostics,
            offset => new SourceLocation("main.tale", 1, offset + 1));

        Assert.IsType<VarRef>(integer);
        Assert.IsType<TextVarRef>(textValue);
        Assert.Empty(integerDiagnostics.Items);
        Assert.Empty(textDiagnostics.Items);
    }

    [Fact]
    public void Syntax_parsing_does_not_resolve_names_but_binding_reports_semantic_errors()
    {
        const string text = "missing:name";
        var diagnostics = new DiagnosticBag();
        var syntax = ExpressionSyntaxParser.ParseExpression(Lexer.Tokenize(text), text.Length,
            offset => new SourceLocation("main.tale", 1, offset + 1), diagnostics);

        Assert.False(syntax.HasErrors);
        Assert.Empty(diagnostics.Items);

        Assert.Null(ExpressionBinder.Bind(syntax, Symbols(textVariable: false), diagnostics,
            offset => new SourceLocation("main.tale", 1, offset + 1)));
        var diagnostic = Assert.Single(diagnostics.Items);
        Assert.Equal("TW2001", diagnostic.Code);
        Assert.Equal(new SourceLocation("main.tale", 1, 1), diagnostic.Location);
    }

    [Theory]
    [InlineData("player:")]
    [InlineData("(")]
    [InlineData("1 +")]
    public void Incomplete_expression_syntax_retains_an_error_node(string text)
    {
        var diagnostics = new DiagnosticBag();
        var syntax = ExpressionSyntaxParser.ParseExpression(Lexer.Tokenize(text), text.Length,
            offset => new SourceLocation("main.tale", 1, offset + 1), diagnostics);

        Assert.True(syntax.HasErrors);
        Assert.IsType<ErrorExpressionNode>(syntax.Root);
        Assert.Single(diagnostics.Items);
        Assert.Null(ExpressionBinder.Bind(syntax, Symbols(textVariable: false), new DiagnosticBag(),
            offset => new SourceLocation("main.tale", 1, offset + 1)));
    }

    [Fact]
    public void Invalid_modifier_arguments_are_syntax_errors_not_binding_errors()
    {
        const string text = "player#one-many(\"singular\", plural)";
        var diagnostics = new DiagnosticBag();
        var syntax = ExpressionSyntaxParser.ParseSubstitution(Lexer.Tokenize(text), text.Length,
            offset => new SourceLocation("main.tale", 1, offset + 1), diagnostics);

        Assert.True(syntax.HasErrors);
        Assert.Contains(diagnostics.Items, diagnostic => diagnostic.Code == "TW2006");
        Assert.Null(ExpressionBinder.Bind(syntax, Symbols(textVariable: false), new DiagnosticBag(),
            offset => new SourceLocation("main.tale", 1, offset + 1)));
    }

    private static StorySymbols Symbols(bool textVariable)
    {
        var options = CompilerOptions.Default;
        var score = new VarId("score");
        var numbers = textVariable ? new Dictionary<VarId, int>() : new Dictionary<VarId, int> { [score] = 0 };
        var text = textVariable ? new Dictionary<VarId, string> { [score] = "" } : new Dictionary<VarId, string>();
        var world = new WorldDefinition(options.Rulesets["generic"](), [], [], [], [], numbers, text);
        return new StorySymbols
        {
            World = world,
            Flags = new HashSet<FlagId>(),
            Variables = new HashSet<VarId> { score },
            Scenes = new HashSet<SceneId>(),
            Predicates = options.Predicates,
            Modifiers = options.Modifiers,
        };
    }
}
