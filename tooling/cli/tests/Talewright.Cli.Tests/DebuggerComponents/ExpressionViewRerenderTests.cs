using Bunit;
using Talewright.Core;
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

    [Fact]
    public void Executed_division_by_zero_is_reported_without_marking_short_circuit_branches()
    {
        var expression = new BinaryExpr(BinaryOp.Divide, new NumberLiteral(1), new NumberLiteral(0));
        var trace = new ExpressionTrace(expression, Value.Of(ExactNumber.Zero),
        [
            new(new NumberLiteral(1), Value.Of(1), []),
            new(new NumberLiteral(0), Value.Of(0), []),
        ]);

        var view = Render<ExpressionView>(parameters => parameters.Add(component => component.Trace, trace));

        Assert.Contains("division by zero executed", view.Find("[role='alert']").TextContent);

        var shortCircuited = new ExpressionTrace(expression, Value.Of(ExactNumber.Zero),
        [
            new(new NumberLiteral(1), Value.Of(1), []),
            ExpressionTrace.NotEvaluated(new NumberLiteral(0)),
        ]);
        view.Render(parameters => parameters.Add(component => component.Trace, shortCircuited));

        Assert.Empty(view.FindAll("[role='alert']"));
    }

    private sealed record UnsupportedExpr : Expr
    {
        public override ExprType Type => ExprType.Bool;
    }
}
