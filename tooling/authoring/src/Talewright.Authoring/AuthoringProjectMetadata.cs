using System.Text.Json;
using System.Text.Json.Serialization;
using Talewright.Core.Identity;
using Talewright.Core.Rulesets;
using Talewright.Core.State;
using Talewright.Narrative.Templates;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Analysis;

namespace Talewright.Authoring;

public sealed record AuthoringStat(string Id, string Name, StatCategory Category, int Min, int Max, int Default,
    string? GoverningAttribute);
public sealed record AuthoringRuleset(string Id, string DefinitionId, AuthoringStat[] Stats,
    ReputationTier[] Tiers, bool SupportsChecks);

/// <summary>
/// Data-only registrations exported explicitly by a trusted project host using the same options as CLI/
/// MSBuild validation. No assembly names, commands, or executable delegates are accepted.
/// </summary>
public sealed record AuthoringProjectMetadata(int FormatVersion, string[] Predicates, string[] Modifiers,
    AuthoringRuleset[] Rulesets)
{
    public string[] BuiltInModifiers { get; init; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    /// <summary>Analyzes a source snapshot using these data-only registrations.</summary>
    public StoryAnalysis Analyze(StorySource source) => StoryAnalyzer.Analyze(source, CreateOptions());

    /// <summary>Explicit host opt-in; ruleset factories run only during this export, never while loading metadata.</summary>
    public static AuthoringProjectMetadata Export(CompilerOptions options) => new(1,
        options.Predicates.Names.ToArray(), options.Modifiers.Names.ToArray(),
        options.Rulesets.Select(pair =>
        {
            var ruleset = pair.Value();
            return new AuthoringRuleset(pair.Key, ruleset.Id, ruleset.Stats.Select(stat => new AuthoringStat(stat.Id.Value,
                stat.Name, stat.Category, stat.Min, stat.Max, stat.Default, stat.GoverningAttribute?.Value)).ToArray(),
                ruleset.Tiers.ToArray(), ruleset.SkillCheckRule is not null);
        }).ToArray())
    {
        BuiltInModifiers = options.Modifiers.Names.Where(name =>
            options.Modifiers.TryGet(name, out var modifier) && TextModifierRegistry.IsBuiltIn(modifier)).ToArray(),
    };

    public string ToJson()
    {
        Validate();
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    public static AuthoringProjectMetadata FromJson(string json)
    {
        var metadata = JsonSerializer.Deserialize<AuthoringProjectMetadata>(json, JsonOptions)
            ?? throw new JsonException("Authoring metadata must be an object.");
        metadata.Validate();
        return metadata;
    }

    private void Validate()
    {
        if (FormatVersion != 1) throw new InvalidDataException($"Unsupported authoring metadata version {FormatVersion}.");
        if (Predicates is null || Modifiers is null || Rulesets is null || Rulesets.Any(ruleset => ruleset is null))
            throw new InvalidDataException("Authoring metadata registration arrays cannot be null or contain null.");
        static void Names(IEnumerable<string> names)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in names)
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                    throw new InvalidDataException("Authoring metadata names must be nonempty and unique.");
        }
        Names(Predicates);
        Names(Modifiers);
        if (BuiltInModifiers is null)
            throw new InvalidDataException("Built-in modifier names cannot be null.");
        Names(BuiltInModifiers);
        var defaults = TextModifierRegistry.CreateDefault();
        if (BuiltInModifiers.Any(name => !Modifiers.Contains(name, StringComparer.Ordinal) || !defaults.TryGet(name, out _)))
            throw new InvalidDataException("Built-in modifier names must identify registered engine built-ins.");
        Names(Rulesets.Select(ruleset => ruleset.Id));
        if (Rulesets.Length == 0)
            throw new InvalidDataException("Authoring metadata must include at least one ruleset.");
        foreach (var ruleset in Rulesets)
        {
            if (string.IsNullOrWhiteSpace(ruleset.DefinitionId))
                throw new InvalidDataException("Ruleset definition IDs must be nonempty.");
            if (ruleset.Stats is null || ruleset.Tiers is null ||
                ruleset.Stats.Any(stat => stat is null) || ruleset.Tiers.Any(tier => tier is null))
                throw new InvalidDataException("Ruleset metadata arrays cannot be null or contain null.");
            Names(ruleset.Stats.Select(stat => stat.Id));
            Names(ruleset.Tiers.Select(tier => tier.Id));
            foreach (var stat in ruleset.Stats)
                if (stat.Min > stat.Max || !Enum.IsDefined(stat.Category) ||
                    stat.GoverningAttribute is { } attribute && !ruleset.Stats.Any(candidate => candidate.Id == attribute))
                    throw new InvalidDataException($"Invalid stat metadata for '{stat.Id}'.");
        }
    }

    internal CompilerOptions CreateOptions()
    {
        Validate();
        var predicates = new NamedPredicateRegistry();
        foreach (var name in Predicates) predicates.Register(name, _ => throw new InvalidOperationException("Authoring metadata cannot evaluate predicates."));
        var modifiers = new TextModifierRegistry();
        var defaults = TextModifierRegistry.CreateDefault();
        foreach (var name in Modifiers)
            modifiers.Register(BuiltInModifiers.Contains(name, StringComparer.Ordinal) && defaults.TryGet(name, out var builtIn)
                ? builtIn
                : new DelegateTextModifier(name, (_, _) => throw new InvalidOperationException("Authoring metadata cannot apply modifiers.")));
        var rulesets = new Dictionary<string, Func<Ruleset>>(StringComparer.Ordinal);
        foreach (var metadata in Rulesets)
        {
            var builder = new RulesetBuilder(metadata.DefinitionId).WithTiers(metadata.Tiers);
            foreach (var stat in metadata.Stats)
                builder.AddStat(new StatDefinition(new StatId(stat.Id), stat.Name, stat.Category,
                    stat.Min, stat.Max, stat.Default, stat.GoverningAttribute is { } attribute ? new StatId(attribute) : null));
            if (metadata.SupportsChecks) builder.WithSkillCheckRule(new AuthoringCheckRule());
            var ruleset = builder.Build();
            rulesets.Add(metadata.Id, () => ruleset);
        }
        return new CompilerOptions
        {
            Predicates = predicates,
            Modifiers = modifiers,
            Rulesets = rulesets,
        };
    }

    private sealed class AuthoringCheckRule : ISkillCheckRule
    {
        public int DieSides => throw new InvalidOperationException("Authoring metadata cannot roll dice.");
        public SkillCheckResult Resolve(IGameStateView state, SkillCheckRequest request) =>
            throw new InvalidOperationException("Authoring metadata cannot evaluate skill checks.");
    }
}
