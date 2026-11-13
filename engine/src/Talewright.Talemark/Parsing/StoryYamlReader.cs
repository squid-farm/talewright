using System.Globalization;
using Talewright.Core.Identity;
using Talewright.Core.World;
using Talewright.Rules.Checks;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Model;
using Talewright.Talemark.Vocabulary;
using YamlDotNet.RepresentationModel;
using static Talewright.Talemark.Parsing.StoryYamlContext;

namespace Talewright.Talemark.Parsing;

/// <summary>Reads assembled story configuration and coordinates its section readers.</summary>
internal sealed class StoryYamlReader(StoryYamlContext yaml, CompilerOptions options,
    StatDiagnosticGuidance statGuidance, AnalysisCollector? analysis)
{
    private readonly StoryYamlContext _yaml = yaml;
    private readonly CompilerOptions _options = options;
    private readonly StatDiagnosticGuidance _statGuidance = statGuidance;
    private readonly AnalysisCollector? _analysis = analysis;

    public static StoryManifest? Read(StorySource source, DiagnosticBag diagnostics, CompilerOptions options,
        StatDiagnosticGuidance statGuidance, AnalysisCollector? analysis = null)
    {
        var yaml = new StoryYamlContext(StorySource.ManifestFileName, diagnostics);
        var root = StoryYamlAssembler.Read(source, yaml, analysis);
        return root is null ? null : new StoryYamlReader(yaml, options, statGuidance, analysis).Read(root);
    }

    private StoryManifest Read(YamlMappingNode root)
    {
        var gameNode = Get(root, ManifestVocabulary.Game);
        if (gameNode is null)
        {
            _yaml.Diagnostics.Error(CompilerDiagnosticCodes.MissingRequiredConfiguration, "The root story configuration must declare a 'game:' mapping.", _yaml.At(root));
        }
        var game = gameNode is null ? new YamlMappingNode() : _yaml.Mapping(gameNode, ManifestVocabulary.Game) ?? new YamlMappingNode();
        _yaml.CheckKeys(game, ManifestVocabulary.Id, ManifestVocabulary.Title, ManifestVocabulary.Version,
            ManifestVocabulary.Start, ManifestVocabulary.Randomness, ManifestVocabulary.Seed,
            ManifestVocabulary.Undo, ManifestVocabulary.Autosave, ManifestVocabulary.CharacterSheet,
            ManifestVocabulary.Ruleset, ManifestVocabulary.Creation);

        var ruleset = new StoryYamlRulesetReader(_yaml, _options, _statGuidance, _analysis)
            .Read(Get(game, ManifestVocabulary.Ruleset));
        var world = new StoryYamlWorldReader(_yaml, _statGuidance);
        var items = world.ReadItems(Get(root, ManifestVocabulary.Items), ruleset);
        var factions = world.ReadFactions(Get(root, ManifestVocabulary.Factions));
        var characters = world.ReadCharacters(Get(root, ManifestVocabulary.Characters), root, ruleset, items);
        var flags = world.ReadFlags(Get(root, ManifestVocabulary.Flags));
        var declaredFlags = flags.Keys.ToArray();
        var (numericVars, textVars) = world.ReadVars(Get(root, ManifestVocabulary.Vars));

        var manifest = new StoryManifest
        {
            World = new WorldDefinition(ruleset, characters, items.Values, factions, declaredFlags,
                numericVars, textVars, flags),
            CharacterLocations = world.CharacterLocations,
            Id = _yaml.RequiredIdentifier(game, ManifestVocabulary.Id) ?? string.Empty,
            Title = _yaml.RequiredScalar(game, ManifestVocabulary.Title) ?? string.Empty,
            Version = Scalar(Get(game, ManifestVocabulary.Version)) ?? "0.0.0",
            Start = _yaml.RequiredIdentifier(game, ManifestVocabulary.Start) ?? string.Empty,
            StartLocation = Get(game, ManifestVocabulary.Start) is { } startNode ? _yaml.At(startNode) : _yaml.At(game),
        };

        if (Get(game, ManifestVocabulary.Randomness) is { } randomness)
        {
            manifest.Randomness = Scalar(randomness) switch
            {
                ManifestVocabulary.RandomnessOff => RandomnessMode.Off,
                ManifestVocabulary.RandomnessChecksOnly => RandomnessMode.ChecksOnly,
                var other => _yaml.Error(randomness, CompilerDiagnosticCodes.InvalidConfigurationValue, $"randomness must be 'off' or 'checks-only', not '{other}'.", RandomnessMode.ChecksOnly),
            };
        }

        if (Get(game, ManifestVocabulary.Seed) is { } seed)
        {
            manifest.Seed = ulong.TryParse(Scalar(seed), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value
                : _yaml.Error<ulong?>(seed, CompilerDiagnosticCodes.InvalidConfigurationValue, "seed must be a non-negative whole number.", null);
        }

        if (Get(game, ManifestVocabulary.Undo) is { } undo)
        {
            manifest.UndoEnabled = _yaml.Boolean(undo, ManifestVocabulary.Undo, true);
        }

        if (Get(game, ManifestVocabulary.Autosave) is { } autosave)
        {
            manifest.AutosaveEnabled = _yaml.Boolean(autosave, ManifestVocabulary.Autosave, true);
        }

        if (Get(game, ManifestVocabulary.CharacterSheet) is { } characterSheet)
        {
            (manifest.CharacterSheet, manifest.CharacterSheetStatRules) = ReadCharacterSheet(characterSheet, ruleset);
        }

        ReadTriggers(Get(root, ManifestVocabulary.Triggers), manifest.Triggers);
        manifest.Creation = world.ReadCreation(Get(game, ManifestVocabulary.Creation), ruleset);
        return manifest;
    }

    private (CharacterSheetVisibility Visibility, IReadOnlyList<RawStatVisibilityRule> StatRules)
        ReadCharacterSheet(YamlNode node, Talewright.Core.Rulesets.Ruleset ruleset)
    {
        if (node is YamlScalarNode scalar && bool.TryParse(scalar.Value, out var shown))
        {
            return (shown ? CharacterSheetVisibility.Shown : CharacterSheetVisibility.Hidden, []);
        }

        if (node is not YamlMappingNode map)
        {
            return (_yaml.Error(node, CompilerDiagnosticCodes.InvalidConfigurationValue,
                $"{ManifestVocabulary.CharacterSheet} must be true, false, or a mapping of " +
                $"{ManifestVocabulary.Stats}, {ManifestVocabulary.Inventory} and {ManifestVocabulary.Standing} to true or false.",
                CharacterSheetVisibility.Shown), []);
        }

        _yaml.CheckKeys(map, ManifestVocabulary.Stats, ManifestVocabulary.Inventory, ManifestVocabulary.Standing);
        var (statsShown, statRules) = ReadStatVisibility(Get(map, ManifestVocabulary.Stats), ruleset);
        return (new CharacterSheetVisibility(
            statsShown, Section(ManifestVocabulary.Inventory), Section(ManifestVocabulary.Standing)), statRules);

        bool Section(string key) => Get(map, key) is not { } value
            ? true
            : _yaml.Boolean(value, $"{ManifestVocabulary.CharacterSheet}.{key}", true);
    }

    private (bool Shown, IReadOnlyList<RawStatVisibilityRule> Rules) ReadStatVisibility(
        YamlNode? node, Talewright.Core.Rulesets.Ruleset ruleset)
    {
        if (node is null)
        {
            return (true, []);
        }

        if (_yaml.Mapping(node, $"{ManifestVocabulary.CharacterSheet}.{ManifestVocabulary.Stats}") is not { } map)
        {
            return (true, []);
        }

        var shown = Get(map, ManifestVocabulary.Shown) is not { } shownNode
            || _yaml.Boolean(shownNode, $"{ManifestVocabulary.CharacterSheet}.{ManifestVocabulary.Stats}.{ManifestVocabulary.Shown}", true);
        var rules = new List<RawStatVisibilityRule>();
        foreach (var (key, value) in map.Children)
        {
            if (Scalar(key) == ManifestVocabulary.Shown)
            {
                continue;
            }

            var rawId = _yaml.Identifier(key, IdentifierLabels.Stat);
            if (rawId is null)
            {
                continue;
            }

            var stat = new StatId(rawId);
            if (!ruleset.StatsById.ContainsKey(stat))
            {
                _yaml.Error(key, CompilerDiagnosticCodes.UnknownConfigurationReference,
                    $"Unknown stat '{stat}' (ruleset '{ruleset.Id}')." + _statGuidance.ForMissingStat(stat), 0);
                continue;
            }

            var scalarValue = Scalar(value);
            if (bool.TryParse(scalarValue, out var visible))
            {
                rules.Add(new RawStatVisibilityRule(stat, visible, null, _yaml.At(value)));
            }
            else if (scalarValue is not null)
            {
                if (value is YamlScalarNode scalar)
                {
                    _analysis?.Expression(ExpressionContextKind.Condition, scalarValue, _yaml.At(scalar), cacheForReuse: true);
                }
                rules.Add(new RawStatVisibilityRule(stat, null, scalarValue, _yaml.At(value)));
            }
            else
            {
                _yaml.Error(value, CompilerDiagnosticCodes.InvalidConfigurationValue,
                    $"{ManifestVocabulary.CharacterSheet}.{ManifestVocabulary.Stats}.{stat} must be true, false, or a condition expression.", 0);
            }
        }

        return (shown, rules.AsReadOnly());
    }

    private void ReadTriggers(YamlNode? node, List<RawTrigger> triggers)
    {
        if (node is null)
        {
            return;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in _yaml.Sequence(node, ManifestVocabulary.Triggers))
        {
            if (_yaml.Mapping(item, "trigger") is not { } map)
            {
                continue;
            }

            _yaml.CheckKeys(map, ManifestVocabulary.Id, ManifestVocabulary.When, ManifestVocabulary.Do, ManifestVocabulary.Once, ManifestVocabulary.Save);
            if (_analysis is not null)
            {
                foreach (var (key, kind) in new[] { (ManifestVocabulary.When, ExpressionContextKind.Condition), (ManifestVocabulary.Do, ExpressionContextKind.Effects) })
                {
                    if (Get(map, key) is not YamlScalarNode scalar) continue;
                    _analysis.Scalar(scalar, _yaml.At(scalar).File);
                    _analysis.Expression(kind, scalar.Value ?? string.Empty, _yaml.At(scalar), cacheForReuse: true);
                }
            }
            var idNode = Get(map, ManifestVocabulary.Id);
            var id = _yaml.RequiredIdentifier(map, ManifestVocabulary.Id);
            if (id is null)
            {
                continue;
            }

            if (idNode is YamlScalarNode idScalar)
                _analysis?.NameDeclaration(idScalar, AuthoringSymbolKind.Trigger, _yaml.At(idScalar).File);

            if (!ids.Add(id))
            {
                _yaml.Diagnostics.Error(CompilerDiagnosticCodes.DuplicateConfigurationDeclaration, $"Trigger id '{id}' is declared more than once.", _yaml.At(idNode ?? item));
                continue;
            }

            var when = _yaml.RequiredScalar(map, ManifestVocabulary.When);
            var effects = _yaml.RequiredScalar(map, ManifestVocabulary.Do);
            if (when is null || effects is null)
            {
                continue;
            }

            var once = Get(map, ManifestVocabulary.Once) is { } onceNode
                ? _yaml.Boolean(onceNode, ManifestVocabulary.Once, false)
                : false;
            var save = Get(map, ManifestVocabulary.Save) is { } saveNode
                ? _yaml.Boolean(saveNode, ManifestVocabulary.Save, false)
                : false;
            triggers.Add(new RawTrigger(id, when, _yaml.At(Get(map, ManifestVocabulary.When)!), effects, _yaml.At(Get(map, ManifestVocabulary.Do)!), once, save));
        }
    }
}
