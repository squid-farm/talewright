namespace Talewright.Runtime;

/// <summary>The candidate cannot safely reconstruct this timeline; explicit fresh setup is required.</summary>
public sealed class AuthorReloadException(string reason) : InvalidOperationException(
    $"Automatic reload refused: {reason}. Explicit fresh restart/setup is required.");
