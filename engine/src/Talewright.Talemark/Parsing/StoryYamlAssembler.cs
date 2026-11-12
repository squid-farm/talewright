using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Vocabulary;
using YamlDotNet.RepresentationModel;
using static Talewright.Talemark.Parsing.StoryYamlContext;

namespace Talewright.Talemark.Parsing;

/// <summary>Combines the root and supporting configuration documents while retaining their source locations.</summary>
internal static class StoryYamlAssembler
{
    internal static YamlMappingNode? Read(StorySource source, StoryYamlContext yaml, AnalysisCollector? analysis)
    {
        var root = yaml.Parse(StorySource.ManifestFileName, source.Files[StorySource.ManifestFileName]);
        if (root is null) return null;
        analysis?.Manifest(root);
        var sections = new Dictionary<string, YamlMappingNode>(StringComparer.Ordinal);
        var declarations = new Dictionary<string, Dictionary<string, SourceLocation>>(StringComparer.Ordinal);
        var flags = new YamlMappingNode();
        var flagLocations = new Dictionary<string, SourceLocation>(StringComparer.Ordinal);
        YamlNode? triggers = null;
        SourceLocation? triggerLocation = null;
        void Duplicate(string kind, string id, YamlNode node, SourceLocation first) =>
            yaml.Diagnostics.Error(CompilerDiagnosticCodes.DuplicateConfigurationDeclaration, $"{kind} '{id}' is declared more than once (first at {first}).", yaml.At(node));
        void AddFlag(YamlNode key, YamlNode value)
        {
            if (Scalar(key) is not { } id)
            {
                yaml.Identifier(key, IdentifierLabels.Flag);
                return;
            }

            if (!flagLocations.TryAdd(id, yaml.At(key)))
            {
                Duplicate(ManifestVocabulary.Flags, id, key, flagLocations[id]);
                return;
            }

            flags.Add(key, value);
        }
        void Collect(YamlMappingNode document)
        {
            yaml.CheckKeys(document, ManifestVocabulary.Game, ManifestVocabulary.Characters, ManifestVocabulary.Factions, ManifestVocabulary.Items, ManifestVocabulary.Flags, ManifestVocabulary.Vars, ManifestVocabulary.Triggers);
            foreach (var name in new[] { ManifestVocabulary.Characters, ManifestVocabulary.Factions, ManifestVocabulary.Items, ManifestVocabulary.Vars })
            {
                if (Get(document, name) is not { } node || yaml.Mapping(node, name) is not { } map) continue;
                if (!sections.TryGetValue(name, out var combined))
                {
                    sections.Add(name, combined = new YamlMappingNode());
                    declarations.Add(name, new(StringComparer.Ordinal));
                }
                foreach (var (key, value) in map.Children)
                {
                    var id = Scalar(key);
                    if (id is not null && !declarations[name].TryAdd(id, yaml.At(key)))
                        Duplicate(name, id, key, declarations[name][id]);
                    else
                        combined.Add(key, value);
                }
            }
            if (Get(document, ManifestVocabulary.Flags) is { } flagNode)
            {
                if (flagNode is YamlSequenceNode sequence)
                {
                    foreach (var flag in sequence.Children)
                        AddFlag(flag, new YamlScalarNode(ExpressionVocabulary.False));
                }
                else if (flagNode is YamlMappingNode map)
                {
                    foreach (var (key, value) in map.Children)
                        AddFlag(key, value);
                }
                else
                {
                    yaml.Diagnostics.Error(CompilerDiagnosticCodes.InvalidConfigurationValue,
                        "'flags' must be a list or mapping.", yaml.At(flagNode));
                }
            }
            if (Get(document, ManifestVocabulary.Triggers) is { } triggerNode)
            {
                if (triggerLocation is { } first)
                    yaml.Diagnostics.Error(CompilerDiagnosticCodes.DuplicateConfigurationDeclaration, $"Only one 'triggers' section is allowed (first at {first}).", yaml.At(triggerNode));
                else
                {
                    triggers = triggerNode;
                    triggerLocation = yaml.At(triggerNode);
                }
            }
        }
        Collect(root);
        foreach (var (file, text) in source.Files
            .Where(file => file.Key != StorySource.ManifestFileName && StorySource.IsConfigurationFile(file.Key))
            .OrderBy(file => file.Key, StringComparer.Ordinal))
        {
            if (file.EndsWith("/" + StorySource.ManifestFileName, StringComparison.Ordinal))
            {
                yaml.Diagnostics.Error(CompilerDiagnosticCodes.MissingRequiredConfiguration,
                    $"'{StorySource.ManifestFileName}' is reserved for the single root story configuration; found additional manifest '{file}'.",
                    new SourceLocation(file, 1, 1));
                continue;
            }
            if (!file.Contains('/'))
            {
                yaml.Diagnostics.Error(CompilerDiagnosticCodes.MissingRequiredConfiguration,
                    $"Only '{StorySource.ManifestFileName}' is allowed at the story root; found extra configuration file '{file}'.",
                    new SourceLocation(file, 1, 1));
                continue;
            }
            if (yaml.Parse(file, text) is not { } document) continue;
            analysis?.Manifest(document, file);
            if (Get(document, ManifestVocabulary.Game) is { } game)
                yaml.Diagnostics.Error(CompilerDiagnosticCodes.MissingRequiredConfiguration, "'game:' is allowed only in the root story configuration.", yaml.At(game));
            Collect(document);
        }
        var assembled = new YamlMappingNode();
        if (Get(root, ManifestVocabulary.Game) is { } rootGame) assembled.Add(ManifestVocabulary.Game, rootGame);
        foreach (var (name, map) in sections) assembled.Add(name, map);
        assembled.Add(ManifestVocabulary.Flags, flags);
        if (triggers is not null) assembled.Add(ManifestVocabulary.Triggers, triggers);
        return assembled;
    }
}
