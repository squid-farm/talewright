using Talewright.Core.Identity;
using Talewright.Core.World;
using Talewright.Rules.Expressions;
using Talewright.Rules.Triggers;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Model;
using Talewright.Talemark.Parsing;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Talewright.Talemark.Analysis;

namespace Talewright.Talemark.Compilation;

/// <summary>
/// Compiles a story (story.talewright.yaml plus <c>.tale</c> files) into a validated <see cref="StoryDefinition"/>.
/// </summary>
/// <remarks>
/// Pipeline:
/// <list type="number">
/// <item>Read story.talewright.yaml into a <see cref="WorldDefinition"/> (ruleset, characters, items, factions…).</item>
/// <item>Discover scene ids and effect assignment targets using the Talemark parser and expression lexer,
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
        var textVariables = manifest.World.TextVariables.ToDictionary();
        var declarations = new List<VariableDeclaration>();
        var discoveredNumbers = new Dictionary<VarId, int>();
        void DiscoverEffects(string text, SourceLocation location)
        {
            ExpressionParser.CollectDeclarations(text, flags, discoveredNumbers,
                typedDeclaration: declarations.Add, location: location);
        }
        var discoverySymbols = new StorySymbols
        {
            World = manifest.World,
            Flags = flags,
            Variables = variables.Keys.Concat(manifest.World.TextVariables.Keys).ToHashSet(),
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

        foreach (var trigger in manifest.Triggers)
        {
            DiscoverEffects(trigger.Do, trigger.DoLocation);
        }

        var unresolved = VariableDiscovery.Resolve(manifest.World, flags, scenes.Keys.ToHashSet(),
            declarations, variables, textVariables, options, diagnostics);
        var world = new WorldDefinition(manifest.World.Ruleset, manifest.World.Characters.Values,
            manifest.World.Items.Values, manifest.World.Factions.Values, flags, variables, textVariables);
        analysis?.World(world, unresolved);
        var symbols = new StorySymbols
        {
            World = world,
            Flags = world.Flags,
            Variables = world.Variables.Keys.Concat(world.TextVariables.Keys).Concat(unresolved).ToHashSet(),
            UnresolvedVariables = unresolved,
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

        // Saves store this fingerprint: the hashed bytes must stay identical to length-prefixed UTF-8.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var encoder = Encoding.UTF8.GetEncoder();
        void Append(string text)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, Encoding.UTF8.GetByteCount(text));
            hash.AppendData(length);

            Span<byte> buffer = stackalloc byte[4096];
            var remaining = text.AsSpan();
            encoder.Reset();
            bool completed;
            do
            {
                encoder.Convert(remaining, buffer, flush: true, out var charsUsed, out var bytesUsed, out completed);
                hash.AppendData(buffer[..bytesUsed]);
                remaining = remaining[charsUsed..];
            } while (!completed);
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
