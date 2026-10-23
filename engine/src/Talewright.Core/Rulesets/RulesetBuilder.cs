using Talewright.Core.Identity;

namespace Talewright.Core.Rulesets;

/// <summary>
/// Fluent builder for <see cref="Ruleset"/>. This is the main extension point for "turn off the defaults and
/// substitute what I need".
/// </summary>
/// <example>
/// <code>
/// var datingSim = GenericRuleset.Create().ToBuilder("dating-sim")
///     .RemoveStat(new StatId("str"))
///     .RemoveStat(new StatId("con"))
///     .AddStat(new StatDefinition(new StatId("charm"), "Charm", StatCategory.Attribute, 0, 20, 10))
///     .Build();
/// </code>
/// </example>
public sealed class RulesetBuilder
{
    private readonly string _id;
    private readonly List<StatDefinition> _stats = [];
    private readonly List<ReputationTier> _tiers = [];
    private readonly List<IStatModifier> _modifiers = [];
    private ISkillCheckRule? _skillCheckRule;
    private ILevelingRule? _levelingRule;

    public RulesetBuilder(string id) => _id = id;

    internal RulesetBuilder(string id, Ruleset source)
        : this(id)
    {
        _stats.AddRange(source.Stats);
        _tiers.AddRange(source.Tiers);
        _modifiers.AddRange(source.Modifiers);
        _skillCheckRule = source.SkillCheckRule;
        _levelingRule = source.LevelingRule;
    }

    /// <summary>Adds a stat. Throws if the id already exists (use <see cref="ReplaceStat"/>).</summary>
    public RulesetBuilder AddStat(StatDefinition stat)
    {
        if (_stats.Any(s => s.Id == stat.Id))
        {
            throw new InvalidOperationException($"Stat '{stat.Id}' is already defined in ruleset '{_id}'.");
        }

        _stats.Add(stat);
        return this;
    }

    /// <summary>Removes a stat. Skills governed by it lose their governing attribute.</summary>
    public RulesetBuilder RemoveStat(StatId id)
    {
        _stats.RemoveAll(s => s.Id == id);
        for (var i = 0; i < _stats.Count; i++)
        {
            if (_stats[i].GoverningAttribute == id)
            {
                _stats[i] = _stats[i] with { GoverningAttribute = null };
            }
        }

        return this;
    }

    public bool HasStat(StatId id) => _stats.Any(s => s.Id == id);

    /// <summary>Replaces an existing stat definition in place (keeps ordering).</summary>
    public RulesetBuilder ReplaceStat(StatDefinition stat)
    {
        var index = _stats.FindIndex(s => s.Id == stat.Id);
        if (index < 0)
        {
            throw new InvalidOperationException($"Stat '{stat.Id}' does not exist in ruleset '{_id}'.");
        }

        _stats[index] = stat;
        return this;
    }

    /// <summary>Replaces all reputation tiers.</summary>
    public RulesetBuilder WithTiers(params ReputationTier[] tiers)
    {
        _tiers.Clear();
        _tiers.AddRange(tiers);
        return this;
    }

    public RulesetBuilder WithSkillCheckRule(ISkillCheckRule? rule)
    {
        _skillCheckRule = rule;
        return this;
    }

    public RulesetBuilder WithLevelingRule(ILevelingRule? rule)
    {
        _levelingRule = rule;
        return this;
    }

    public RulesetBuilder AddModifier(IStatModifier modifier)
    {
        _modifiers.Add(modifier);
        return this;
    }

    public Ruleset Build()
    {
        if (_levelingRule is not null && !_stats.Any(s => s.Id == _levelingRule.LevelStat))
        {
            // Leveling without a level stat is meaningless; drop it rather than fail so removing "level" just works.
            _levelingRule = null;
        }

        return new Ruleset(_id, _stats.ToArray(), _tiers.ToArray(), _skillCheckRule, _levelingRule, _modifiers.ToArray());
    }
}
