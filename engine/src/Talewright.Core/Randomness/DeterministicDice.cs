namespace Talewright.Core.Randomness;

/// <summary>
/// Deterministic dice: the n-th roll of a playthrough is a pure function of <c>(seed, n)</c>.
/// Combined with <see cref="Events.DiceRolled"/> events this makes every playthrough reproducible
/// and prevents save-scumming by reloading (the same roll comes back).
/// </summary>
/// <remarks>
/// The hash is SplitMix64 (Steele, Lea &amp; Flood, "Fast splittable pseudorandom number generators", 2014; the
/// same mixer Java's <c>SplittableRandom</c> uses). Its constants are the published ones: changing any of them
/// changes every roll of every saved game, so they must never be edited.
/// </remarks>
public static class DeterministicDice
{
    /// <summary>
    /// Offset added to the roll index before hashing so that index 0 doesn't hash the bare seed. Any odd 64-bit
    /// constant works; this one is arbitrary but fixed forever for save compatibility.
    /// </summary>
    private const ulong IndexSalt = 0x632BE59BD9B4E019UL;

    /// <summary>SplitMix64 increment: 2^64 / φ (the golden ratio), which spreads consecutive inputs apart.</summary>
    private const ulong GoldenGamma = 0x9E3779B97F4A7C15UL;

    /// <summary>SplitMix64's first avalanche multiplier (Stafford's "Mix13" variant).</summary>
    private const ulong MixMultiplier1 = 0xBF58476D1CE4E5B9UL;

    /// <summary>SplitMix64's second avalanche multiplier (Stafford's "Mix13" variant).</summary>
    private const ulong MixMultiplier2 = 0x94D049BB133111EBUL;

    private const int MixShift1 = 30;
    private const int MixShift2 = 27;
    private const int MixShift3 = 31;

    /// <summary>Rolls a die with <paramref name="sides"/> faces (1..sides) for the given cursor position.</summary>
    public static int Roll(ulong seed, int index, int sides)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sides, 1);
        var value = SplitMix64(seed ^ SplitMix64((ulong)index + IndexSalt));
        return (int)(value % (ulong)sides) + 1;
    }

    /// <summary>Creates a fresh, non-deterministic seed for a new playthrough.</summary>
    public static ulong NewSeed() => (ulong)Random.Shared.NextInt64();

    private static ulong SplitMix64(ulong x)
    {
        x += GoldenGamma;
        x = (x ^ (x >> MixShift1)) * MixMultiplier1;
        x = (x ^ (x >> MixShift2)) * MixMultiplier2;
        return x ^ (x >> MixShift3);
    }
}
