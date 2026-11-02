using Talewright.Talemark.Diagnostics;

namespace Talewright.Talemark.Analysis;

/// <summary>
/// Maps one expression's completion-text offsets to authored positions. Instances are not shared between
/// threads: each request builds its own, so the lazily built boundary table needs no synchronization.
/// </summary>
internal sealed class ExpressionSourceMap(ExpressionSiteSource source)
{
    private SourceLocation[]? _boundaries;

    internal SourceRange Map(int offset, int length)
    {
        var expressionLength = source.ExpressionLength;
        if (offset > expressionLength)
        {
            var point = source.Range.End.Offset(offset - expressionLength);
            return new(point, point.Offset(length));
        }
        if (source.Scalar is { } scalar) return scalar.Range(offset, length);
        if (source.Locate is { } locate) return new(locate(offset), locate(offset + length));
        if (_boundaries is null)
        {
            var text = source.Text;
            var boundaries = new SourceLocation[expressionLength + 1];
            boundaries[0] = source.Location;
            for (var index = 0; index < expressionLength; index++)
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
            _boundaries = boundaries;
        }
        return new(_boundaries[offset], _boundaries[offset + length]);
    }
}

/// <summary>
/// What analysis retains for one expression so its completion sites can be computed on request.
/// <see cref="Text"/> is the completion text: the expression plus trailing whitespace on its final line.
/// </summary>
internal sealed class ExpressionSiteSource(string text, int expressionLength, ExpressionContextKind kind,
    SourceRange range, SourceLocation location, YamlScalarSource? scalar, Func<int, SourceLocation>? locate)
{
    internal string Text { get; } = text;
    internal int ExpressionLength { get; } = expressionLength;
    internal ExpressionContextKind Kind { get; } = kind;
    internal SourceRange Range { get; } = range;
    internal SourceLocation Location { get; } = location;
    internal YamlScalarSource? Scalar { get; } = scalar;
    internal Func<int, SourceLocation>? Locate { get; } = locate;

    internal IEnumerable<CompletionSite> Sites(IReadOnlyList<AuthoringSymbol> symbols) =>
        ExpressionCompletion.Sites(Text, Kind, new ExpressionSourceMap(this).Map, symbols);
}

/// <summary>A precomputed site, or an expression whose sites are computed on request, covering <see cref="Region"/>.</summary>
internal readonly record struct CompletionSiteSource(SourceRange Region, CompletionSite? Site, ExpressionSiteSource? Expression)
{
    internal static CompletionSiteSource Of(CompletionSite site) => new(site.Range, site, null);

    internal IEnumerable<CompletionSite> Sites(IReadOnlyList<AuthoringSymbol> symbols) =>
        Site is not null ? [Site] : Expression!.Sites(symbols);
}

/// <summary>
/// Finds the site sources whose regions contain a position, preserving analysis order. The position index is
/// built on the first query, so analyses that are never queried do not pay for it.
/// </summary>
internal sealed class CompletionSiteIndex(CompletionSiteSource[] sources)
{
    private readonly CompletionSiteSource[] _sources = sources;
    private Dictionary<string, FileSources>? _files;

    // Sources sorted by region start; MaxEnds[i] is the furthest region end among the first i + 1 entries.
    private sealed record FileSources(int[] Order, SourceLocation[] Starts, SourceLocation[] MaxEnds);

    private Dictionary<string, FileSources> Build()
    {
        var files = new Dictionary<string, FileSources>(StringComparer.Ordinal);
        foreach (var group in Enumerable.Range(0, _sources.Length).GroupBy(index => _sources[index].Region.Start.File))
        {
            var order = group.OrderBy(index => _sources[index].Region.Start, Comparer<SourceLocation>.Create(Compare))
                .ThenBy(index => index).ToArray();
            var starts = new SourceLocation[order.Length];
            var maxEnds = new SourceLocation[order.Length];
            for (var index = 0; index < order.Length; index++)
            {
                var region = _sources[order[index]].Region;
                starts[index] = region.Start;
                maxEnds[index] = index > 0 && Compare(maxEnds[index - 1], region.End) > 0 ? maxEnds[index - 1] : region.End;
            }
            files.Add(group.Key, new(order, starts, maxEnds));
        }
        return files;
    }

    /// <summary>Sites whose range contains <paramref name="position"/>, including a range ending there.</summary>
    internal IReadOnlyList<CompletionSite> At(SourceLocation position, IReadOnlyList<AuthoringSymbol> symbols)
    {
        var files = LazyInitializer.EnsureInitialized(ref _files, Build);
        if (position.File is null || !files.TryGetValue(position.File, out var file)) return [];
        var low = 0;
        var high = file.Starts.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (Compare(file.Starts[middle], position) <= 0) low = middle + 1;
            else high = middle;
        }
        List<int>? matches = null;
        for (var index = low - 1; index >= 0 && Compare(position, file.MaxEnds[index]) <= 0; index--)
            if (Compare(position, _sources[file.Order[index]].Region.End) <= 0)
                (matches ??= []).Add(file.Order[index]);
        if (matches is null) return [];
        matches.Sort();
        var result = new List<CompletionSite>();
        foreach (var index in matches)
            foreach (var site in _sources[index].Sites(symbols))
                if (site.Range.Start.File == position.File && Compare(site.Range.Start, position) <= 0 &&
                    Compare(position, site.Range.End) <= 0)
                    result.Add(site);
        return result;
    }

    internal IEnumerable<CompletionSite> All(IReadOnlyList<AuthoringSymbol> symbols) =>
        _sources.SelectMany(source => source.Sites(symbols));

    private static int Compare(SourceLocation left, SourceLocation right) =>
        left.Line != right.Line ? left.Line.CompareTo(right.Line) : left.Column.CompareTo(right.Column);
}
