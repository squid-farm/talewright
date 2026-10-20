namespace Talewright.Core.Randomness;

/// <summary>
/// Deterministic dice: the n-th roll of a playthrough is a pure function of <c>(seed, n)</c>.
/// Combined with <see cref="Events.DiceRolled"/> events this makes every playthrough reproducible
/// and prevents save-scumming by reloading (the same roll comes back).
/// </summary>
public static class DeterministicDice
{
    /// <summary>Rolls a die with <paramref name="sides"/> faces (1..sides) for the given cursor position.</summary>
    public static int Roll(ulong seed, int index, int sides)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sides, 1);
        var value = SplitMix64(seed ^ SplitMix64((ulong)index + 0x632BE59BD9B4E019UL));
        return (int)(value % (ulong)sides) + 1;
    }

    /// <summary>Creates a fresh, non-deterministic seed for a new playthrough.</summary>
    public static ulong NewSeed() => (ulong)Random.Shared.NextInt64();

    private static ulong SplitMix64(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }
}
