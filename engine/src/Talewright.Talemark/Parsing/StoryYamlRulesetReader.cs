using Talewright.Core.Identity;
using Talewright.Core.Rulesets;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Vocabulary;
using YamlDotNet.RepresentationModel;
using static Talewright.Talemark.Parsing.StoryYamlContext;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Talemark.Parsing;

/// <summary>Reads ruleset configuration before world declarations resolve their stats.</summary>
internal sealed class StoryYamlRulesetReader(StoryYamlContext yaml, CompilerOptions options,
    StatDiagnosticGuidance statGuidance, AnalysisCollector? analysis)
{
    private readonly StoryYamlContext _yaml = yaml;
    private readonly CompilerOptions _options = options;
    private readonly StatDiagnosticGuidance _statGuidance = statGuidance;
    private readonly AnalysisCollector? _analysis = analysis;

    internal Ruleset Read(YamlNode? node)
    {
        if (node is null)
        {
            return Default();
        }

        if (node is YamlScalarNode shorthand && !IsNullLike(shorthand))
        {
            return Named(shorthand);
        }

        var map = _yaml.Mapping(node, ManifestVocabulary.Ruleset);
        if (map is null)
        {
            return Default();
        }

        _yaml.CheckKeys(map, ManifestVocabulary.Base, ManifestVocabulary.Disable, ManifestVocabulary.Stats);
        var baseNode = Get(map, ManifestVocabulary.Base);
        var baseRuleset = baseNode is null ? Default() : Named(baseNode);
        var baseId = baseRuleset.Id;
        var builder = baseRuleset.ToBuilder();
        var governingAttributes = new Dictionary<StatId, YamlNode>();
        if (Get(map, ManifestVocabulary.Disable) is { } disable)
        {
            _analysis?.DisabledStats(disable, baseRuleset);
            foreach (var item in _yaml.Sequence(disable, "ruleset.disable"))
            {
                var rawId = _yaml.Identifier(item, "stat id");
                if (rawId is null) continue;
                var id = new StatId(rawId);
                if (builder.HasStat(id))
                {
                    builder.RemoveStat(id);
                }
                else
                {
                    _yaml.Error(item, CompilerDiagnosticCodes.UnknownConfigurationReference,
                        $"Cannot disable '{id}': the '{baseId}' ruleset has no such stat." +
                        _statGuidance.ForInvalidDisable(id), 0);
                }
            }
        }

        if (Get(map, ManifestVocabulary.Stats) is { } stats)
        {
            foreach (var item in _yaml.Sequence(stats, "ruleset.stats"))
            {
                if (_yaml.Mapping(item, "stat") is not { } stat)
                {
                    continue;
                }

                _yaml.CheckKeys(stat, ManifestVocabulary.Id, ManifestVocabulary.Name, ManifestVocabulary.Category, ManifestVocabulary.Min, ManifestVocabulary.Max, ManifestVocabulary.Default, ManifestVocabulary.GoverningAttribute);
                var id = _yaml.RequiredIdentifier(stat, ManifestVocabulary.Id);
                if (id is null)
                {
                    continue;
                }

                var category = Scalar(Get(stat, ManifestVocabulary.Category)) ?? ManifestVocabulary.StatCategoryCustom;
                if (!ManifestVocabulary.TryParseStatCategory(category, out var parsedCategory))
                {
                    _yaml.Error(Get(stat, ManifestVocabulary.Category)!, CompilerDiagnosticCodes.InvalidConfigurationValue,
                        $"Unknown stat category '{category}'. Use {ManifestVocabulary.StatCategoryAttribute}, " +
                        $"{ManifestVocabulary.StatCategorySkill}, {ManifestVocabulary.StatCategoryResource}, " +
                        $"{ManifestVocabulary.StatCategoryProgression} or {ManifestVocabulary.StatCategoryCustom}.", 0);
                }

                var min = _yaml.Int(Get(stat, ManifestVocabulary.Min)) ?? 0;
                var max = _yaml.Int(Get(stat, ManifestVocabulary.Max)) ?? int.MaxValue;
                if (min > max)
                {
                    _yaml.Error(Get(stat, ManifestVocabulary.Min) ?? stat, CompilerDiagnosticCodes.InvalidConfigurationValue, $"Stat '{id}' minimum ({min}) must not be greater than its maximum ({max}).", 0);
                    continue;
                }

                var attributeNode = Get(stat, ManifestVocabulary.GoverningAttribute);
                var attribute = attributeNode is null ? null : _yaml.Identifier(attributeNode, "governing attribute stat id");
                var definition = new StatDefinition(new StatId(id), Scalar(Get(stat, ManifestVocabulary.Name)) ?? id, parsedCategory,
                    min, max, _yaml.Int(Get(stat, ManifestVocabulary.Default)) ?? min, attribute is null ? null : new StatId(attribute));
                governingAttributes.Remove(definition.Id);
                if (attributeNode is not null)
                {
                    if (parsedCategory != StatCategory.Skill)
                    {
                        _yaml.Error(attributeNode, CompilerDiagnosticCodes.InvalidConfigurationValue, $"'governing-attribute' is only allowed on stats with category: skill; '{id}' is not a skill.", 0);
                    }
                    governingAttributes[definition.Id] = attributeNode;
                }
                if (builder.HasStat(definition.Id))
                {
                    builder.ReplaceStat(definition);
                }
                else
                {
                    builder.AddStat(definition);
                }
            }
        }

        var ruleset = builder.Build();
        foreach (var (skill, targetNode) in governingAttributes)
        {
            _analysis?.GoverningAttribute(targetNode, ruleset);
            if (ruleset.StatsById[skill].GoverningAttribute is not { } target) continue;
            if (!ruleset.StatsById.TryGetValue(target, out var attribute))
            {
                var reason = baseRuleset.HasStat(target) ? "disabled" : "unknown";
                _yaml.Error(targetNode, CompilerDiagnosticCodes.UnknownConfigurationReference, $"Stat '{skill}' governing-attribute target '{target}' is {reason}. Reference an enabled stat with category: attribute.", 0);
            }
            else if (attribute.Category != StatCategory.Attribute)
            {
                _yaml.Error(targetNode, CompilerDiagnosticCodes.InvalidConfigurationValue, $"Stat '{skill}' governing-attribute target '{target}' must have category: attribute.", 0);
            }
        }

        return ruleset;
    }

    // YAML null spellings and empty or '~' scalars are missing containers, not ruleset names.
    private static bool IsNullLike(YamlScalarNode node) =>
        node.Value is null or "" or "~" ||
        (node.Style == YamlDotNet.Core.ScalarStyle.Plain && node.Value is "null" or "Null" or "NULL");

    private Ruleset Named(YamlNode node)
    {
        // Unknown names recover with the RPG ruleset so stat references keep resolving while the author fixes the name.
        var id = _yaml.Identifier(node, "ruleset id") ?? Rulesets.Generic.GenericRuleset.Id;
        if (!_options.Rulesets.TryGetValue(id, out var factory))
        {
            _yaml.Error(node, CompilerDiagnosticCodes.UnknownRuleset, $"Unknown ruleset '{id}'. Available: {string.Join(", ", _options.Rulesets.Keys)}.", 0);
            id = Rulesets.Generic.GenericRuleset.Id;
            factory = _options.Rulesets[id];
        }

        return Base(id, factory);
    }

    private Ruleset Default() => Base(ManifestVocabulary.RulesetNone, _options.Rulesets[ManifestVocabulary.RulesetNone]);

    private Ruleset Base(string name, Func<Ruleset> factory)
    {
        var ruleset = factory();
        _statGuidance.SetBase(name, factory, ruleset);
        _analysis?.BaseRuleset(ruleset);
        return ruleset;
    }
}
