using System.Security.Cryptography;
using System.Text;
using Talewright.Core;

namespace Talewright.BenchmarkDotNet;

public sealed record NumericResourceChange(ExactNumber OldValue, ExactNumber NewValue);

public static class ExactNumberWorkload
{
    private static readonly ExactNumber OnePercent = ExactNumber.FromFraction(1, 100);
    private static readonly ExactNumber StartingHitPoints = ExactNumber.FromInteger(100);

    public static ExactNumber ApplyOnePercentCurrentHitPoints(int turns)
    {
        var hitPoints = StartingHitPoints;
        for (var turn = 0; turn < turns; turn++)
            hitPoints -= hitPoints * OnePercent;

        return hitPoints;
    }

    public static List<NumericResourceChange> BuildRetainedHistory(int turns)
    {
        var history = new List<NumericResourceChange>(turns);
        var hitPoints = StartingHitPoints;
        for (var turn = 0; turn < turns; turn++)
        {
            var nextHitPoints = hitPoints - hitPoints * OnePercent;
            history.Add(new NumericResourceChange(hitPoints, nextHitPoints));
            hitPoints = nextHitPoints;
        }

        return history;
    }

    public static long ValueMagnitudeBytes(ExactNumber value) =>
        value.Numerator.GetByteCount(isUnsigned: true) + value.Denominator.GetByteCount(isUnsigned: true);

    public static string CanonicalResultFingerprint(int turns)
    {
        var canonical = ApplyOnePercentCurrentHitPoints(turns).ToCanonicalString();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static long HistoryMagnitudeBytes(IReadOnlyList<NumericResourceChange> history)
    {
        if (history.Count == 0)
            return 0;

        var bytes = ValueMagnitudeBytes(history[0].OldValue);
        foreach (var change in history)
            bytes += ValueMagnitudeBytes(change.NewValue);

        return bytes;
    }

    public static long MeasureRetainedHistoryBytes(int turns)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var history = BuildRetainedHistory(turns);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var retained = GC.GetTotalMemory(forceFullCollection: true) - before;
        GC.KeepAlive(history);
        return retained;
    }
}
