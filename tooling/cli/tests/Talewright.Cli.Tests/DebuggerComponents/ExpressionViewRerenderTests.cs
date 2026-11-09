using Bunit;
using Talewright.Debugger;
using Talewright.Rules.Expressions;

namespace Talewright.Cli.Tests.DebuggerComponents;

// Static rendering of ExpressionView is covered by Talewright.Cli.Tests.ExpressionViewTests.
public sealed class ExpressionViewRerenderTests : BunitContext
{
    [Fact]
    public void Changing_the_trace_replaces_the_formatting_fallback()
    {
        var unsupported = new ExpressionTrace(new UnsupportedExpr(), null, []);
        var literal = new ExpressionTrace(new BoolLiteral(true), Value.Of(true), []);
        var view = Render<ExpressionView>(parameters => parameters.Add(component => component.Trace, unsupported));
        Assert.StartsWith("Talemark text unavailable: ", view.Find("li > em").TextContent, StringComparison.Ordinal);
        Assert.Equal("Not evaluated", view.Find("li > strong").TextContent);

        view.Render(parameters => parameters.Add(component => component.Trace, literal));

        Assert.Empty(view.FindAll("li > em"));
        Assert.Equal("true", view.Find("li > code").TextContent);
        Assert.Equal("true", view.Find("li > strong").TextContent);

        view.Render(parameters => parameters.Add(component => component.Trace, unsupported));

        Assert.Empty(view.FindAll("li > code"));
        Assert.NotEmpty(view.FindAll("li > em"));
    }

    private sealed record UnsupportedExpr : Expr
    {
        public override ExprType Type => ExprType.Bool;
    }
}
