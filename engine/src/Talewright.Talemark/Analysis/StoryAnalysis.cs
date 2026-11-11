using System.Collections.Frozen;
using Talewright.Core.Identity;
using Talewright.Core.Rulesets;
using Talewright.Core.World;
using Talewright.Narrative.Templates;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Model;
using Talewright.Talemark.Parsing;
using Talewright.Talemark.Vocabulary;
using YamlDotNet.RepresentationModel;

namespace Talewright.Talemark.Analysis;

/// <summary>Half-open UTF-16 source range. Lines and columns are 1-based.</summary>
public sealed record SourceRange(SourceLocation Start, SourceLocation End);

/// <summary>
/// Kinds of authoring names. <see cref="Tag"/> and <see cref="Trigger"/> identify authored names that appear only in
/// <see cref="StoryAnalysis.Names"/>, not in <see cref="StoryAnalysis.Symbols"/>.
/// </summary>
public enum AuthoringSymbolKind { Scene, Character, Stat, Item, Faction, Flag, Variable, Predicate, Modifier, Ruleset, Story, Directive, Image, Tag, Trigger }

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
    private sealed class OverlaySymbols(
        IReadOnlyList<AuthoringSymbol> snapshot,
        string replacedFile,
        IReadOnlyList<AuthoringSymbol> currentFile) : IReadOnlyList<AuthoringSymbol>
    {
        private int _count = -1;

        public int Count => _count >= 0 ? _count :
            (_count = snapshot.Count(symbol => symbol.Declaration?.Start.File != replacedFile) + currentFile.Count);

        public AuthoringSymbol this[int index]
        {
            get
            {
                if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
                foreach (var symbol in this)
                    if (index-- == 0) return symbol;
                throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        public IEnumerator<AuthoringSymbol> GetEnumerator()
        {
            foreach (var symbol in snapshot)
                if (symbol.Declaration?.Start.File != replacedFile)
                    yield return symbol;
            foreach (var symbol in currentFile)
                yield return symbol;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private StoryAnalysis(StoryAnalysis original, Diagnostic additional)
    {
        Source = original.Source;
        Symbols = original.Symbols;
        Expressions = original.Expressions;
        _sites = original._sites;
        _names = original._names;
        BaseRulesetStats = original.BaseRulesetStats;
        Tokens = original.Tokens;
        _classifications = original._classifications;
        World = original.World;
        Substitutions = original.Substitutions;
        _scenes = original._scenes;
        SymbolContext = original.SymbolContext;
        _statDiagnosticGuidance = original._statDiagnosticGuidance;
        _currentFile = original._currentFile;
        _currentFileSymbols = original._currentFileSymbols;
        _symbolsByIdentity = original._symbolsByIdentity;
        Diagnostics = Array.AsReadOnly(original.Diagnostics.Append(additional).ToArray());
    }
    internal StoryAnalysis(StorySource source, AnalysisCollector collector, IReadOnlyList<Diagnostic> diagnostics)
        : this(source, collector, diagnostics, null, null, false, null) { }
    internal StoryAnalysis(StorySource source, AnalysisCollector collector, IReadOnlyList<Diagnostic> diagnostics,
        StoryAnalysis? snapshot, string currentFile, IReadOnlyList<string>? additionalTagNames = null)
        : this(source, collector, diagnostics, snapshot, currentFile, true, additionalTagNames) { }

    private StoryAnalysis(StorySource source, AnalysisCollector collector, IReadOnlyList<Diagnostic> diagnostics,
        StoryAnalysis? snapshot, string? currentFile, bool currentFileAnalysis,
        IReadOnlyList<string>? additionalTagNames)
    {
        Source = source;
        _currentFile = currentFileAnalysis ? currentFile : null;
        var currentSymbols = collector.Symbols.Distinct().ToArray();
        _currentFileSymbols = currentFileAnalysis ? Array.AsReadOnly(currentSymbols) : [];
        Symbols = currentFileAnalysis && snapshot is not null
            ? new OverlaySymbols(snapshot.Symbols, currentFile!, _currentFileSymbols)
            : Array.AsReadOnly(currentSymbols);
        _symbolsByIdentity = new(() => Symbols.GroupBy(symbol => (symbol.Kind, symbol.Name))
            .ToDictionary(group => group.Key, group => group.ToArray()));
        Expressions = Array.AsReadOnly(collector.Expressions.Distinct().ToArray());
        var tagCandidates = Array.Empty<AuthoringSymbol>();
        if (collector.CompletionSites.Any(source => source.Site?.Kinds.Contains(AuthoringSymbolKind.Tag) == true))
        {
            var tagNames = collector.Names
                .Where(name => name.Kind == AuthoringSymbolKind.Tag && name.IsDeclaration)
                .Select(name => name.Name)
                .ToHashSet(StringComparer.Ordinal);
            if (currentFileAnalysis && snapshot is not null)
                tagNames.UnionWith(snapshot.Names.DeclaredNames(AuthoringSymbolKind.Tag, currentFile!));
            if (currentFileAnalysis && additionalTagNames is not null)
                tagNames.UnionWith(additionalTagNames);
            tagCandidates = tagNames.OrderBy(name => name, StringComparer.Ordinal)
                .Select(name => new AuthoringSymbol(name, AuthoringSymbolKind.Tag, "tag", null)).ToArray();
        }
        var readOnlyTagCandidates = Array.AsReadOnly(tagCandidates);
        var siteSources = collector.CompletionSites.Select(source =>
            source.Site is { } site && site.Kinds.Contains(AuthoringSymbolKind.Tag)
                ? source with { Site = site with { Candidates = readOnlyTagCandidates } }
                : source).ToArray();
        _sites = new CompletionSiteIndex(siteSources);
        var names = collector.Names.ToArray();
        _names = currentFileAnalysis ? null : new(() => NameIndex.Build(Symbols, names, siteSources));
        Tokens = Array.AsReadOnly(collector.Tokens.ToArray());
        var classificationSources = collector.ClassificationSources.ToArray();
        _classifications = new(() => Array.AsReadOnly(classificationSources
            .SelectMany(input => TalemarkClassifier.Classify(input.File, input.Syntax, CancellationToken.None))
            .ToArray()));
        World = collector.AuthoredWorld;
        Substitutions = Array.AsReadOnly(collector.Substitutions.ToArray());
        var scenes = Array.AsReadOnly(collector.Scenes.ToArray());
        _scenes = currentFileAnalysis && snapshot is not null
            ? new(() => OverlayScenes(snapshot, currentFile!, scenes))
            : new(() => scenes);
        SymbolContext = collector.SymbolContext;
        _statDiagnosticGuidance = collector.SymbolContext?.StatGuidance;
        BaseRulesetStats = collector.BaseRulesetStats.ToFrozenSet(StringComparer.Ordinal);
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    private readonly CompletionSiteIndex _sites;
    private readonly Lazy<NameIndex>? _names;
    private readonly Lazy<IReadOnlyList<TalemarkClassification>> _classifications;
    private readonly Lazy<IReadOnlyList<AuthoredScene>> _scenes;
    private readonly string? _currentFile;
    private readonly IReadOnlyList<AuthoringSymbol> _currentFileSymbols;
    private readonly Lazy<Dictionary<(AuthoringSymbolKind Kind, string Name), AuthoringSymbol[]>> _symbolsByIdentity;
    private readonly StatDiagnosticGuidance? _statDiagnosticGuidance;

    public StorySource Source { get; }
    public IReadOnlyList<AuthoringSymbol> Symbols { get; }
    public IReadOnlyList<ExpressionContext> Expressions { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>
    /// Parser-recognized reference slots whose range contains <paramref name="position"/>, including a range
    /// ending there, in source-analysis order. Expression slots are computed on request.
    /// </summary>
    public IReadOnlyList<CompletionSite> CompletionSitesAt(SourceLocation position) => _sites.At(position, Symbols);

    /// <summary>Every completion site, computed in full; for tests comparing the indexed query with a scan.</summary>
    internal IEnumerable<CompletionSite> AllCompletionSites() => _sites.All(Symbols);

    /// <summary>
    /// Declarations and parser-recognized references of authored names across the whole story, built once on first
    /// access. Current-file analyses only hold one file's references, so they do not provide this index.
    /// </summary>
    public NameIndex Names => _names?.Value ??
        throw new InvalidOperationException("Name references need a whole-story analysis.");

    public IReadOnlyList<AuthoringToken> Tokens { get; }

    /// <summary>Source classifications, computed once on first access from this snapshot's parsed syntax.</summary>
    public IReadOnlyList<TalemarkClassification> Classifications => _classifications.Value;
    public WorldDefinition? World { get; }
    public IReadOnlyList<AuthoredSubstitution> Substitutions { get; }

    /// <summary>Parsed scene definitions; the first declaration wins when IDs are duplicated.</summary>
    public IReadOnlyList<AuthoredScene> Scenes => _scenes.Value;
    internal StorySymbols? SymbolContext { get; }
    internal string? StatProviderName(string stat) =>
        _statDiagnosticGuidance?.ProviderNameFor(new StatId(stat));

    /// <summary>
    /// Stat IDs the story's base ruleset defines before the story disables or overrides any, so a story stat with one
    /// of these IDs overrides a built-in stat. Only whole-story analyses read the ruleset configuration.
    /// </summary>
    internal IReadOnlySet<string> BaseRulesetStats { get; }

    // Scene definitions carry no source file, so a snapshot scene belongs to the edited file only when the
    // snapshot declares it there and nowhere else.
    private static IReadOnlyList<AuthoredScene> OverlayScenes(StoryAnalysis snapshot, string file,
        IReadOnlyList<AuthoredScene> currentScenes)
    {
        var replaced = snapshot.Symbols
            .Where(symbol => symbol.Kind == AuthoringSymbolKind.Scene && symbol.Declaration?.Start.File == file &&
                !snapshot.HasSymbolOutside(file, AuthoringSymbolKind.Scene, symbol.Name))
            .Select(symbol => symbol.Name)
            .ToHashSet(StringComparer.Ordinal);
        return Array.AsReadOnly(snapshot.Scenes
            .Where(scene => !replaced.Contains(scene.Definition.Id.Value))
            .Concat(currentScenes)
            .ToArray());
    }

    internal bool HasSymbolOutside(string file, AuthoringSymbolKind kind, string name) =>
        _symbolsByIdentity.Value.TryGetValue((kind, name), out var symbols) &&
        symbols.Any(symbol => symbol.Declaration?.Start.File != file);

    internal AuthoringSymbol? DeclaredAt(SourceLocation position) =>
        (_currentFile is not null && position.File == _currentFile ? _currentFileSymbols : Symbols)
        .FirstOrDefault(symbol => symbol.Declaration is { } range &&
            position.File == range.Start.File && Compare(range.Start, position) <= 0 && Compare(position, range.End) < 0);

    private static int Compare(SourceLocation left, SourceLocation right) =>
        left.Line != right.Line ? left.Line.CompareTo(right.Line) : left.Column.CompareTo(right.Column);

    /// <summary>Creates a result with an additional host diagnostic, leaving this snapshot unchanged.</summary>
    public StoryAnalysis WithDiagnostic(Diagnostic diagnostic) => new(this, diagnostic);
}

public static class StoryAnalyzer
{
    /// <summary>Reuses compiler parsing/checks, retaining authoring data even when no runtime story can be built.</summary>
    public static StoryAnalysis Analyze(StorySource source, CompilerOptions? options = null,
        CancellationToken cancellationToken = default)
        => Analyze(source, options, cancellationToken, null);

    internal static StoryAnalysis Analyze(StorySource source, CompilerOptions? options,
        CancellationToken cancellationToken, Func<string, string, CancellationToken, TaleFileSyntax>? syntax)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= CompilerOptions.Default;
        var collector = new AnalysisCollector(source, options);
        var result = StoryCompiler.CompileForAnalysis(source, options, collector, cancellationToken, syntax);
        cancellationToken.ThrowIfCancellationRequested();
        var analysis = new StoryAnalysis(source, collector, result.Diagnostics.Concat(collector.RecoveryImageDiagnostics).ToArray());
        cancellationToken.ThrowIfCancellationRequested();
        return analysis;
    }

    internal static StoryAnalysis AnalyzeCurrentFile(StorySource source, string file, TaleFileSyntax? syntax,
        StoryAnalysis? snapshot, CompilerOptions options, CancellationToken cancellationToken,
        IReadOnlyList<string>? additionalTagNames = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var collector = new AnalysisCollector(source, options);
        var diagnostics = new DiagnosticBag();
        var inheritedDiagnostics = snapshot?.Diagnostics.ToArray() ?? [];
        var previousSymbols = snapshot?.Symbols ?? [];
        var context = snapshot?.SymbolContext;
        var previousWorld = context?.World ?? EmptyWorld();
        if (context is null) collector.Registrations();

        if (StorySource.IsConfigurationFile(file))
        {
            var flags = context?.Flags.ToHashSet() ?? [];
            var numbers = previousWorld.Variables.ToDictionary();
            var textVariables = previousWorld.TextVariables.ToDictionary();
            var unresolved = context?.UnresolvedVariables.ToHashSet() ?? [];
            RemovePreviousFileSymbols(file, previousSymbols, snapshot, flags, numbers, textVariables, unresolved, null);
            var baseWorld = new WorldDefinition(previousWorld.Ruleset, previousWorld.Characters.Values,
                previousWorld.Items.Values, previousWorld.Factions.Values, flags, numbers, textVariables);
            collector.World(baseWorld, unresolved);
            var root = new StoryYamlContext(file, diagnostics).Parse(file, source.Files[file]);
            if (root is not null) collector.Manifest(root, file);
        }
        else
        {
            if (syntax is null)
                throw new ArgumentNullException(nameof(syntax), "Current-file syntax is required for a TaleMark document.");

            var declarations = new List<VariableDeclaration>();
            var discoveredNumbers = new Dictionary<VarId, int>();
            var localFlags = new HashSet<FlagId>();
            var localVariables = new HashSet<VarId>();
            var currentScenes = new HashSet<SceneId>();
            void DiscoverEffects(string text, SourceLocation location) =>
                ExpressionParser.CollectDeclarations(text, localFlags, discoveredNumbers,
                    declaration: (name, flag, _) =>
                    {
                        if (flag) localFlags.Add(new FlagId(name));
                        else localVariables.Add(new VarId(name));
                    },
                    typedDeclaration: declarations.Add, location: location);

            TaleDeclarations.Collect(file, syntax, (id, location) =>
            {
                currentScenes.Add(id);
                collector.Scene(id, location);
            }, DiscoverEffects, cancellationToken);

            var previousFileSymbols = previousSymbols
                .Where(symbol => symbol.Declaration?.Start.File == file).ToArray();
            var unchangedDeclarations = context is not null &&
                !previousFileSymbols.Any(symbol => symbol.Kind is AuthoringSymbolKind.Flag or AuthoringSymbolKind.Variable) &&
                localFlags.Count == 0 && localVariables.Count == 0 &&
                currentScenes.SetEquals(previousFileSymbols
                    .Where(symbol => symbol.Kind == AuthoringSymbolKind.Scene)
                    .Select(symbol => new SceneId(symbol.Name)));

            WorldDefinition world;
            IReadOnlySet<FlagId> flags;
            IReadOnlySet<VarId> variables;
            IReadOnlySet<VarId> unresolved;
            IReadOnlySet<SceneId> scenes;
            if (unchangedDeclarations)
            {
                world = previousWorld;
                flags = context!.Flags;
                variables = context.Variables;
                unresolved = context.UnresolvedVariables;
                scenes = context.Scenes;
                collector.CurrentWorld(world);
            }
            else
            {
                var mutableFlags = context?.Flags.ToHashSet() ?? [];
                var numbers = previousWorld.Variables.ToDictionary();
                var textVariables = previousWorld.TextVariables.ToDictionary();
                var mutableUnresolved = context?.UnresolvedVariables.ToHashSet() ?? [];
                var mutableScenes = context?.Scenes.ToHashSet() ?? [];
                RemovePreviousFileSymbols(file, previousFileSymbols, snapshot, mutableFlags, numbers,
                    textVariables, mutableUnresolved, mutableScenes);
                mutableFlags.UnionWith(localFlags);
                mutableScenes.UnionWith(currentScenes);
                var baseWorld = new WorldDefinition(previousWorld.Ruleset, previousWorld.Characters.Values,
                    previousWorld.Items.Values, previousWorld.Factions.Values, mutableFlags, numbers, textVariables);
                var localUnresolved = VariableDiscovery.Resolve(baseWorld, mutableFlags, mutableScenes,
                    declarations, numbers, textVariables, options, diagnostics);
                mutableUnresolved.UnionWith(localUnresolved);
                world = new WorldDefinition(previousWorld.Ruleset, previousWorld.Characters.Values,
                    previousWorld.Items.Values, previousWorld.Factions.Values, mutableFlags, numbers, textVariables);
                collector.World(world, mutableUnresolved);
                flags = mutableFlags;
                variables = numbers.Keys.Concat(textVariables.Keys).Concat(mutableUnresolved).ToHashSet();
                unresolved = mutableUnresolved;
                scenes = mutableScenes;
            }

            collector.ClassificationSources.Add((file, syntax));
            var currentSymbols = new StorySymbols
            {
                Analysis = collector,
                ImageSource = source,
                World = world,
                Flags = flags,
                Variables = variables,
                UnresolvedVariables = unresolved,
                Scenes = scenes,
                Predicates = context?.Predicates ?? options.Predicates,
                Modifiers = context?.Modifiers ?? options.Modifiers,
                StatGuidance = context?.StatGuidance,
            };
            collector.SymbolContext = currentSymbols;
            var parsedScenes = TaleBinder.Parse(file, syntax, currentSymbols, diagnostics, cancellationToken);
            foreach (var scene in parsedScenes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                collector.Scenes.Add(new(scene.Definition, scene.HasExplicitPriority));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new StoryAnalysis(source, collector, inheritedDiagnostics.Concat(diagnostics.Items).ToArray(),
            snapshot, file, additionalTagNames);
    }

    private static void RemovePreviousFileSymbols(string file, IReadOnlyList<AuthoringSymbol> previousSymbols,
        StoryAnalysis? snapshot, HashSet<FlagId> flags, Dictionary<VarId, int> numbers,
        Dictionary<VarId, string> textVariables, HashSet<VarId> unresolved, HashSet<SceneId>? scenes)
    {
        foreach (var symbol in previousSymbols.Where(symbol => symbol.Declaration?.Start.File == file))
        {
            if (snapshot?.HasSymbolOutside(file, symbol.Kind, symbol.Name) == true)
                continue;
            switch (symbol.Kind)
            {
                case AuthoringSymbolKind.Scene:
                    scenes?.Remove(new SceneId(symbol.Name));
                    break;
                case AuthoringSymbolKind.Flag:
                    flags.Remove(new FlagId(symbol.Name));
                    break;
                case AuthoringSymbolKind.Variable:
                    var variable = new VarId(symbol.Name);
                    numbers.Remove(variable);
                    textVariables.Remove(variable);
                    unresolved.Remove(variable);
                    break;
            }
        }
    }

    private static WorldDefinition EmptyWorld() =>
        new(new RulesetBuilder("current-file").Build(), [], [], [], [], new Dictionary<VarId, int>());
}

internal sealed class AnalysisCollector(StorySource source, CompilerOptions options)
{
    private readonly Dictionary<string, string[]> _lines = new(StringComparer.Ordinal);
    private readonly Dictionary<SourceLocation, YamlScalarSource> _scalars = [];
    private readonly Dictionary<string, int[]> _manifestLineStarts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _variableTypes = new(StringComparer.Ordinal);
    private Dictionary<(SourceLocation Location, string Text), TokenBuffer>? _reusableExpressions;
    private readonly AuthoringSymbol[] _imageCandidates = StoryImageResolver.CompletionPaths(source)
        .Select(name => new AuthoringSymbol(name, AuthoringSymbolKind.Image, "image path", null)).ToArray();
    internal List<AuthoringSymbol> Symbols { get; } = [];
    internal List<ExpressionContext> Expressions { get; } = [];
    internal List<CompletionSiteSource> CompletionSites { get; } = [];
    internal List<NameOccurrence> Names { get; } = [];
    internal HashSet<string> BaseRulesetStats { get; } = new(StringComparer.Ordinal);
    internal List<AuthoringToken> Tokens { get; } = [];
    internal List<(string File, TaleFileSyntax Syntax)> ClassificationSources { get; } = [];
    internal WorldDefinition? AuthoredWorld { get; private set; }
    internal List<AuthoredSubstitution> Substitutions { get; } = [];
    internal List<AuthoredScene> Scenes { get; } = [];
    internal List<Diagnostic> RecoveryImageDiagnostics { get; } = [];
    internal StorySymbols? SymbolContext { get; set; }

    private void AddSite(CompletionSite site) => CompletionSites.Add(CompletionSiteSource.Of(site));

    internal void ImagePath(string path, SourceRange range, IReadOnlyList<SourceLocation> positions)
    {
        AddSite(new(path, range, [AuthoringSymbolKind.Image], [], positions,
            Candidates: _imageCandidates, IsImagePath: true));
    }

    internal void Reference(string text, SourceLocation location, AuthoringSymbolKind kind, params string[] keywords)
        => ReferenceCore(text, location, kind, keywords);

    internal void RequirementSuffix(string text, SourceLocation location, RequirementClauseSyntax? parsed = null)
    {
        var syntax = parsed ?? RequirementClauseSyntax.Parse(text);
        if (!syntax.HasSuffix)
            return;

        var keywords = new[] { SceneVocabulary.Shown };
        var details = AuthoringSyntaxVocabulary.KeywordDetails(keywords);
        if (syntax.SuffixLength == 0)
        {
            AddKeywordPoint(location.Offset(syntax.SuffixStart), keywords, details);
            return;
        }

        var word = text.Substring(syntax.SuffixStart, syntax.SuffixLength);
        var start = location.Offset(syntax.SuffixStart);
        AddSite(new(word, Range(start, word), [], keywords,
            Enumerable.Range(0, word.Length + 1).Select(start.Offset).ToArray(),
            KeywordDetails: details));
    }

    internal void HubSelector(string value, SourceLocation location)
    {
        var words = new List<(int Start, int Length)>();
        for (var index = 0; index < value.Length;)
        {
            while (index < value.Length && char.IsWhiteSpace(value[index]))
                index++;
            if (index == value.Length)
                break;
            var start = index;
            while (index < value.Length && !char.IsWhiteSpace(value[index]))
                index++;
            words.Add((start, index - start));
        }

        var keywords = new[] { ExpressionVocabulary.And, ExpressionVocabulary.Or };
        var details = AuthoringSyntaxVocabulary.KeywordDetails(keywords);
        if (words.Count == 0)
        {
            ReferenceCore("", location, AuthoringSymbolKind.Tag, []);
            return;
        }

        for (var index = 0; index < words.Count; index++)
        {
            var word = words[index];
            if (index % 2 == 1)
            {
                var text = value.Substring(word.Start, word.Length);
                var start = location.Offset(word.Start);
                AddSite(new(text, Range(start, text), [], keywords,
                    Enumerable.Range(0, text.Length + 1).Select(start.Offset).ToArray(),
                    KeywordDetails: details));
                var operatorAfterWord = word.Start + word.Length;
                while (operatorAfterWord < value.Length && char.IsWhiteSpace(value[operatorAfterWord]))
                    operatorAfterWord++;
                if (operatorAfterWord > word.Start + word.Length)
                    ReferenceCore("", location.Offset(operatorAfterWord), AuthoringSymbolKind.Tag, []);
                else if (index == words.Count - 1 &&
                         TrailingWhitespaceEnd(location, value) is { } tagPosition)
                    ReferenceCore("", tagPosition, AuthoringSymbolKind.Tag, []);
                continue;
            }

            var tag = value.Substring(word.Start, word.Length);
            ReferenceCore(tag, location.Offset(word.Start), AuthoringSymbolKind.Tag, []);
            var afterWord = word.Start + word.Length;
            while (afterWord < value.Length && char.IsWhiteSpace(value[afterWord]))
                afterWord++;
            if (afterWord > word.Start + word.Length)
            {
                AddKeywordPoint(location.Offset(afterWord), keywords, details);
                continue;
            }

            if (index == words.Count - 1 &&
                TrailingWhitespaceEnd(location, value) is { } keywordPosition)
                AddKeywordPoint(keywordPosition, keywords, details);
        }
    }

    private SourceLocation? TrailingWhitespaceEnd(SourceLocation location, string value)
    {
        if (!source.Files.TryGetValue(location.File, out var content)) return null;
        if (!_lines.TryGetValue(location.File, out var lines))
            _lines.Add(location.File, lines = content.ReplaceLineEndings("\n").Split('\n'));
        var line = lines[location.Line - 1];
        var end = location.Column - 1 + value.Length;
        var trailing = end;
        while (end < line.Length && char.IsWhiteSpace(line[end]))
            end++;
        return end > trailing ? location with { Column = end + 1 } : null;
    }

    internal void TagSelector(string value, SourceLocation location)
    {
        foreach (var (name, at) in TagSegments(value, location))
            ReferenceCore(name, at, AuthoringSymbolKind.Tag, []);
    }

    /// <summary>Records each comma-separated <c>@tags</c> entry as a tag declaration.</summary>
    internal void Tags(string value, SourceLocation location)
    {
        foreach (var (name, at) in TagSegments(value, location))
            if (name.Length > 0)
                Names.Add(new(AuthoringSymbolKind.Tag, name, new(at, at.Offset(name.Length)), true));
    }

    /// <summary>Records the tag words of a valid <c>@hub</c> selector, which alternate with and/or.</summary>
    internal void HubTags(string value, SourceLocation location)
    {
        var word = 0;
        for (var index = 0; index < value.Length;)
        {
            while (index < value.Length && char.IsWhiteSpace(value[index])) index++;
            if (index == value.Length) break;
            var start = index;
            while (index < value.Length && !char.IsWhiteSpace(value[index])) index++;
            if (word++ % 2 == 0)
            {
                var at = location.Offset(start);
                Names.Add(new(AuthoringSymbolKind.Tag, value[start..index], new(at, at.Offset(index - start)), false));
            }
        }
    }

    private static List<(string Name, SourceLocation Location)> TagSegments(string value, SourceLocation location)
    {
        var segments = new List<(string, SourceLocation)>();
        for (var start = 0; start <= value.Length;)
        {
            var comma = value.IndexOf(ExpressionVocabulary.ArgumentSeparator, start);
            var end = comma < 0 ? value.Length : comma;
            var segment = value[start..end];
            var name = segment.Trim();
            var leadingWhitespace = segment.Length - segment.TrimStart().Length;
            var offset = name.Length == 0 ? start : start + leadingWhitespace;
            segments.Add((name, location.Offset(offset)));
            if (comma < 0) break;
            start = comma + 1;
        }
        return segments;
    }

    /// <summary>Records a configuration scalar as the declaration of a name that has no symbol, such as a trigger ID.</summary>
    internal void NameDeclaration(YamlScalarNode node, AuthoringSymbolKind kind, string file)
    {
        if (node.Value is not { Length: > 0 } name) return;
        Scalar(node, file);
        var location = new SourceLocation(file, (int)node.Start.Line, (int)node.Start.Column);
        Names.Add(new(kind, name, _scalars[location].Range(0, name.Length), true));
    }

    private void AddKeywordPoint(SourceLocation location, IReadOnlyList<string> keywords,
        IReadOnlyDictionary<string, string> details) =>
        AddSite(new("", new(location, location), [], keywords, [location], KeywordDetails: details));

    private void ReferenceCore(string text, SourceLocation location, AuthoringSymbolKind kind,
        IReadOnlyList<string> keywords, IReadOnlyList<AuthoringSymbol>? candidates = null)
    {
        var range = Range(location, text);
        AddSite(new(text, range, [kind], keywords,
            Enumerable.Range(0, text.Length + 1).Select(location.Offset).ToArray(), Candidates: candidates));
        if (text.Length == 0 && source.Files.TryGetValue(location.File, out var content))
        {
            if (!_lines.TryGetValue(location.File, out var lines))
                _lines.Add(location.File, lines = content.ReplaceLineEndings("\n").Split('\n'));
            var line = lines[location.Line - 1];
            for (var column = location.Column; column <= line.Length && char.IsWhiteSpace(line[column - 1]); column++)
            {
                var point = location with { Column = column + 1 };
                AddSite(new("", new(point, point), [kind], keywords, [point], Candidates: candidates));
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
    internal TokenBuffer Expression(ExpressionContextKind kind, string text, SourceLocation location,
        Func<int, SourceLocation>? locate = null, bool cacheForReuse = false)
    {
        YamlScalarSource? scalar = null;
        if (locate is null) _scalars.TryGetValue(location, out scalar);
        var range = locate is not null ? new SourceRange(locate(0), locate(text.Length)) :
            scalar is not null ? scalar.Range(0, text.Length) : Range(location, text);
        Expressions.Add(new(kind, text, range));
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
        var key = (location, completionText);
        if (_reusableExpressions is null || !_reusableExpressions.TryGetValue(key, out var tokens))
            tokens = Lexer.Tokenize(completionText);
        if (cacheForReuse) (_reusableExpressions ??= []).TryAdd(key, tokens);
        var sites = new ExpressionSiteSource(tokens, text.Length, kind, range, location, scalar, locate);
        var map = new ExpressionSourceMap(sites);
        CompletionSites.Add(new(new(map.Map(0, 0).Start, map.Map(completionText.Length, 0).End), null, sites));
        foreach (var token in tokens)
            if (token.Kind is not (TokenKind.End or TokenKind.String or TokenKind.Error or TokenKind.Hash or TokenKind.NamedPredicate))
            {
                var value = token.Value;
                Tokens.Add(new(value, map.Map(token.Offset, token.Length))
                    { Kind = AuthoringSyntaxVocabulary.ExpressionKind(value) });
            }
        if (kind == ExpressionContextKind.Effects)
        {
            ExpressionParser.CollectDeclarations(tokens, text.Length, new HashSet<FlagId>(), new Dictionary<VarId, int>(),
                (name, flag, offset) => Symbols.Add(new(name,
                    flag ? AuthoringSymbolKind.Flag : AuthoringSymbolKind.Variable,
                    flag ? ManifestVocabulary.SymbolBoolean :
                        _variableTypes.GetValueOrDefault(name, ManifestVocabulary.SymbolUnresolved),
                    scalar?.Range(offset, name.Length) ?? Range(location.Offset(offset), name))));
        }
        return tokens;
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
        var start = line.IndexOf(SceneVocabulary.SceneHeader, StringComparison.Ordinal)
            + SceneVocabulary.SceneHeader.Length;
        while (start < line.Length && char.IsWhiteSpace(line[start])) start++;
        Symbols.Add(new(id.Value, AuthoringSymbolKind.Scene, ManifestVocabulary.SymbolScene,
            Range(location with { Column = start + 1 }, id.Value)));
    }

    internal void BaseRuleset(Talewright.Core.Rulesets.Ruleset ruleset)
    {
        foreach (var stat in ruleset.Stats) BaseRulesetStats.Add(stat.Id.Value);
    }

    internal void DisabledStats(YamlNode node, Talewright.Core.Rulesets.Ruleset ruleset) =>
        ReferenceNode(node, AuthoringSymbolKind.Stat, ruleset.Stats.Select(stat =>
            new AuthoringSymbol(stat.Id.Value, AuthoringSymbolKind.Stat, ManifestVocabulary.SymbolInteger, null)).ToArray());

    internal void GoverningAttribute(YamlNode node, Talewright.Core.Rulesets.Ruleset ruleset) =>
        ReferenceNode(node, AuthoringSymbolKind.Stat, ruleset.Stats
            .Where(stat => stat.Category == Talewright.Core.Rulesets.StatCategory.Attribute)
            .Select(stat => new AuthoringSymbol(stat.Id.Value, AuthoringSymbolKind.Stat, ManifestVocabulary.StatCategoryAttribute,
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
                            AddSite(new("", new(point, point), [kind], [], [point], Candidates: candidates));
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
        AddSite(new(text, mapping.Range(0, text.Length), [kind], [],
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
                    Tokens.Add(new(name, _scalars[location].Range(0, name.Length), next)
                        { Kind = AuthoringTokenKind.ManifestProperty });
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
                    if (next is [ManifestVocabulary.Game, ManifestVocabulary.Start]) ReferenceNode(value, AuthoringSymbolKind.Scene, file: file);
                    else if (next is [ManifestVocabulary.Game, ManifestVocabulary.Ruleset, ManifestVocabulary.Base]
                        || (next is [ManifestVocabulary.Game, ManifestVocabulary.Ruleset] && value is YamlScalarNode))
                        ReferenceNode(value, AuthoringSymbolKind.Ruleset, file: file);
                    else if (path is [ManifestVocabulary.Characters, _, ManifestVocabulary.Inventory]) ReferenceNode(key, AuthoringSymbolKind.Item, file: file);
                    else if (next is [ManifestVocabulary.Characters, _, ManifestVocabulary.Inventory] && value is YamlSequenceNode)
                        ReferenceNode(value, AuthoringSymbolKind.Item, file: file);
                    else if (path is [ManifestVocabulary.Characters, _, ManifestVocabulary.Stats] or [ManifestVocabulary.Items, _, ManifestVocabulary.Modifiers] or [ManifestVocabulary.Game, ManifestVocabulary.Creation, ManifestVocabulary.Stats]
                        or [ManifestVocabulary.Game, ManifestVocabulary.Creation, ManifestVocabulary.Presets, _, ManifestVocabulary.Stats]) ReferenceNode(key, AuthoringSymbolKind.Stat, file: file);
                    if (next is [ManifestVocabulary.Game] or [ManifestVocabulary.Characters] or [ManifestVocabulary.Characters, _] or [ManifestVocabulary.Characters, _, ManifestVocabulary.Inventory]
                        or [ManifestVocabulary.Characters, _, ManifestVocabulary.Stats] or [ManifestVocabulary.Items] or [ManifestVocabulary.Items, _] or [ManifestVocabulary.Items, _, ManifestVocabulary.Modifiers]
                        or [ManifestVocabulary.Game, ManifestVocabulary.Ruleset] or [ManifestVocabulary.Game, ManifestVocabulary.Creation] or [ManifestVocabulary.Game, ManifestVocabulary.Creation, ManifestVocabulary.Stats] or [ManifestVocabulary.Game, ManifestVocabulary.Creation, ManifestVocabulary.Presets]
                        or [ManifestVocabulary.Game, ManifestVocabulary.Creation, ManifestVocabulary.Presets, _] or [ManifestVocabulary.Game, ManifestVocabulary.Creation, ManifestVocabulary.Presets, _, ManifestVocabulary.Stats])
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
        if (file == StorySource.ManifestFileName && Get(root, ManifestVocabulary.Game) is YamlMappingNode game &&
            Get(game, ManifestVocabulary.Id) is YamlScalarNode { Value: { Length: > 0 } storyId } idNode)
        {
            Scalar(idNode);
            var location = new SourceLocation(file, (int)idNode.Start.Line, (int)idNode.Start.Column);
            Symbols.Add(new(storyId,             AuthoringSymbolKind.Story, ManifestVocabulary.SymbolStory,
                _scalars[location].Range(0, storyId.Length)));
        }
        foreach (var (name, kind, type) in new[]
        {
            (ManifestVocabulary.Characters, AuthoringSymbolKind.Character, ManifestVocabulary.SymbolCharacter),
            (ManifestVocabulary.Items, AuthoringSymbolKind.Item, ManifestVocabulary.SymbolItem),
            (ManifestVocabulary.Factions, AuthoringSymbolKind.Faction, ManifestVocabulary.SymbolFaction),
            (ManifestVocabulary.Vars, AuthoringSymbolKind.Variable, ManifestVocabulary.SymbolInteger),
        })
        {
            if (Get(root, name) is YamlMappingNode map)
                foreach (var key in map.Children.Keys) Add(key, kind, type);
        }
        if (Get(root, ManifestVocabulary.Flags) is YamlSequenceNode flags)
            foreach (var flag in flags.Children) Add(flag, AuthoringSymbolKind.Flag, ManifestVocabulary.SymbolBoolean);
        if (file == StorySource.ManifestFileName && Get(root, ManifestVocabulary.Game) is YamlMappingNode gameConfig &&
            Get(gameConfig, ManifestVocabulary.Ruleset) is YamlMappingNode ruleset &&
            Get(ruleset, ManifestVocabulary.Stats) is YamlSequenceNode stats)
            foreach (var stat in stats.Children.OfType<YamlMappingNode>())
                if (Get(stat, ManifestVocabulary.Id) is { } id) Add(id, AuthoringSymbolKind.Stat, ManifestVocabulary.SymbolInteger);
    }

    internal void World(WorldDefinition world, IReadOnlySet<VarId>? unresolved = null)
    {
        AuthoredWorld = world;
        foreach (var id in world.Variables.Keys) _variableTypes[id.Value] = ManifestVocabulary.SymbolInteger;
        foreach (var id in world.TextVariables.Keys) _variableTypes[id.Value] = ManifestVocabulary.SymbolText;
        foreach (var id in unresolved ?? new HashSet<VarId>())
            _variableTypes[id.Value] = ManifestVocabulary.SymbolUnresolved;
        for (var index = 0; index < Symbols.Count; index++)
        {
            var symbol = Symbols[index];
            if (symbol.Kind == AuthoringSymbolKind.Variable && _variableTypes.TryGetValue(symbol.Name, out var type))
                Symbols[index] = symbol with { Type = type };
        }
        foreach (var stat in world.Ruleset.Stats)
            if (!Symbols.Any(symbol => symbol.Kind == AuthoringSymbolKind.Stat && symbol.Name == stat.Id.Value))
                Symbols.Add(new(stat.Id.Value, AuthoringSymbolKind.Stat, ManifestVocabulary.SymbolInteger, null));
    }

    internal void CurrentWorld(WorldDefinition world) => AuthoredWorld = world;

    internal void Registrations()
    {
        foreach (var name in options.Predicates.Names)
            Symbols.Add(new(name, AuthoringSymbolKind.Predicate, ManifestVocabulary.SymbolBoolean, null));
        foreach (var name in options.Modifiers.Names)
            Symbols.Add(new(name, AuthoringSymbolKind.Modifier, ManifestVocabulary.SymbolText, null));
        foreach (var name in options.Rulesets.Keys)
            Symbols.Add(new(name, AuthoringSymbolKind.Ruleset, ManifestVocabulary.Ruleset, null));
    }

    internal void DiscoverWithoutManifest(CancellationToken cancellationToken,
        Func<string, string, CancellationToken, TaleFileSyntax> syntax)
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
            var fileSyntax = syntax(file, text, cancellationToken);
            ClassificationSources.Add((file, fileSyntax));
            TaleDeclarations.Collect(file, fileSyntax, Scene, null, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostics = new DiagnosticBag();
            TaleBinder.Parse(file, fileSyntax, symbols, diagnostics, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            RecoveryImageDiagnostics.AddRange(diagnostics.Items.Where(diagnostic =>
                diagnostic.Code is CompilerDiagnosticCodes.UnsafeImagePath or CompilerDiagnosticCodes.UnsupportedImageFormat or
                    CompilerDiagnosticCodes.MissingImage or CompilerDiagnosticCodes.AmbiguousImagePath or
                    CompilerDiagnosticCodes.MissingImageAlt or CompilerDiagnosticCodes.DecorativeImageHasAlt or
                    CompilerDiagnosticCodes.MissingAccessibleChoiceLabel));
        }
    }
}
