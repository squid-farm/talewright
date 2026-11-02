using Talewright.Core.Identity;
using Talewright.Core.World;
using Talewright.Narrative.Templates;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Model;
using Talewright.Talemark.Parsing;
using YamlDotNet.RepresentationModel;

namespace Talewright.Talemark.Analysis;

/// <summary>Half-open UTF-16 source range. Lines and columns are 1-based.</summary>
public sealed record SourceRange(SourceLocation Start, SourceLocation End);

public enum AuthoringSymbolKind { Scene, Character, Stat, Item, Faction, Flag, Variable, Predicate, Modifier, Ruleset, Story, Directive, Image }

/// <summary>A declaration, including unresolved declarations retained during incomplete edits.</summary>
public sealed record AuthoringSymbol(string Name, AuthoringSymbolKind Kind, string Type, SourceRange? Declaration);

public enum ExpressionContextKind { Condition, Number, Effects, Substitution }

/// <summary>Parser-recognized expression input, retained even when semantic parsing fails.</summary>
public sealed record ExpressionContext(ExpressionContextKind Kind, string Text, SourceRange Range);

public sealed record AuthoredSubstitution(SourceRange Range, SubstitutionTemplate Template);
public sealed record AuthoredScene(SceneDefinition Definition, bool HasExplicitPriority);

/// <summary>Immutable, source-aware authoring data, separate from the runtime story model.</summary>
public sealed class StoryAnalysis
{
    private StoryAnalysis(StoryAnalysis original, Diagnostic additional)
    {
        Source = original.Source;
        Symbols = original.Symbols;
        Expressions = original.Expressions;
        CompletionSites = original.CompletionSites;
        Tokens = original.Tokens;
        World = original.World;
        Substitutions = original.Substitutions;
        Scenes = original.Scenes;
        Diagnostics = Array.AsReadOnly(original.Diagnostics.Append(additional).ToArray());
    }
    internal StoryAnalysis(StorySource source, AnalysisCollector collector, IReadOnlyList<Diagnostic> diagnostics)
    {
        Source = source;
        Symbols = Array.AsReadOnly(collector.Symbols.Distinct().ToArray());
        Expressions = Array.AsReadOnly(collector.Expressions.Distinct().ToArray());
        CompletionSites = Array.AsReadOnly(collector.CompletionSites.ToArray());
        Tokens = Array.AsReadOnly(collector.Tokens.ToArray());
        World = collector.AuthoredWorld;
        Substitutions = Array.AsReadOnly(collector.Substitutions.ToArray());
        Scenes = Array.AsReadOnly(collector.Scenes.ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    public StorySource Source { get; }
    public IReadOnlyList<AuthoringSymbol> Symbols { get; }
    public IReadOnlyList<ExpressionContext> Expressions { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public IReadOnlyList<CompletionSite> CompletionSites { get; }
    public IReadOnlyList<AuthoringToken> Tokens { get; }
    public WorldDefinition? World { get; }
    public IReadOnlyList<AuthoredSubstitution> Substitutions { get; }

    /// <summary>Parsed scene definitions; the first declaration wins when IDs are duplicated.</summary>
    public IReadOnlyList<AuthoredScene> Scenes { get; }

    /// <summary>Creates a result with an additional host diagnostic, leaving this snapshot unchanged.</summary>
    public StoryAnalysis WithDiagnostic(Diagnostic diagnostic) => new(this, diagnostic);
}

public static class StoryAnalyzer
{
    /// <summary>Reuses compiler parsing/checks, retaining authoring data even when no runtime story can be built.</summary>
    public static StoryAnalysis Analyze(StorySource source, CompilerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= CompilerOptions.Default;
        var collector = new AnalysisCollector(source, options);
        var result = StoryCompiler.CompileForAnalysis(source, options, collector, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var analysis = new StoryAnalysis(source, collector, result.Diagnostics.Concat(collector.RecoveryImageDiagnostics).ToArray());
        cancellationToken.ThrowIfCancellationRequested();
        return analysis;
    }
}

internal sealed class AnalysisCollector(StorySource source, CompilerOptions options)
{
    private readonly Dictionary<string, string[]> _lines = new(StringComparer.Ordinal);
    private readonly Dictionary<SourceLocation, YamlScalarSource> _scalars = [];
    private readonly Dictionary<string, int[]> _manifestLineStarts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _variableTypes = new(StringComparer.Ordinal);
    private readonly AuthoringSymbol[] _imageCandidates = StoryImageResolver.CompletionPaths(source)
        .Select(name => new AuthoringSymbol(name, AuthoringSymbolKind.Image, "image path", null)).ToArray();
    internal List<AuthoringSymbol> Symbols { get; } = [];
    internal List<ExpressionContext> Expressions { get; } = [];
    internal List<CompletionSite> CompletionSites { get; } = [];
    internal List<AuthoringToken> Tokens { get; } = [];
    internal WorldDefinition? AuthoredWorld { get; private set; }
    internal List<AuthoredSubstitution> Substitutions { get; } = [];
    internal List<AuthoredScene> Scenes { get; } = [];
    internal List<Diagnostic> RecoveryImageDiagnostics { get; } = [];

    internal void ImagePath(string path, SourceRange range, IReadOnlyList<SourceLocation> positions)
    {
        CompletionSites.Add(new(path, range, [AuthoringSymbolKind.Image], [], positions,
            Candidates: _imageCandidates, IsImagePath: true));
    }

    internal void Reference(string text, SourceLocation location, AuthoringSymbolKind kind, params string[] keywords)
        => ReferenceCore(text, location, kind, keywords);

    private void ReferenceCore(string text, SourceLocation location, AuthoringSymbolKind kind,
        IReadOnlyList<string> keywords, IReadOnlyList<AuthoringSymbol>? candidates = null)
    {
        var range = Range(location, text);
        CompletionSites.Add(new(text, range, [kind], keywords,
            Enumerable.Range(0, text.Length + 1).Select(location.Offset).ToArray(), Candidates: candidates));
        if (text.Length == 0 && source.Files.TryGetValue(location.File, out var content))
        {
            if (!_lines.TryGetValue(location.File, out var lines))
                _lines.Add(location.File, lines = content.ReplaceLineEndings("\n").Split('\n'));
            var line = lines[location.Line - 1];
            for (var column = location.Column; column <= line.Length && char.IsWhiteSpace(line[column - 1]); column++)
            {
                var point = location with { Column = column + 1 };
                CompletionSites.Add(new("", new(point, point), [kind], keywords, [point], Candidates: candidates));
            }
        }
    }

    internal static SourceRange Range(SourceLocation start, string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        return new(start, lines.Length == 1 ? start.Offset(text.Length) :
            new SourceLocation(start.File, start.Line + lines.Length - 1, lines[^1].Length + 1));
    }

    // Prose callers pass their paragraph mapping as locate, so positions come straight from the line map.
    internal void Expression(ExpressionContextKind kind, string text, SourceLocation location,
        Func<int, SourceLocation>? locate = null)
    {
        YamlScalarSource? scalar = null;
        if (locate is null) _scalars.TryGetValue(location, out scalar);
        var range = locate is not null ? new SourceRange(locate(0), locate(text.Length)) :
            scalar is not null ? scalar.Range(0, text.Length) : Range(location, text);
        Expressions.Add(new(kind, text, range));
        SourceLocation[]? boundaries = null;
        SourceRange Map(int offset, int length)
        {
            if (offset > text.Length)
            {
                var point = range.End.Offset(offset - text.Length);
                return new(point, point.Offset(length));
            }
            if (scalar is not null) return scalar.Range(offset, length);
            if (locate is not null) return new(locate(offset), locate(offset + length));
            if (boundaries is null)
            {
                boundaries = new SourceLocation[text.Length + 1];
                boundaries[0] = location;
                for (var index = 0; index < text.Length; index++)
                {
                    var previous = boundaries[index];
                    boundaries[index + 1] = text[index] switch
                    {
                        '\r' => previous with { Line = previous.Line + 1, Column = 1 },
                        '\n' when index > 0 && text[index - 1] == '\r' => previous,
                        '\n' => previous with { Line = previous.Line + 1, Column = 1 },
                        _ => previous.Offset(1)
                    };
                }
            }
            return new(boundaries[offset], boundaries[offset + length]);
        }
        var completionText = text;
        if (!StorySource.IsConfigurationFile(location.File) && source.Files.TryGetValue(location.File, out var content))
        {
            if (!_lines.TryGetValue(location.File, out var sourceLines))
                _lines.Add(location.File, sourceLines = content.ReplaceLineEndings("\n").Split('\n'));
            var line = sourceLines[range.End.Line - 1];
            var end = range.End.Column - 1;
            while (end < line.Length && char.IsWhiteSpace(line[end])) end++;
            completionText += line[(range.End.Column - 1)..end];
        }
        CompletionSites.AddRange(ExpressionCompletion.Sites(completionText, kind, Map, Symbols));
        foreach (var token in Lexer.Tokenize(text))
            if (token.Kind is not (TokenKind.End or TokenKind.String or TokenKind.Error or TokenKind.NamedPredicate))
                Tokens.Add(new(token.Text, Map(token.Offset, token.Text.Length)));
        if (kind == ExpressionContextKind.Effects)
        {
            ExpressionParser.CollectDeclarations(text, new HashSet<FlagId>(), new Dictionary<VarId, int>(),
                (name, flag, offset) => Symbols.Add(new(name,
                    flag ? AuthoringSymbolKind.Flag : AuthoringSymbolKind.Variable,
                    flag ? "boolean" : _variableTypes.GetValueOrDefault(name, "unresolved"),
                    scalar?.Range(offset, name.Length) ?? Range(location.Offset(offset), name))));
        }
    }

    internal void Scalar(YamlScalarNode node, string file = StorySource.ManifestFileName)
    {
        var text = source.Files[file];
        if (!_manifestLineStarts.TryGetValue(file, out var lineStarts))
        {
            var starts = new List<int> { 0 };
            for (var index = 0; index < text.Length; index++)
            {
                if (text[index] == '\r')
                {
                    if (index + 1 < text.Length && text[index + 1] == '\n') index++;
                    starts.Add(index + 1);
                }
                else if (text[index] == '\n') starts.Add(index + 1);
            }
            _manifestLineStarts[file] = lineStarts = starts.ToArray();
        }
        _scalars.TryAdd(new SourceLocation(file, (int)node.Start.Line, (int)node.Start.Column),
            new YamlScalarSource(file, text, node, lineStarts));
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

    internal void DisabledStats(YamlNode node, Talewright.Core.Rulesets.Ruleset ruleset) =>
        ReferenceNode(node, AuthoringSymbolKind.Stat, ruleset.Stats.Select(stat =>
            new AuthoringSymbol(stat.Id.Value, AuthoringSymbolKind.Stat, "integer", null)).ToArray());

    internal void GoverningAttribute(YamlNode node, Talewright.Core.Rulesets.Ruleset ruleset) =>
        ReferenceNode(node, AuthoringSymbolKind.Stat, ruleset.Stats
            .Where(stat => stat.Category == Talewright.Core.Rulesets.StatCategory.Attribute)
            .Select(stat => new AuthoringSymbol(stat.Id.Value, AuthoringSymbolKind.Stat, "attribute",
                Symbols.FirstOrDefault(symbol => symbol.Kind == AuthoringSymbolKind.Stat && symbol.Name == stat.Id.Value)?.Declaration))
            .ToArray());

    private void ReferenceNode(YamlNode node, AuthoringSymbolKind kind, IReadOnlyList<AuthoringSymbol>? candidates = null,
        string file = StorySource.ManifestFileName)
    {
        if (node is YamlSequenceNode sequence)
        {
            if (sequence.Children.Count == 0)
            {
                if (!_lines.TryGetValue(file, out var lines))
                    _lines.Add(file, lines = source.Files[file].ReplaceLineEndings("\n").Split('\n'));
                var opened = false;
                // Empty sequence end marks can equal their start; inspect only its authored whitespace and brackets.
                for (var line = (int)sequence.Start.Line; line <= lines.Length; line++)
                {
                    var first = line == sequence.Start.Line ? (int)sequence.Start.Column : 1;
                    var last = lines[line - 1].Length + 1;
                    for (var column = first; column <= last; column++)
                    {
                        var character = column <= lines[line - 1].Length ? lines[line - 1][column - 1] : '\n';
                        if (opened && (char.IsWhiteSpace(character) || character == ']'))
                        {
                            var point = new SourceLocation(file, line, column);
                            CompletionSites.Add(new("", new(point, point), [kind], [], [point], Candidates: candidates));
                        }
                        if (character == ']') return;
                        if (!opened && character == '[') opened = true;
                        else if (!char.IsWhiteSpace(character)) return;
                    }
                }
            }
            foreach (var child in sequence.Children.OfType<YamlScalarNode>()) ReferenceNode(child, kind, candidates, file);
            return;
        }
        if (node is not YamlScalarNode scalar) return;
        Scalar(scalar, file);
        var location = new SourceLocation(file, (int)scalar.Start.Line, (int)scalar.Start.Column);
        var mapping = _scalars[location];
        var text = scalar.Value ?? "";
        if (text.Length == 0)
        {
            ReferenceCore("", mapping.Range(0, 0).Start, kind, [], candidates);
            return;
        }
        CompletionSites.Add(new(text, mapping.Range(0, text.Length), [kind], [],
            Enumerable.Range(0, text.Length + 1).Select(index => mapping.Range(index, 0).Start).ToArray(),
            Candidates: candidates));
    }

    /// <summary>Collects authoring data from one configuration file; earlier files keep duplicate declarations.</summary>
    internal void Manifest(YamlMappingNode root, string file = StorySource.ManifestFileName)
    {
        void Properties(YamlNode node, string[] path, HashSet<YamlNode> ancestors)
        {
            if (!ancestors.Add(node)) return;
            if (node is YamlMappingNode mapping)
            {
                foreach (var (key, value) in mapping.Children)
                {
                    if (key is not YamlScalarNode { Value: { } name } scalar) continue;
                    var next = path.Append(name).ToArray();
                    Scalar(scalar, file);
                    var location = new SourceLocation(file, (int)key.Start.Line, (int)key.Start.Column);
                    Tokens.Add(new(name, _scalars[location].Range(0, name.Length), next));
                    Properties(value, next, ancestors);
                }
            }
            else if (node is YamlSequenceNode sequence)
                foreach (var child in sequence.Children) Properties(child, path.Append("[]").ToArray(), ancestors);
            ancestors.Remove(node);
        }
        Properties(root, [], []);
        void Walk(YamlNode node, string[] path)
        {
            if (node is YamlMappingNode map)
            {
                foreach (var (key, value) in map.Children)
                {
                    if (key is not YamlScalarNode { Value: { } name }) continue;
                    var next = path.Append(name).ToArray();
                    if (next is ["game", "start"]) ReferenceNode(value, AuthoringSymbolKind.Scene, file: file);
                    else if (next is ["game", "ruleset", "base"]) ReferenceNode(value, AuthoringSymbolKind.Ruleset, file: file);
                    else if (path is ["characters", _, "inventory"]) ReferenceNode(key, AuthoringSymbolKind.Item, file: file);
                    else if (next is ["characters", _, "inventory"] && value is YamlSequenceNode)
                        ReferenceNode(value, AuthoringSymbolKind.Item, file: file);
                    else if (path is ["characters", _, "stats"] or ["items", _, "modifiers"] or ["game", "creation", "stats"]
                        or ["game", "creation", "presets", _, "stats"]) ReferenceNode(key, AuthoringSymbolKind.Stat, file: file);
                    if (next is ["game"] or ["characters"] or ["characters", _] or ["characters", _, "inventory"]
                        or ["characters", _, "stats"] or ["items"] or ["items", _] or ["items", _, "modifiers"]
                        or ["game", "ruleset"] or ["game", "creation"] or ["game", "creation", "stats"] or ["game", "creation", "presets"]
                        or ["game", "creation", "presets", _] or ["game", "creation", "presets", _, "stats"])
                        Walk(value, next);
                }
            }
        }
        Walk(root, []);
        SourceRange At(YamlNode node) => new(
            new SourceLocation(file, (int)node.Start.Line, (int)node.Start.Column),
            new SourceLocation(file, (int)node.End.Line, (int)node.End.Column));
        static YamlNode? Get(YamlMappingNode map, string name) =>
            map.Children.TryGetValue(new YamlScalarNode(name), out var value) ? value : null;
        void Add(YamlNode key, AuthoringSymbolKind kind, string type)
        {
            if (key is YamlScalarNode { Value: { Length: > 0 } name } scalar &&
                !Symbols.Any(symbol => symbol.Kind == kind && symbol.Name == name && symbol.Declaration is not null))
            {
                Scalar(scalar, file);
                var location = At(key).Start;
                Symbols.Add(new(name, kind, type, _scalars[location].Range(0, name.Length)));
            }
        }
        if (file == StorySource.ManifestFileName && Get(root, "game") is YamlMappingNode game &&
            Get(game, "id") is YamlScalarNode { Value: { Length: > 0 } storyId } idNode)
        {
            Scalar(idNode);
            var location = new SourceLocation(file, (int)idNode.Start.Line, (int)idNode.Start.Column);
            Symbols.Add(new(storyId, AuthoringSymbolKind.Story, "story",
                _scalars[location].Range(0, storyId.Length)));
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
        if (file == StorySource.ManifestFileName && Get(root, "game") is YamlMappingNode gameConfig &&
            Get(gameConfig, "ruleset") is YamlMappingNode ruleset &&
            Get(ruleset, "stats") is YamlSequenceNode stats)
            foreach (var stat in stats.Children.OfType<YamlMappingNode>())
                if (Get(stat, "id") is { } id) Add(id, AuthoringSymbolKind.Stat, "integer");
    }

    internal void World(WorldDefinition world, IReadOnlySet<VarId>? unresolved = null)
    {
        AuthoredWorld = world;
        foreach (var id in world.Variables.Keys) _variableTypes[id.Value] = "integer";
        foreach (var id in world.TextVariables.Keys) _variableTypes[id.Value] = "text";
        foreach (var id in unresolved ?? new HashSet<VarId>()) _variableTypes[id.Value] = "unresolved";
        for (var index = 0; index < Symbols.Count; index++)
        {
            var symbol = Symbols[index];
            if (symbol.Kind == AuthoringSymbolKind.Variable && _variableTypes.TryGetValue(symbol.Name, out var type))
                Symbols[index] = symbol with { Type = type };
        }
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

    internal void DiscoverWithoutManifest(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var world = new WorldDefinition(new Talewright.Core.Rulesets.RulesetBuilder("analysis-recovery").Build(),
            [], [], [], [], new Dictionary<VarId, int>());
        var symbols = new StorySymbols
        {
            World = world, Flags = new HashSet<FlagId>(), Variables = new HashSet<VarId>(),
            Scenes = new HashSet<SceneId>(), Predicates = options.Predicates, Modifiers = options.Modifiers,
            Analysis = this,
            ImageSource = source,
        };
        foreach (var (file, text) in source.TaleFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TaleParser.Discover(file, text, symbols, (_, _) => { }, Scene, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostics = new DiagnosticBag();
            TaleParser.Parse(file, text, symbols, diagnostics, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            RecoveryImageDiagnostics.AddRange(diagnostics.Items.Where(diagnostic =>
                diagnostic.Code is "TW2010" or "TW2011" or "TW2012" or "TW2013" or "TW2014"));
        }
    }
}
