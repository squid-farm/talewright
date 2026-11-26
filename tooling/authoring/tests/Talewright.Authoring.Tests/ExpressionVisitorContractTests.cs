using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Formatting;
using Talewright.Talemark.Model;

namespace Talewright.Authoring.Tests;

public sealed class ExpressionVisitorContractTests
{
    private sealed record Case(Type Type, string Source, string? Preview, string? RoundedPreview, bool? Visibility,
        StoryStateReadKind? ReadKind, string? ReadReference = null, bool ConditionOnly = false,
        string? SelectedPreview = null);

    private static readonly Case[] Cases =
    [
        new(typeof(NumberLiteral), "1", "1", "1", true, null, SelectedPreview: "one"),
        new(typeof(BoolLiteral), "true", "true", null, true, null),
        new(typeof(TextLiteral), "\"iron\"", "iron", null, true, null),
        new(typeof(TierLiteral), "friendly", "Friendly", null, true, null),
        new(typeof(StatRef), "player:str", null, "10", false, StoryStateReadKind.Stat, SelectedPreview: "many"),
        new(typeof(StandingRef), "faction:guild:standing", null, "1", true, StoryStateReadKind.Standing,
            SelectedPreview: "one"),
        new(typeof(StandingTierRef), "faction:guild:tier", null, null, true, StoryStateReadKind.StandingTier),
        new(typeof(FlagRef), "flag:ready", null, null, true, StoryStateReadKind.Flag),
        new(typeof(VarRef), "var:coins", "3", "3", true, StoryStateReadKind.NumberVariable, SelectedPreview: "many"),
        new(typeof(TextVarRef), "var:weather", "sunny", null, true, StoryStateReadKind.TextVariable),
        new(typeof(NameRef), "player:first-name", "Mara", null, true, StoryStateReadKind.CharacterName),
        new(typeof(PronounRef), "player:pronoun", "she", null, true, StoryStateReadKind.CharacterPronoun),
        new(typeof(FactionNameRef), "faction:guild", "Guild", null, true, StoryStateReadKind.FactionName),
        new(typeof(ItemNameRef), "item:sword", "Sword", null, true, StoryStateReadKind.ItemName),
        new(typeof(ItemPropertyRef), "item:sword:sharpness", "1.25", "1", true, StoryStateReadKind.ItemProperty,
            SelectedPreview: "many"),
        new(typeof(HasItem), "player has sword", null, null, true, StoryStateReadKind.ItemsHeld),
        new(typeof(Visited), "player visited start", null, null, true, StoryStateReadKind.VisitedScene),
        new(typeof(SceneVisitCountRef), "scene:player:visits", null, "0", true, StoryStateReadKind.SceneVisitCount,
            SelectedPreview: "many"),
        new(typeof(InventoryCountRef), "player:inventory:sword:count", null, "0", true, StoryStateReadKind.InventoryCount,
            SelectedPreview: "many"),
        new(typeof(InventoryItemPropertyRef), "player:inventory:sword:sharpness", "1.25", "1", true,
            StoryStateReadKind.InventoryItemProperty, SelectedPreview: "many"),
        new(typeof(ItemComparison), "var:tool == sword", null, null, true, StoryStateReadKind.ItemName),
        new(typeof(ItemReferencePropertyRef), "var:tool:sharpness", null, null, true,
            StoryStateReadKind.InventoryItemProperty),
        new(typeof(SkillCheckExpr), "skill-check perception dc 12", null, null, null, StoryStateReadKind.SkillCheck,
            "depends on a perception check", ConditionOnly: true),
        new(typeof(NamedPredicate), "@Ready", null, null, false, StoryStateReadKind.NamedPredicate),
        new(typeof(UnaryExpr), "-var:coins", null, "-3", true, StoryStateReadKind.NumberVariable, "var:coins",
            SelectedPreview: "many"),
        new(typeof(BinaryExpr), "var:coins + 1", null, "4", true, StoryStateReadKind.NumberVariable, "var:coins",
            SelectedPreview: "many"),
    ];

    private const string Manifest = """
        game:
          id: expression-contract
          title: Expression contract
          start: start
          talemark: 2
          ruleset: rpg
          character-sheet:
            stats:
              dex: 'CONDITION'
        characters:
          player: {first-name: Mara, pronouns: she}
        vars: {weather: "sunny", coins: 3}
        flags: {ready: true}
        factions:
          guild: {name: Guild, standing: 1}
        items:
          sword: {name: Sword, sharpness: 1.25}
        """;

    [Fact]
    public void Every_builtin_expression_has_an_authoring_contract()
    {
        var types = typeof(Expr).Assembly.GetTypes()
            .Where(type => type.IsSubclassOf(typeof(Expr)) && !type.IsAbstract)
            .OrderBy(type => type.Name);
        Assert.Equal(types, Cases.Select(item => item.Type).OrderBy(type => type.Name));
    }

    public static IEnumerable<object[]> ExpressionSources() => Cases.Select(item => new object[] { item.Source });

    [Theory]
    [MemberData(nameof(ExpressionSources))]
    public void Expression_contracts_cover_previews_visibility_formatting_and_reads(string source)
    {
        var item = Assert.Single(Cases, candidate => candidate.Source == source);
        var options = new CompilerOptions
        {
            Predicates = new NamedPredicateRegistry().Register("Ready", _ =>
                throw new InvalidOperationException("Authoring must not execute project predicates.")),
        };
        var condition = $"({item.Source}) == ({item.Source})";
        var manifest = Manifest.Replace("CONDITION", condition, StringComparison.Ordinal);
        var tale = $"=== start\n{{{item.Source}}}\n-> END";
        if (item.ConditionOnly)
        {
            Assert.Null(item.Visibility);
            var invalid = AnalyzeSource(manifest, tale, options);
            Assert.Contains(invalid.Diagnostics, diagnostic => diagnostic.Location.File == StorySource.ManifestFileName
                && diagnostic.Message.Contains("'skill-check' is not allowed", StringComparison.Ordinal));
            Assert.Contains(invalid.Diagnostics, diagnostic => diagnostic.Location.File == "main.tale"
                && diagnostic.Message.Contains("'skill-check' is not allowed", StringComparison.Ordinal));
            var valid = Analyze(Manifest.Replace("CONDITION", "true", StringComparison.Ordinal),
                $"=== start\n@requires {item.Source}\n-> END", options);
            var conditionExpression = valid.Story!.Scenes[0].Requires!;
            Assert.Equal(item.Type, conditionExpression.GetType());
            Assert.Equal(item.Source, ExpressionFormatter.Format(conditionExpression));
            AssertReads(item, conditionExpression);
            return;
        }
        var analysis = Analyze(manifest, tale, options);
        var expression = Assert.Single(analysis.Substitutions).Template.Value;
        Assert.Equal(item.Type, expression.GetType());
        Assert.Equal(item.Source, ExpressionFormatter.Format(expression));
        Assert.Equal(item.Preview, Assert.Single(StoryPreview.Get(analysis, "main.tale")).Value);

        var state = GameState.CreateInitial(analysis.World!, 0);
        var visible = AuthoringStatVisibility.IsVisible(analysis, new StatId("dex"), state);
        Assert.Equal(Assert.IsType<bool>(item.Visibility), visible);
        AssertReads(item, analysis.CharacterSheet!.StatRules[new StatId("dex")]);
        if (expression.Type == ExprType.Number)
        {
            var rounded = Analyze(manifest, $"=== start\n{{({item.Source})#round}}\n-> END", options);
            Assert.Equal(item.RoundedPreview, Assert.Single(StoryPreview.Get(rounded, "main.tale")).Value);
            var selected = Analyze(manifest,
                $"=== start\n{{({item.Source})#one-many(\"one\", \"many\")}}\n-> END", options);
            Assert.Equal(item.SelectedPreview, Assert.Single(StoryPreview.Get(selected, "main.tale")).Value);
        }
    }

    private static void AssertReads(Case item, Expr condition)
    {
        var reads = StoryCondition.FromExpressions([condition]).Reads;
        if (item.ReadKind is not { } kind)
        {
            Assert.Empty(reads);
            return;
        }
        if (item.Type == typeof(ItemComparison) || item.Type == typeof(ItemReferencePropertyRef))
        {
            var variable = Assert.Single(reads, read => read.Kind == StoryStateReadKind.TextVariable);
            Assert.Equal("var:tool", variable.Reference);
            Assert.Equal(2, reads.Count);
        }
        var read = item.Type == typeof(ItemComparison) || item.Type == typeof(ItemReferencePropertyRef)
            ? Assert.Single(reads, read => read.Kind == kind)
            : Assert.Single(reads);
        Assert.Equal(kind, read.Kind);
        Assert.Equal(item.ReadReference ?? item.Source, read.Reference);
        Assert.Equal(kind == StoryStateReadKind.NamedPredicate, read.IsOpaque);
    }

    private static StoryAnalysis Analyze(string manifest, string tale, CompilerOptions options)
    {
        var analysis = AnalyzeSource(manifest, tale, options);
        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return analysis;
    }

    private static StoryAnalysis AnalyzeSource(string manifest, string tale, CompilerOptions options)
    {
        if (tale.Contains("var:tool", StringComparison.Ordinal))
            tale = tale.Replace("=== start\n", "=== start\n~ var:tool = item:sword\n", StringComparison.Ordinal);
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = manifest,
            ["main.tale"] = tale,
        });
        return StoryAnalyzer.Analyze(source, options);
    }
}
