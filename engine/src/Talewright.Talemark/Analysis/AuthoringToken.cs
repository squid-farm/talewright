using Talewright.Talemark.Diagnostics;

namespace Talewright.Talemark.Analysis;

/// <summary>A parsed syntax token or manifest property, without editor-specific presentation.</summary>
public sealed record AuthoringToken(string Text, SourceRange Range, IReadOnlyList<string>? ManifestPath = null);
