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
/// <item>Parse each <c>.tale</c> file's syntax once, then collect scene ids and effect assignment targets from it
/// with the expression lexer, so forward references and auto-declared flags/variables resolve.</item>
/// <item>Bind each file's syntax (expressions are type-checked and resolved as they are bound).</item>
/// <item>Parse triggers, then run the whole-story <see cref="StoryValidator"/>.</item>
/// </list>
/// Any error means no story is produced; all diagnostics are returned either way.
/// </remarks>
public static class StoryCompiler
{
    private const string ReplayVersion = "talewright-replay-v3";

    /// <summary>Compiles <paramref name="source"/>.</summary>
    public static CompilationResult Compile(StorySource source, CompilerOptions? options = null)
        => CompileCore(source, options ?? CompilerOptions.Default, null, CancellationToken.None);

    internal static CompilationResult CompileForAnalysis(StorySource source, CompilerOptions options, AnalysisCollector collector,
        CancellationToken cancellationToken, Func<string, string, CancellationToken, TaleFileSyntax>? syntax = null)
        => CompileCore(source, options, collector, cancellationToken, syntax);

    private static CompilationResult CompileCore(StorySource source, CompilerOptions options, AnalysisCollector? analysis,
        CancellationToken cancellationToken, Func<string, string, CancellationToken, TaleFileSyntax>? parseSyntax = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        parseSyntax ??= static (_, text, token) => TaleSyntaxParser.Parse(text, token);
        var diagnostics = new DiagnosticBag();
        analysis?.Registrations();

        if (!source.Files.ContainsKey(StorySource.ManifestFileName))
        {
            diagnostics.Error(CompilerDiagnosticCodes.MissingRequiredConfiguration, "The story has no story.talewright.yaml at its root.", new SourceLocation(StorySource.ManifestFileName, 0, 0));
            analysis?.DiscoverWithoutManifest(cancellationToken, parseSyntax);
            return new CompilationResult(null, diagnostics.Items);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var statGuidance = new StatDiagnosticGuidance(options);
        var manifest = StoryYamlReader.Read(source, diagnostics, options, statGuidance, analysis);
        cancellationToken.ThrowIfCancellationRequested();
        if (manifest is null)
        {
            analysis?.DiscoverWithoutManifest(cancellationToken, parseSyntax);
            return new CompilationResult(null, diagnostics.Items);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var taleFiles = source.TaleFiles.ToList();
        cancellationToken.ThrowIfCancellationRequested();
        if (taleFiles.Count == 0)
        {
            diagnostics.Error(CompilerDiagnosticCodes.TextOutsideSceneOrNoFiles, "The story has no .tale files.", new SourceLocation(StorySource.ManifestFileName, 0, 0));
        }

        var scenes = new Dictionary<SceneId, SourceLocation>();
        var flags = manifest.World.Flags.ToHashSet();
        var variables = manifest.World.Variables.ToDictionary();
        var textVariables = manifest.World.TextVariables.ToDictionary();
        var declarations = new List<VariableDeclaration>();
        var discoveredNumbers = new Dictionary<VarId, int>();
        void DiscoverEffects(string text, SourceLocation location)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExpressionParser.CollectDeclarations(text, flags, discoveredNumbers,
                typedDeclaration: declarations.Add, location: location);
        }
        var syntaxes = new List<(string Path, TaleFileSyntax Syntax)>(taleFiles.Count);
        foreach (var (path, text) in taleFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var syntax = parseSyntax(path, text, cancellationToken);
            syntaxes.Add((path, syntax));
            analysis?.ClassificationSources.Add((path, syntax));
            TaleDeclarations.Collect(path, syntax, (id, location) =>
            {
                analysis?.Scene(id, location);
                if (!scenes.TryAdd(id, location))
                {
                    diagnostics.Error(CompilerDiagnosticCodes.DuplicateScene, $"Scene '{id}' is declared more than once (first at {scenes[id]}).", location);
                }
            }, DiscoverEffects, cancellationToken);
        }

        foreach (var (scene, location) in scenes)
        {
            if (manifest.CharacterLocations.TryGetValue(new EntityId(scene.Value), out var characterLocation))
            {
                diagnostics.Error(CompilerDiagnosticCodes.SceneCharacterIdCollision,
                    $"Scene '{scene}' and character '{scene}' use the same id; character declared at {characterLocation}.",
                    location);
            }
        }

        foreach (var trigger in manifest.Triggers)
        {
            DiscoverEffects(trigger.Do, trigger.DoLocation);
        }

        var unresolved = VariableDiscovery.Resolve(manifest.World, flags, scenes.Keys.ToHashSet(),
            declarations, variables, textVariables, options, diagnostics);
        var world = new WorldDefinition(manifest.World.Ruleset, manifest.World.Characters.Values,
            manifest.World.Items.Values, manifest.World.Factions.Values, flags, variables, textVariables,
            manifest.World.InitialFlags);
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
            StatGuidance = statGuidance,
            Analysis = analysis,
            ImageSource = source,
        };
        if (analysis is not null) analysis.SymbolContext = symbols;

        var parsed = new List<ParsedScene>();
        var seen = new HashSet<SceneId>();
        foreach (var (path, syntax) in syntaxes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var scene in TaleBinder.Parse(path, syntax, symbols, diagnostics, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Discovery reported duplicates; keep the first so the rest still validates.
                if (seen.Add(scene.Definition.Id))
                {
                    parsed.Add(scene);
                    analysis?.Scenes.Add(new(scene.Definition, scene.HasExplicitPriority));
                }
            }
        }

        var triggers = new List<TriggerDefinition>();
        foreach (var raw in manifest.Triggers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var when = ExpressionParser.ParseCondition(raw.When, raw.WhenLocation, symbols, diagnostics);
            var effects = ExpressionParser.ParseEffects(raw.Do, raw.DoLocation, symbols, diagnostics);
            if (when is not null && effects is not null)
            {
                triggers.Add(new TriggerDefinition(raw.Id, when, effects, raw.Once) { Save = raw.Save });
            }
        }

        var reachability = StoryValidator.Validate(manifest, parsed, diagnostics, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (diagnostics.HasErrors)
        {
            return new CompilationResult(null, diagnostics.Items);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var sceneDefinitions = parsed.Select(p => p.Definition).ToList();
        // Editor analysis never replays; a story it produces builds its identities on first playback instead.
        var replayIdentities = analysis is null ? ChoiceReplayIdentity.Assign(sceneDefinitions) : null;
        var story = new StoryDefinition
        {
            Id = manifest.Id,
            Title = manifest.Title,
            Version = manifest.Version,
            ContentFingerprint = Fingerprint(source, options.CompatibilityId),
            ReplayCompatibilityId = string.IsNullOrWhiteSpace(options.CompatibilityId)
                ? null : $"{ReplayVersion}\n{options.CompatibilityId}",
            UndoEnabled = manifest.UndoEnabled,
            AutosaveEnabled = manifest.AutosaveEnabled,
            CharacterSheet = manifest.CharacterSheet,
            Creation = manifest.Creation,
            Start = new SceneId(manifest.Start),
            Randomness = manifest.Randomness,
            Seed = manifest.Seed,
            World = world,
            Scenes = sceneDefinitions,
            Triggers = triggers,
            Predicates = options.Predicates,
            Reachability = reachability,
            ReplayIdentities = replayIdentities,
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

        Append(ReplayVersion);
        Append(compatibilityId);
        foreach (var (path, text) in source.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            Append(path);
            Append(text);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

}
