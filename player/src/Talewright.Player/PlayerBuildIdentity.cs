namespace Talewright.Player;

/// <summary>The player build identity stamped at build time (#315); see BuildIdentity.targets.</summary>
internal static partial class PlayerBuildIdentity
{
    /// <summary>One line for display and bug reports, e.g. "Talewright 1.0.0 · commit abc1234 · built 2026-01-31".</summary>
    public static string Summary => $"Talewright {Version} · commit {Commit} · built {Date}";
}
