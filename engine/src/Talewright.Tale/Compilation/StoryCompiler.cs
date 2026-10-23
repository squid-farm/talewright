using Talewright.Core.Identity;
using Talewright.Core.World;
using Talewright.Rules.Expressions;
using Talewright.Rules.Triggers;
using Talewright.Tale.Diagnostics;
using Talewright.Tale.Model;
using Talewright.Tale.Parsing;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Talewright.Tale.Analysis;

namespace Talewright.Tale.Compilation;

/// <summary>
/// Compiles a story (story.talewright.yaml plus <c>.tale</c> files) into a validated <see cref="StoryDefinition"/>.
/// </summary>
/// <remarks>
/// Pipeline:
/// <list type="number">
/// <item>Read story.talewright.yaml into a <see cref="WorldDefinition"/> (ruleset, characters, items, factions…).</item>
/// <item>Discover scene ids and effect assignment targets using the Tale parser and expression lexer,
/// so forward references and auto-declared flags/variables resolve.</item>
/// <item>Parse each <c>.tale</c> file (expressions are type-checked and resolved as they are parsed).</item>
/// <item>Parse triggers, then run the whole-story <see cref="StoryValidator"/>.</item>
/// </list>
/// Any error means no story is produced; all diagnostics are returned either way.
/// </remarks>
public static class StoryCompiler
{
    /// <summary>Compiles <paramref name="source"/>.</summary>
    public static CompilationResult Compile(StorySource source, CompilerOptions? options = null)
        => CompileCore(source, options ?? CompilerOptions.Default, null);

    internal static CompilationResult CompileForAnalysis(StorySource source, CompilerOptions options, AnalysisCollector collector)
        => CompileCore(source, options, collector);

    private static CompilationResult CompileCore(StorySource source, CompilerOptions options, AnalysisCollector? analysis)
    {
        var diagnostics = new DiagnosticBag();
        analysis?.Registrations();

        if (!source.Files.TryGetValue(StorySource.ManifestFileName, out var yaml))
        {
            diagnostics.Error("TW4002", "The story has no story.talewright.yaml at its root.", new SourceLocation(StorySource.ManifestFileName, 0, 0));
            analysis?.DiscoverWithoutManifest();
            return new CompilationResult(null, diagnostics.Items);
        }

        var manifest = StoryYamlReader.Read(StorySource.ManifestFileName, yaml, diagnostics, options, analysis);
        if (manifest is null)
        {
            analysis?.DiscoverWithoutManifest();
            return new CompilationResult(null, diagnostics.Items);
        }

        var taleFiles = source.TaleFiles.ToList();
        if (taleFiles.Count == 0)
        {
            diagnostics.Error("TW3001", "The story has no .tale files.", new SourceLocation(StorySource.ManifestFileName, 0, 0));
        }

        var scenes = new Dictionary<SceneId, SourceLocation>();
        var flags = manifest.World.Flags.ToHashSet();
        var variables = manifest.World.Variables.ToDictionary();
        void DiscoverEffects(string text) => ExpressionParser.CollectDeclarations(text, flags, variables);
        var discoverySymbols = new StorySymbols
        {
            World = manifest.World,
            Flags = flags,
            Variables = variables.Keys.ToHashSet(),
            Scenes = new HashSet<SceneId>(),
            Predicates = options.Predicates,
            Modifiers = options.Modifiers,
        };
        foreach (var (path, text) in taleFiles)
        {
            TaleParser.Discover(path, text, discoverySymbols, DiscoverEffects, (id, location) =>
            {
                analysis?.Scene(id, location);
                if (!scenes.TryAdd(id, location))
                {
                    diagnostics.Error("TW3010", $"Scene '{id}' is declared more than once (first at {scenes[id]}).", location);
                }
            });
        }

        foreach (var text in manifest.EffectText)
        {
            DiscoverEffects(text);
        }

        var world = new WorldDefinition(manifest.World.Ruleset, manifest.World.Characters.Values,
            manifest.World.Items.Values, manifest.World.Factions.Values, flags, variables);
        analysis?.World(world);
        var symbols = new StorySymbols
        {
            World = world,
            Flags = world.Flags,
            Variables = world.Variables.Keys.ToHashSet(),
            Scenes = scenes.Keys.ToHashSet(),
            Predicates = options.Predicates,
            Modifiers = options.Modifiers,
            Analysis = analysis,
        };

        var parsed = new List<ParsedScene>();
        var seen = new HashSet<SceneId>();
        foreach (var (path, text) in taleFiles)
        {
            foreach (var scene in TaleParser.Parse(path, text, symbols, diagnostics))
            {
                // Discovery reported duplicates; keep the first so the rest still validates.
                if (seen.Add(scene.Definition.Id))
                {
                    parsed.Add(scene);
                }
            }
        }

        var triggers = new List<TriggerDefinition>();
        foreach (var raw in manifest.Triggers)
        {
            var when = ExpressionParser.ParseCondition(raw.When, raw.WhenLocation, symbols, diagnostics);
            var effects = ExpressionParser.ParseEffects(raw.Do, raw.DoLocation, symbols, diagnostics);
            if (when is not null && effects is not null)
            {
                triggers.Add(new TriggerDefinition(raw.Id, when, effects, raw.Once));
            }
        }

        StoryValidator.Validate(manifest, parsed, diagnostics);

        if (diagnostics.HasErrors)
        {
            return new CompilationResult(null, diagnostics.Items);
        }

        var story = new StoryDefinition
        {
            Id = manifest.Id,
            Title = manifest.Title,
            Version = manifest.Version,
            ContentFingerprint = Fingerprint(source, options.CompatibilityId),
            UndoEnabled = manifest.UndoEnabled,
            Creation = manifest.Creation,
            Start = new SceneId(manifest.Start),
            Randomness = manifest.Randomness,
            Seed = manifest.Seed,
            World = world,
            Scenes = parsed.Select(p => p.Definition).ToList(),
            Triggers = triggers,
            Predicates = options.Predicates,
        };
        return new CompilationResult(story, diagnostics.Items)
        {
            SourceMap = new StorySourceMap(source, parsed.ToDictionary(scene => scene.Definition.Id,
                scene => new SceneSourceRange(scene.Location.File, scene.Location.Line, scene.EndLine))),
        };
    }

    private static string? Fingerprint(StorySource source, string? compatibilityId)
    {
        if (string.IsNullOrWhiteSpace(compatibilityId))
        {
            return null;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        Append("talewright-replay-v1");
        Append(compatibilityId);
        foreach (var (path, text) in source.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            Append(path);
            Append(text);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

}
