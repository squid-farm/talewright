using System.Text.RegularExpressions;
using Talewright.Core.Identity;
using Talewright.Core.World;
using Talewright.Rules.Expressions;
using Talewright.Rules.Triggers;
using Talewright.Tale.Diagnostics;
using Talewright.Tale.Model;
using Talewright.Tale.Parsing;

namespace Talewright.Tale.Compilation;

/// <summary>
/// Compiles a story (story.yaml plus <c>.tale</c> files) into a validated <see cref="StoryDefinition"/>.
/// </summary>
/// <remarks>
/// Pipeline:
/// <list type="number">
/// <item>Read story.yaml into a <see cref="WorldDefinition"/> (ruleset, characters, items, factions…).</item>
/// <item>Pre-scan every <c>.tale</c> file for scene ids and for flags/variables that some effect assigns,
/// so forward references and auto-declared flags/variables resolve.</item>
/// <item>Parse each <c>.tale</c> file (expressions are type-checked and resolved as they are parsed).</item>
/// <item>Parse triggers, then run the whole-story <see cref="StoryValidator"/>.</item>
/// </list>
/// Any error means no story is produced; all diagnostics are returned either way.
/// </remarks>
public static partial class StoryCompiler
{
    [GeneratedRegex(@"^[ \t]*===[ \t]*(\S+)", RegexOptions.Multiline)]
    private static partial Regex SceneHeader();

    [GeneratedRegex(@"\b(?:set|clear)\s+flag:([A-Za-z0-9_]+(?:[.\-][A-Za-z0-9_]+)*)")]
    private static partial Regex FlagAssignment();

    [GeneratedRegex(@"\bvar:([A-Za-z0-9_]+(?:[.\-][A-Za-z0-9_]+)*)\s*(?:=(?!=)|\+=|-=)")]
    private static partial Regex VariableAssignment();

    /// <summary>Compiles <paramref name="source"/>.</summary>
    public static CompilationResult Compile(StorySource source, CompilerOptions? options = null)
    {
        options ??= CompilerOptions.Default;
        var diagnostics = new DiagnosticBag();

        if (!source.Files.TryGetValue(StorySource.ManifestFileName, out var yaml))
        {
            diagnostics.Error("TW4002", "The story has no story.yaml at its root.", new SourceLocation(StorySource.ManifestFileName, 0, 0));
            return new CompilationResult(null, diagnostics.Items);
        }

        var manifest = StoryYamlReader.Read(StorySource.ManifestFileName, yaml, diagnostics, options);
        if (manifest is null)
        {
            return new CompilationResult(null, diagnostics.Items);
        }

        var taleFiles = source.TaleFiles.ToList();
        if (taleFiles.Count == 0)
        {
            diagnostics.Error("TW3001", "The story has no .tale files.", new SourceLocation(StorySource.ManifestFileName, 0, 0));
        }

        var scenes = PreScanScenes(taleFiles, diagnostics);
        var world = WithAutoDeclarations(manifest.World, taleFiles.Select(f => f.Value).Concat(manifest.EffectText));
        var symbols = new StorySymbols
        {
            World = world,
            Flags = world.Flags,
            Variables = world.Variables.Keys.ToHashSet(),
            Scenes = scenes,
            Predicates = options.Predicates,
            Modifiers = options.Modifiers,
        };

        var parsed = new List<ParsedScene>();
        var seen = new HashSet<SceneId>();
        foreach (var (path, text) in taleFiles)
        {
            foreach (var scene in TaleParser.Parse(path, text, symbols, diagnostics))
            {
                // Duplicates were reported by the pre-scan; keep the first so the rest still validates.
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
            Start = new SceneId(manifest.Start),
            Randomness = manifest.Randomness,
            Seed = manifest.Seed,
            World = world,
            Scenes = parsed.Select(p => p.Definition).ToList(),
            Triggers = triggers,
            Predicates = options.Predicates,
        };
        return new CompilationResult(story, diagnostics.Items);
    }

    private static HashSet<SceneId> PreScanScenes(List<KeyValuePair<string, string>> files, DiagnosticBag diagnostics)
    {
        var scenes = new Dictionary<SceneId, SourceLocation>();
        foreach (var (path, text) in files)
        {
            foreach (Match match in SceneHeader().Matches(text))
            {
                var id = new SceneId(match.Groups[1].Value);
                var line = text.AsSpan(0, match.Index).Count('\n') + 1;
                var location = new SourceLocation(path, line, 1);
                if (!scenes.TryAdd(id, location))
                {
                    diagnostics.Error("TW3010", $"Scene '{id}' is declared more than once (first at {scenes[id]}).", location);
                }
            }
        }

        return scenes.Keys.ToHashSet();
    }

    /// <summary>
    /// Flags and variables don't have to be declared in story.yaml: assigning one anywhere declares it (flags start
    /// false, variables 0). Reading one that is never assigned or declared is still an error.
    /// </summary>
    private static WorldDefinition WithAutoDeclarations(WorldDefinition world, IEnumerable<string> texts)
    {
        var flags = world.Flags.ToHashSet();
        var vars = world.Variables.ToDictionary();
        foreach (var text in texts)
        {
            foreach (Match match in FlagAssignment().Matches(text))
            {
                flags.Add(new FlagId(match.Groups[1].Value));
            }

            foreach (Match match in VariableAssignment().Matches(text))
            {
                vars.TryAdd(new VarId(match.Groups[1].Value), 0);
            }
        }

        return new WorldDefinition(world.Ruleset, world.Characters.Values, world.Items.Values, world.Factions.Values, flags, vars);
    }
}
