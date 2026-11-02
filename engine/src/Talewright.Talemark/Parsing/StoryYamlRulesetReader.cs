using Talewright.Core.Identity;
using Talewright.Core.Rulesets;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using YamlDotNet.RepresentationModel;
using static Talewright.Talemark.Parsing.StoryYamlContext;

namespace Talewright.Talemark.Parsing;

/// <summary>Reads ruleset configuration before world declarations resolve their stats.</summary>
internal sealed class StoryYamlRulesetReader(StoryYamlContext yaml, CompilerOptions options, AnalysisCollector? analysis)
{
    private readonly StoryYamlContext _yaml = yaml;
    private readonly CompilerOptions _options = options;
    private readonly AnalysisCollector? _analysis = analysis;

    internal Ruleset Read(YamlNode? node)
    {
        if (node is null)
        {
            return _options.Rulesets[Rulesets.Generic.GenericRuleset.Id]();
        }

        var map = _yaml.Mapping(node, "ruleset");
        if (map is null)
        {
            return _options.Rulesets[Rulesets.Generic.GenericRuleset.Id]();
        }

        _yaml.CheckKeys(map, "base", "disable", "stats");
        var baseNode = Get(map, "base");
        var baseId = baseNode is null ? Rulesets.Generic.GenericRuleset.Id
            : _yaml.Identifier(baseNode, "ruleset id") ?? Rulesets.Generic.GenericRuleset.Id;
        if (!_options.Rulesets.TryGetValue(baseId, out var factory))
        {
            _yaml.Error(Get(map, "base")!, "TW4004", $"Unknown ruleset '{baseId}'. Available: {string.Join(", ", _options.Rulesets.Keys)}.", 0);
            factory = _options.Rulesets[Rulesets.Generic.GenericRuleset.Id];
        }

        var baseRuleset = factory();
        var builder = baseRuleset.ToBuilder();
        var governingAttributes = new Dictionary<StatId, YamlNode>();
        if (Get(map, "disable") is { } disable)
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
                    _yaml.Error(item, "TW4005", $"Cannot disable '{id}': the '{baseId}' ruleset has no such stat.", 0);
                }
            }
        }

        if (Get(map, "stats") is { } stats)
        {
            foreach (var item in _yaml.Sequence(stats, "ruleset.stats"))
            {
                if (_yaml.Mapping(item, "stat") is not { } stat)
                {
                    continue;
                }

                _yaml.CheckKeys(stat, "id", "name", "category", "min", "max", "default", "governing-attribute");
                var id = _yaml.RequiredIdentifier(stat, "id");
                if (id is null)
                {
                    continue;
                }

                var category = Scalar(Get(stat, "category")) ?? "custom";
                if (!Enum.TryParse<StatCategory>(category, ignoreCase: true, out var parsedCategory))
                {
                    _yaml.Error(Get(stat, "category")!, "TW4003", $"Unknown stat category '{category}'. Use attribute, skill, resource, progression or custom.", 0);
                }

                var min = _yaml.Int(Get(stat, "min")) ?? 0;
                var max = _yaml.Int(Get(stat, "max")) ?? int.MaxValue;
                if (min > max)
                {
                    _yaml.Error(Get(stat, "min") ?? stat, "TW4003", $"Stat '{id}' minimum ({min}) must not be greater than its maximum ({max}).", 0);
                    continue;
                }

                var attributeNode = Get(stat, "governing-attribute");
                var attribute = attributeNode is null ? null : _yaml.Identifier(attributeNode, "governing attribute stat id");
                var definition = new StatDefinition(new StatId(id), Scalar(Get(stat, "name")) ?? id, parsedCategory,
                    min, max, _yaml.Int(Get(stat, "default")) ?? min, attribute is null ? null : new StatId(attribute));
                governingAttributes.Remove(definition.Id);
                if (attributeNode is not null)
                {
                    if (parsedCategory != StatCategory.Skill)
                    {
                        _yaml.Error(attributeNode, "TW4003", $"'governing-attribute' is only allowed on stats with category: skill; '{id}' is not a skill.", 0);
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
                _yaml.Error(targetNode, "TW4005", $"Stat '{skill}' governing-attribute target '{target}' is {reason}. Reference an enabled stat with category: attribute.", 0);
            }
            else if (attribute.Category != StatCategory.Attribute)
            {
                _yaml.Error(targetNode, "TW4003", $"Stat '{skill}' governing-attribute target '{target}' must have category: attribute.", 0);
            }
        }

        return ruleset;
    }
}
