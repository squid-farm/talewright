using Talewright.Core.Rulesets;
using Talewright.Narrative.Templates;
using Talewright.Rules.Expressions;
using Talewright.Rulesets.Generic;
using Talewright.Tale.Diagnostics;
using Talewright.Tale.Model;

namespace Talewright.Tale.Compilation;

/// <summary>
/// Extension points available to the compiler. No-code stories use <see cref="Default"/>; C# story projects
/// (M2) register their own predicates, modifiers and rulesets.
/// </summary>
public sealed class CompilerOptions
{
    /// <summary>Options with the built-in modifiers and the <c>generic</c> and <c>none</c> rulesets.</summary>
    public static CompilerOptions Default => new() { CompatibilityId = "talewright-builtins-v1" };

    /// <summary>
    /// Stable version of explicitly registered extensions for deterministic saves. Change it when extension
    /// behavior changes. Null disables saves; custom C# hosts must opt in with their own identity.
    /// </summary>
    public string? CompatibilityId { get; init; }

    /// <summary>Predicates usable as <c>@Name</c> in expressions.</summary>
    public NamedPredicateRegistry Predicates { get; init; } = NamedPredicateRegistry.Empty;

    /// <summary>Modifiers usable as <c>#name</c> in substitutions.</summary>
    public TextModifierRegistry Modifiers { get; init; } = TextModifierRegistry.CreateDefault();

    /// <summary>Rulesets selectable with <c>ruleset.base</c> in story.yaml.</summary>
    public IReadOnlyDictionary<string, Func<Ruleset>> Rulesets { get; init; } = DefaultRulesets();

    /// <summary>
    /// The built-in catalog: <c>generic</c> (the d20-style ruleset) and <c>none</c> (no stats and no check rule,
    /// but the standard reputation tiers so factions still work).
    /// </summary>
    public static IReadOnlyDictionary<string, Func<Ruleset>> DefaultRulesets() => new Dictionary<string, Func<Ruleset>>
    {
        [GenericRuleset.Id] = GenericRuleset.Create,
        ["none"] = () => new RulesetBuilder("none").WithTiers(GenericRuleset.Create().Tiers.ToArray()).Build(),
    };
}

/// <summary>The outcome of compiling a story: the model (if there were no errors) and every diagnostic.</summary>
public sealed record CompilationResult(StoryDefinition? Story, IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>True when a story was produced (warnings are allowed).</summary>
    public bool Success => Story is not null;

    public IEnumerable<Diagnostic> Errors => Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);

    public IEnumerable<Diagnostic> Warnings => Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning);
}
