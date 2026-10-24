namespace Talewright.Talemark.Diagnostics;

public enum DiagnosticSeverity
{
    Warning,
    Error,
}

/// <summary>A position in a story source file. Lines and columns are 1-based.</summary>
public readonly record struct SourceLocation(string File, int Line, int Column)
{
    public static SourceLocation None { get; } = new(string.Empty, 0, 0);

    public SourceLocation Offset(int columns) => this with { Column = Column + columns };

    public override string ToString() => Line > 0 ? $"{File}:{Line}:{Column}" : File;
}

/// <summary>
/// A compiler message. Codes are stable (<c>TW1xxx</c> syntax, <c>TW2xxx</c> references/types,
/// <c>TW3xxx</c> story structure, <c>TW4xxx</c> story.talewright.yaml) so tools and docs can refer to them.
/// </summary>
public sealed record Diagnostic(DiagnosticSeverity Severity, string Code, string Message, SourceLocation Location)
{
    public override string ToString() => $"{Location}: {Severity.ToString().ToLowerInvariant()} {Code}: {Message}";
}

/// <summary>Collects diagnostics during compilation.</summary>
public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = [];

    public IReadOnlyList<Diagnostic> Items => _items;

    public bool HasErrors => _items.Any(d => d.Severity == DiagnosticSeverity.Error);

    public void Error(string code, string message, SourceLocation location) =>
        _items.Add(new Diagnostic(DiagnosticSeverity.Error, code, message, location));

    public void Warning(string code, string message, SourceLocation location) =>
        _items.Add(new Diagnostic(DiagnosticSeverity.Warning, code, message, location));
}
