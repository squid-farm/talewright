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
            ManifestVocabulary.Talemark,
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

        if (Get(game, ManifestVocabulary.Talemark) is not { } talemark)
        {
            _yaml.Diagnostics.Information(
                CompilerDiagnosticCodes.MissingTalemarkVersion,
                "game.talemark is not set. Add 'talemark: 1' to preserve the current integer arithmetic. " +
                "See the language-version migration guide: https://github.com/squid-farm/talewright/blob/main/docs/guide/language-versions.md",
                _yaml.At(game));
        }
        else if (talemark is not YamlScalarNode { Style: YamlDotNet.Core.ScalarStyle.Plain } scalar ||
                 !int.TryParse(scalar.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var talemarkVersion))
        {
            _yaml.Diagnostics.Error(
                CompilerDiagnosticCodes.InvalidConfigurationValue,
                "game.talemark must be the integer 1; quoted or other values are invalid.",
                _yaml.At(talemark));
        }
        else if (talemarkVersion != 1)
        {
            _yaml.Diagnostics.Error(
                CompilerDiagnosticCodes.InvalidConfigurationValue,
                $"Talemark version {talemarkVersion} is not supported by this release. Use 1 or omit game.talemark to use version 1.",
                _yaml.At(talemark));
        }
        else
        {
            manifest.TalemarkVersion = 1;
        }

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
            AnalyzeExpression(Get(map, ManifestVocabulary.When), ExpressionContextKind.Condition);
            var idNode = Get(map, ManifestVocabulary.Id);
            var id = _yaml.RequiredIdentifier(map, ManifestVocabulary.Id);
            if (id is not null && idNode is YamlScalarNode idScalar)
                _analysis?.NameDeclaration(idScalar, AuthoringSymbolKind.Trigger, _yaml.At(idScalar).File);

            var duplicate = id is not null && !ids.Add(id);
            if (duplicate)
            {
                _yaml.Diagnostics.Error(CompilerDiagnosticCodes.DuplicateConfigurationDeclaration, $"Trigger id '{id}' is declared more than once.", _yaml.At(idNode ?? item));
            }

            var when = _yaml.RequiredScalar(map, ManifestVocabulary.When);
            var actionNode = Get(map, ManifestVocabulary.Do);
            string? effect = null;
            SourceLocation? effectLocation = null;
            string? divert = null;
            SourceLocation? divertLocation = null;
            var actionIsValid = true;
            if (actionNode is null)
            {
                _yaml.RequiredScalar(map, ManifestVocabulary.Do);
            }
            else if (actionNode is YamlScalarNode)
            {
                effect = _yaml.RequiredScalar(map, ManifestVocabulary.Do);
                effectLocation = _yaml.At(actionNode);
                AnalyzeExpression(actionNode, ExpressionContextKind.Effects);
            }
            else if (actionNode is YamlMappingNode action)
            {
                _yaml.CheckKeys(action, ManifestVocabulary.Effect, ManifestVocabulary.Divert);
                var effectNode = Get(action, ManifestVocabulary.Effect);
                var divertNode = Get(action, ManifestVocabulary.Divert);
                if (effectNode is null && divertNode is null)
                {
                    _yaml.Error(actionNode, CompilerDiagnosticCodes.InvalidConfigurationValue,
                        "'do' must include at least an 'effect' or a 'divert'.", 0);
                    actionIsValid = false;
                }

                if (effectNode is not null)
                {
                    effect = _yaml.RequiredScalar(action, ManifestVocabulary.Effect);
                    effectLocation = _yaml.At(effectNode);
                    AnalyzeExpression(effectNode, ExpressionContextKind.Effects);
                }

                if (divertNode is not null)
                {
                    _analysis?.ReferenceNode(divertNode, AuthoringSymbolKind.Scene,
                        file: _yaml.At(divertNode).File);
                    var target = _yaml.RequiredScalar(action, ManifestVocabulary.Divert);
                    if (target is not null)
                    {
                        divert = _yaml.Identifier(divertNode, $"'{ManifestVocabulary.Divert}'");
                        divertLocation = _yaml.At(divertNode);
                    }
                }
            }
            else
            {
                _yaml.Error(actionNode, CompilerDiagnosticCodes.InvalidConfigurationValue,
                    "'do' must be an effect string or a mapping with 'effect' and/or 'divert'.", 0);
                actionIsValid = false;
            }

            if (when is null || !actionIsValid || actionNode is null || effect is null && divert is null
                || id is null || duplicate)
            {
                continue;
            }

            var once = Get(map, ManifestVocabulary.Once) is { } onceNode
                ? _yaml.Boolean(onceNode, ManifestVocabulary.Once, false)
                : false;
            var save = Get(map, ManifestVocabulary.Save) is { } saveNode
                ? _yaml.Boolean(saveNode, ManifestVocabulary.Save, false)
                : false;
            triggers.Add(new RawTrigger(id, when, _yaml.At(Get(map, ManifestVocabulary.When)!),
                effect, effectLocation, divert, divertLocation, once, save, _yaml.At(item)));
        }

        void AnalyzeExpression(YamlNode? node, ExpressionContextKind kind)
        {
            if (_analysis is not null && node is YamlScalarNode scalar)
            {
                _analysis.Scalar(scalar, _yaml.At(scalar).File);
                _analysis.Expression(kind, scalar.Value ?? string.Empty, _yaml.At(scalar), cacheForReuse: true);
            }
        }
    }
}
