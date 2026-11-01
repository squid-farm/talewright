using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Talewright.Core.Identity;
using Talewright.Debugger;
using Talewright.Rules.Diagnostics;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Compilation;

namespace Talewright.Cli.Tests;

public sealed partial class ExpressionViewTests
{
    private const string Compound =
        "player has lantern and (faction:river >= 3 or not visited(cave)) and @Brave and var:weather == \"sunny\"";

    [Fact]
    public async Task Branch_explanations_show_each_condition_node_as_Talemark_with_its_recorded_result()
    {
        var model = Start();

        Assert.Equal(
        [
            (1, Compound, "true"),
            (2, "player has lantern and (faction:river >= 3 or not visited(cave)) and @Brave", "true"),
            (3, "player has lantern and (faction:river >= 3 or not visited(cave))", "true"),
            (4, "player has lantern", "true"),
            (4, "faction:river >= 3 or not visited(cave)", "true"),
            (5, "faction:river >= 3", "false"),
            (6, "faction:river", "1"),
            (6, "3", "3"),
            (5, "not visited(cave)", "true"),
            (6, "visited(cave)", "false"),
            (3, "@Brave", "true"),
            (2, "var:weather == \"sunny\"", "true"),
            (3, "var:weather", "\"sunny\""),
            (3, "\"sunny\"", "\"sunny\""),
        ], Nodes(await RenderTrace(Decision(model, 0))));

        Assert.Equal(
        [
            (1, "flag:ready and player has lantern", "false"),
            (2, "flag:ready", "false"),
            (2, "player has lantern", "Not evaluated"),
        ], Nodes(await RenderTrace(Decision(model, 1))));
    }

    [Fact]
    public async Task Inspection_panel_uses_Talemark_text_and_keeps_named_predicates_opaque()
    {
        var html = await Render<InspectionPanel>(new() { [nameof(InspectionPanel.Inspection)] = Start().Inspection });

        Assert.Contains($"<code>{Html(Compound)}</code>", html);
        Assert.Contains("<code>@Brave</code>", html);
        Assert.Contains("(opaque C# predicate; internals unavailable)", html);
        Assert.Contains($"<code>{Html("flag:ready and player has lantern")}</code>", html);
        Assert.DoesNotContain("BinaryExpr", html);
        Assert.DoesNotContain("HasItem", html);
        Assert.DoesNotContain("NamedPredicate", html);
        Assert.DoesNotContain("TextLiteral", html);
    }

    [Fact]
    public async Task Decision_details_omit_absent_metadata_and_keep_zero_and_negative_values()
    {
        var model = Start("""
            === hall
            @hub any(adventure)
            @hub-limit 2
            * [Wait] -> END
            === cave
            @tags adventure
            @priority 0
            -> END
            === cellar
            @tags adventure
            @priority -1
            -> END
            """, """

            triggers:
              - id: ready
                when: flag:ready
                do: set flag:ready
                once: true
            """);
        var decisions = model.Inspection!.Decisions;
        Assert.Contains(decisions, decision => decision.Pass == 0);
        Assert.Contains(decisions, decision => decision.Priority == 0);
        Assert.Contains(decisions, decision => decision.Priority == -1);
        Assert.Contains(decisions, decision => decision.Rank is not null);
        Assert.Contains(decisions, decision => decision.Limit == 2);
        Assert.Contains(decisions, decision => decision.Pass is null && decision.Priority is null &&
            decision.Rank is null && decision.Limit is null);

        var html = await Render<InspectionPanel>(new() { [nameof(InspectionPanel.Inspection)] = model.Inspection });
        var details = Regex.Matches(html, @"<p>Events (?<details>.*?)</p>", RegexOptions.Singleline);
        Assert.Equal(decisions.Count, details.Count);
        for (var i = 0; i < decisions.Count; i++)
        {
            var decision = decisions[i];
            var expected = $"[{decision.EventStart}, {decision.EventEnd}).";
            if (decision.Pass is { } pass) expected += $" Pass: {pass}.";
            if (decision.Priority is { } priority) expected += $" Priority: {priority}.";
            if (decision.Rank is { } rank) expected += $" Rank: {rank}.";
            if (decision.Limit is { } limit) expected += $" Limit: {limit}.";
            var actual = WebUtility.HtmlDecode(details[i].Groups["details"].Value);
            Assert.Equal(expected, Regex.Replace(actual, @"\s+", " ").Trim());
        }
    }

    [Fact]
    public async Task Host_built_quoted_text_displays_escaped_Talemark_and_recorded_results()
    {
        var quoted = new TextLiteral("say \"hi\"");
        var variable = new TextVarRef(new VarId("weather"));
        var trace = new ExpressionTrace(new BinaryExpr(BinaryOp.Equal, variable, quoted), Value.Of(false),
        [
            new(variable, Value.Of("sunny"), []),
            new(quoted, Value.Of("say \"hi\""), []),
        ]);

        var html = await RenderTrace(trace);

        Assert.DoesNotContain("Talemark text unavailable", html);
        Assert.Equal([
            (1, """var:weather == "say \"hi\"" """.TrimEnd(), "false"),
            (2, "var:weather", "\"sunny\""),
            (2, "\"say \\\"hi\\\"\"", "\"say \\u0022hi\\u0022\""),
        ], Nodes(html));
        Assert.Contains(Html("\"say \\u0022hi\\u0022\""), html);
        Assert.DoesNotContain("TextLiteral", html);
    }

    [Fact]
    public async Task Unsupported_host_built_expression_keeps_fallback_and_formattable_children()
    {
        var trace = new ExpressionTrace(new UnsupportedExpr(), null,
            [new(new BoolLiteral(true), Value.Of(true), [])]);
        var html = await RenderTrace(trace);
        Assert.Contains("Talemark text unavailable", html);
        Assert.Equal([(2, "true", "true")], Nodes(html));
    }

    [Fact]
    public async Task Long_expression_trace_is_truncated_for_display_without_recursion()
    {
        Expr expression = new BoolLiteral(false);
        for (var index = 0; index < 500; index++)
            expression = new BinaryExpr(BinaryOp.And, expression, new BoolLiteral(true));

        var html = await RenderTrace(ExpressionTrace.NotEvaluated(expression));

        Assert.Contains("Additional expression details omitted.", html);
    }

    private sealed record UnsupportedExpr : Expr
    {
        public override ExprType Type => ExprType.Bool;
    }

    private static WorkbenchModel Start(string? tale = null, string extraYaml = "")
    {
        var source = new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = """
                game:
                  id: expression-view
                  title: Expression view
                  start: hall
                flags: [ready]
                vars:
                  weather: "sunny"
                items:
                  lantern: {name: Lantern}
                factions:
                  river: {name: River Folk, standing: 1}
                characters:
                  player: {first-name: Ada, inventory: [lantern]}
                """ + extraYaml,
            ["main.tale"] = tale ?? $"""
                === hall
                * [Light] (requires: {Compound}) -> cave
                * [Ready] (requires: flag:ready and player has lantern) -> cave
                === cave
                The cave.
                -> END
                """,
        });
        var predicates = NamedPredicateRegistry.Empty.Register("Brave", _ => true);
        var compilation = StoryCompiler.Compile(source, new CompilerOptions { Predicates = predicates });
        Assert.True(compilation.Success, string.Join("\n", compilation.Diagnostics));
        var model = new WorkbenchModel("unused");
        Assert.True(model.ApplySource(new SourceCompilation(source, compilation, null), restart: true));
        model.Start();
        Assert.Null(model.Error);
        Assert.True(model.Inspection!.DecisionsCaptured);
        return model;
    }

    // Source order, not subjects: decision subject spelling belongs to scene-trace identity (#321).
    private static ExpressionTrace Decision(WorkbenchModel model, int choice)
    {
        var conditioned = model.Inspection!.Decisions
            .Where(decision => decision.Kind == DecisionKind.Choice && decision.Expression is not null).ToList();
        Assert.Equal(2, conditioned.Count);
        return conditioned[choice].Expression!;
    }

    // Pre-order (depth, Talemark text, result) rows; depth counts enclosing lists.
    private static List<(int Depth, string Text, string Result)> Nodes(string html)
    {
        var nodes = new List<(int, string, string)>();
        var depth = 0;
        foreach (Match match in NodePattern().Matches(html))
        {
            if (match.Value == "<ul>") depth++;
            else if (match.Value == "</ul>") depth--;
            else nodes.Add((depth, WebUtility.HtmlDecode(match.Groups["text"].Value), WebUtility.HtmlDecode(match.Groups["result"].Value)));
        }
        return nodes;
    }

    [GeneratedRegex(@"<ul>|</ul>|<li><code>(?<text>[^<]*)</code>:\s*<strong>(?<result>[^<]*)</strong>")]
    private static partial Regex NodePattern();

    private static string Html(string text) => System.Text.Encodings.Web.HtmlEncoder.Default.Encode(text);

    private static Task<string> RenderTrace(ExpressionTrace trace) => Render<RootList>(new() { [nameof(RootList.Trace)] = trace });

    private static async Task<string> Render<T>(Dictionary<string, object?> parameters) where T : IComponent
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters));
            return component.ToHtmlString();
        });
    }

    // Mirrors InspectionPanel's single-list wrapper around a decision's root node.
    private sealed class RootList : ComponentBase
    {
        [Parameter] public ExpressionTrace Trace { get; set; } = default!;

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "ul");
            builder.OpenComponent<ExpressionView>(1);
            builder.AddComponentParameter(2, nameof(ExpressionView.Trace), Trace);
            builder.CloseComponent();
            builder.CloseElement();
        }
    }
}
