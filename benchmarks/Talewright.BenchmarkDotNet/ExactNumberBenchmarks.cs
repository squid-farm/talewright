using BenchmarkDotNet.Attributes;

namespace Talewright.BenchmarkDotNet;

[MemoryDiagnoser]
public class ExactNumberBenchmarks
{
    [Params(100, 1000)]
    public int HistoryDepth { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var current = ExactNumberWorkload.ApplyOnePercentCurrentHitPoints(HistoryDepth);
        var history = ExactNumberWorkload.BuildRetainedHistory(HistoryDepth);
        if (history.Count != HistoryDepth || history[^1].NewValue != current)
            throw new InvalidOperationException("The repeated-effect and retained-history workloads diverged.");

        Console.WriteLine(
            $"Exact number history {HistoryDepth}: current-value magnitude=" +
            $"{ExactNumberWorkload.ValueMagnitudeBytes(current)} bytes, " +
            $"retained-value magnitude={ExactNumberWorkload.HistoryMagnitudeBytes(history)} bytes, " +
            $"retained managed heap delta={ExactNumberWorkload.MeasureRetainedHistoryBytes(HistoryDepth)} bytes, " +
            $"canonical SHA-256={ExactNumberWorkload.CanonicalResultFingerprint(HistoryDepth)}.");
    }

    [Benchmark]
    public Talewright.Core.ExactNumber ApplyRepeatedOnePercentEffect() =>
        ExactNumberWorkload.ApplyOnePercentCurrentHitPoints(HistoryDepth);

    [Benchmark]
    public List<NumericResourceChange> BuildRetainedEffectHistory() =>
        ExactNumberWorkload.BuildRetainedHistory(HistoryDepth);
}
