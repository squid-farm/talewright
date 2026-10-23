using Talewright.Core.Identity;
using Talewright.Core.World;
using Talewright.Tale.Compilation;
using Talewright.Tale.Diagnostics;
using Talewright.Tale.Parsing;
using YamlDotNet.RepresentationModel;

namespace Talewright.Tale.Analysis;

/// <summary>Half-open UTF-16 source range. Lines and columns are 1-based.</summary>
public sealed record SourceRange(SourceLocation Start, SourceLocation End);

public enum AuthoringSymbolKind { Scene, Character, Stat, Item, Faction, Flag, Variable, Predicate, Modifier, Ruleset }

/// <summary>A declaration, including unresolved declarations retained during incomplete edits.</summary>
public sealed record AuthoringSymbol(string Name, AuthoringSymbolKind Kind, string Type, SourceRange? Declaration);

public enum ExpressionContextKind { Condition, Number, Effects, Substitution }

/// <summary>Parser-recognized expression input, retained even when semantic parsing fails.</summary>
public sealed record ExpressionContext(ExpressionContextKind Kind, string Text, SourceRange Range);

/// <summary>Immutable, source-aware authoring data, separate from the runtime story model.</summary>
public sealed class StoryAnalysis
{
    private StoryAnalysis(StoryAnalysis original, Diagnostic additional)
    {
        Source = original.Source;
        Symbols = original.Symbols;
        Expressions = original.Expressions;
        Diagnostics = Array.AsReadOnly(original.Diagnostics.Append(additional).ToArray());
    }
    internal StoryAnalysis(StorySource source, AnalysisCollector collector, IReadOnlyList<Diagnostic> diagnostics)
    {
        Source = source;
        Symbols = Array.AsReadOnly(collector.Symbols.Distinct().ToArray());
        Expressions = Array.AsReadOnly(collector.Expressions.Distinct().ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    public StorySource Source { get; }
    public IReadOnlyList<AuthoringSymbol> Symbols { get; }
    public IReadOnlyList<ExpressionContext> Expressions { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>Creates a result with an additional host diagnostic, leaving this snapshot unchanged.</summary>
    public StoryAnalysis WithDiagnostic(Diagnostic diagnostic) => new(this, diagnostic);
}

public static class StoryAnalyzer
{
    /// <summary>Reuses compiler parsing/checks, retaining authoring data even when no runtime story can be built.</summary>
    public static StoryAnalysis Analyze(StorySource source, CompilerOptions? options = null)
    {
        options ??= CompilerOptions.Default;
        var collector = new AnalysisCollector(source, options);
        var result = StoryCompiler.CompileForAnalysis(source, options, collector);
        return new StoryAnalysis(source, collector, result.Diagnostics);
    }
}

internal sealed class AnalysisCollector(StorySource source, CompilerOptions options)
{
    private readonly Dictionary<(string File, int Line), List<(SourceText Source, int Start, int End)>> _inlineRanges = [];
    private readonly Dictionary<string, string[]> _lines = new(StringComparer.Ordinal);
    internal List<AuthoringSymbol> Symbols { get; } = [];
    internal List<ExpressionContext> Expressions { get; } = [];

    internal void InlineRange(SourceText sourceText, int start, int end)
    {
        var location = sourceText.Locate(start);
        var key = (location.File, location.Line);
        if (!_inlineRanges.TryGetValue(key, out var ranges)) _inlineRanges.Add(key, ranges = []);
        ranges.Add((sourceText, start, end));
    }

    internal static SourceRange Range(SourceLocation start, string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        return new(start, lines.Length == 1 ? start.Offset(text.Length) :
            new SourceLocation(start.File, start.Line + lines.Length - 1, lines[^1].Length + 1));
    }

    internal void Expression(ExpressionContextKind kind, string text, SourceLocation location)
    {
        var range = Range(location, text);
        if (_inlineRanges.TryGetValue((location.File, location.Line), out var ranges))
        {
            var inline = ranges.LastOrDefault(candidate =>
                location.Column >= candidate.Source.Locate(candidate.Start).Column &&
                location.Column - candidate.Source.Locate(candidate.Start).Column + text.Length <= candidate.End - candidate.Start);
            if (inline.Source is not null)
            {
                var offset = inline.Start + location.Column - inline.Source.Locate(inline.Start).Column;
                range = new(inline.Source.Locate(offset), inline.Source.Locate(offset + text.Length));
            }
        }
        Expressions.Add(new(kind, text, range));
        if (kind == ExpressionContextKind.Effects)
        {
            ExpressionParser.CollectDeclarations(text, new HashSet<FlagId>(), new Dictionary<VarId, int>(),
                (name, flag, offset) => Symbols.Add(new(name,
                    flag ? AuthoringSymbolKind.Flag : AuthoringSymbolKind.Variable,
                    flag ? "boolean" : "integer", Range(location.Offset(offset), name))));
        }
    }

    internal void Scene(SceneId id, SourceLocation location)
    {
        if (!_lines.TryGetValue(location.File, out var lines))
            _lines.Add(location.File, lines = source.Files[location.File].ReplaceLineEndings("\n").Split('\n'));
        var line = lines[location.Line - 1];
        var start = line.IndexOf("===", StringComparison.Ordinal) + 3;
        while (start < line.Length && char.IsWhiteSpace(line[start])) start++;
        Symbols.Add(new(id.Value, AuthoringSymbolKind.Scene, "scene",
            Range(location with { Column = start + 1 }, id.Value)));
    }

    internal void Manifest(YamlMappingNode root)
    {
        SourceRange At(YamlNode node) => new(
            new SourceLocation(StorySource.ManifestFileName, (int)node.Start.Line, (int)node.Start.Column),
            new SourceLocation(StorySource.ManifestFileName, (int)node.End.Line, (int)node.End.Column));
        static YamlNode? Get(YamlMappingNode map, string name) =>
            map.Children.TryGetValue(new YamlScalarNode(name), out var value) ? value : null;
        void Add(YamlNode key, AuthoringSymbolKind kind, string type)
        {
            if (key is YamlScalarNode { Value: { Length: > 0 } name })
                Symbols.Add(new(name, kind, type, At(key)));
        }
        foreach (var (name, kind, type) in new[]
        {
            ("characters", AuthoringSymbolKind.Character, "character"),
            ("items", AuthoringSymbolKind.Item, "item"),
            ("factions", AuthoringSymbolKind.Faction, "faction"),
            ("vars", AuthoringSymbolKind.Variable, "integer"),
        })
        {
            if (Get(root, name) is YamlMappingNode map)
                foreach (var key in map.Children.Keys) Add(key, kind, type);
        }
        if (Get(root, "flags") is YamlSequenceNode flags)
            foreach (var flag in flags.Children) Add(flag, AuthoringSymbolKind.Flag, "boolean");
        if (Get(root, "ruleset") is YamlMappingNode ruleset && Get(ruleset, "stats") is YamlSequenceNode stats)
            foreach (var stat in stats.Children.OfType<YamlMappingNode>())
                if (Get(stat, "id") is { } id) Add(id, AuthoringSymbolKind.Stat, "integer");
    }

    internal void World(WorldDefinition world)
    {
        foreach (var stat in world.Ruleset.Stats)
            if (!Symbols.Any(symbol => symbol.Kind == AuthoringSymbolKind.Stat && symbol.Name == stat.Id.Value))
                Symbols.Add(new(stat.Id.Value, AuthoringSymbolKind.Stat, "integer", null));
    }

    internal void Registrations()
    {
        foreach (var name in options.Predicates.Names)
            Symbols.Add(new(name, AuthoringSymbolKind.Predicate, "boolean", null));
        foreach (var name in options.Modifiers.Names)
            Symbols.Add(new(name, AuthoringSymbolKind.Modifier, "text", null));
        foreach (var name in options.Rulesets.Keys)
            Symbols.Add(new(name, AuthoringSymbolKind.Ruleset, "ruleset", null));
    }

    internal void DiscoverWithoutManifest()
    {
        var world = new WorldDefinition(options.Rulesets["generic"](), [], [], [], [], new Dictionary<VarId, int>());
        var symbols = new StorySymbols
        {
            World = world, Flags = new HashSet<FlagId>(), Variables = new HashSet<VarId>(),
            Scenes = new HashSet<SceneId>(), Predicates = options.Predicates, Modifiers = options.Modifiers,
            Analysis = this,
        };
        foreach (var (file, text) in source.TaleFiles)
        {
            TaleParser.Discover(file, text, symbols, _ => { }, Scene);
            TaleParser.Parse(file, text, symbols, new DiagnosticBag());
        }
    }
}
