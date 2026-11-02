using System.Globalization;
using Talewright.Core.World;
using Talewright.Rules.Checks;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using YamlDotNet.RepresentationModel;
using static Talewright.Talemark.Parsing.StoryYamlContext;

namespace Talewright.Talemark.Parsing;

/// <summary>Reads assembled story configuration and coordinates its section readers.</summary>
internal sealed class StoryYamlReader(StoryYamlContext yaml, CompilerOptions options, AnalysisCollector? analysis)
{
    private readonly StoryYamlContext _yaml = yaml;
    private readonly CompilerOptions _options = options;
    private readonly AnalysisCollector? _analysis = analysis;

    public static StoryManifest? Read(StorySource source, DiagnosticBag diagnostics, CompilerOptions options,
        AnalysisCollector? analysis = null)
    {
        var yaml = new StoryYamlContext(StorySource.ManifestFileName, diagnostics);
        var root = StoryYamlAssembler.Read(source, yaml, analysis);
        return root is null ? null : new StoryYamlReader(yaml, options, analysis).Read(root);
    }

    private StoryManifest Read(YamlMappingNode root)
    {
        var gameNode = Get(root, "game");
        if (gameNode is null)
        {
            _yaml.Diagnostics.Error("TW4002", "The root story configuration must declare a 'game:' mapping.", _yaml.At(root));
        }
        var game = gameNode is null ? new YamlMappingNode() : _yaml.Mapping(gameNode, "game") ?? new YamlMappingNode();
        _yaml.CheckKeys(game, "id", "title", "version", "start", "randomness", "seed", "undo", "ruleset", "creation");

        var ruleset = new StoryYamlRulesetReader(_yaml, _options, _analysis).Read(Get(game, "ruleset"));
        var world = new StoryYamlWorldReader(_yaml);
        var items = world.ReadItems(Get(root, "items"), ruleset);
        var factions = world.ReadFactions(Get(root, "factions"));
        var characters = world.ReadCharacters(Get(root, "characters"), root, ruleset, items);
        var flags = world.ReadFlags(Get(root, "flags"));
        var (numericVars, textVars) = world.ReadVars(Get(root, "vars"));

        var manifest = new StoryManifest
        {
            World = new WorldDefinition(ruleset, characters, items.Values, factions, flags, numericVars, textVars),
            Id = _yaml.RequiredIdentifier(game, "id") ?? string.Empty,
            Title = _yaml.RequiredScalar(game, "title") ?? string.Empty,
            Version = Scalar(Get(game, "version")) ?? "0.0.0",
            Start = _yaml.RequiredIdentifier(game, "start") ?? string.Empty,
            StartLocation = Get(game, "start") is { } startNode ? _yaml.At(startNode) : _yaml.At(game),
        };

        if (Get(game, "randomness") is { } randomness)
        {
            manifest.Randomness = Scalar(randomness) switch
            {
                "off" => RandomnessMode.Off,
                "checks-only" => RandomnessMode.ChecksOnly,
                var other => _yaml.Error(randomness, "TW4003", $"randomness must be 'off' or 'checks-only', not '{other}'.", RandomnessMode.ChecksOnly),
            };
        }

        if (Get(game, "seed") is { } seed)
        {
            manifest.Seed = ulong.TryParse(Scalar(seed), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value
                : _yaml.Error<ulong?>(seed, "TW4003", "seed must be a non-negative whole number.", null);
        }

        if (Get(game, "undo") is { } undo)
        {
            manifest.UndoEnabled = bool.TryParse(Scalar(undo), out var enabled)
                ? enabled
                : _yaml.Error(undo, "TW4003", "undo must be true or false.", true);
        }

        ReadTriggers(Get(root, "triggers"), manifest.Triggers);
        manifest.Creation = world.ReadCreation(Get(game, "creation"), ruleset);
        return manifest;
    }

    private void ReadTriggers(YamlNode? node, List<RawTrigger> triggers)
    {
        if (node is null)
        {
            return;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in _yaml.Sequence(node, "triggers"))
        {
            if (_yaml.Mapping(item, "trigger") is not { } map)
            {
                continue;
            }

            _yaml.CheckKeys(map, "id", "when", "do", "once");
            if (_analysis is not null)
            {
                foreach (var (key, kind) in new[] { ("when", ExpressionContextKind.Condition), ("do", ExpressionContextKind.Effects) })
                {
                    if (Get(map, key) is not YamlScalarNode scalar) continue;
                    _analysis.Scalar(scalar, _yaml.At(scalar).File);
                    _analysis.Expression(kind, scalar.Value ?? string.Empty, _yaml.At(scalar));
                }
            }
            var idNode = Get(map, "id");
            var id = _yaml.RequiredIdentifier(map, "id");
            if (id is null)
            {
                continue;
            }

            if (!ids.Add(id))
            {
                _yaml.Diagnostics.Error("TW4007", $"Trigger id '{id}' is declared more than once.", _yaml.At(idNode ?? item));
                continue;
            }

            var when = _yaml.RequiredScalar(map, "when");
            var effects = _yaml.RequiredScalar(map, "do");
            if (when is null || effects is null)
            {
                continue;
            }

            var once = Get(map, "once") is { } onceNode
                ? bool.TryParse(Scalar(onceNode), out var onceValue)
                    ? onceValue
                    : _yaml.Error(onceNode, "TW4003", "once must be true or false.", false)
                : false;
            triggers.Add(new RawTrigger(id, when, _yaml.At(Get(map, "when")!), effects, _yaml.At(Get(map, "do")!), once));
        }
    }
}
