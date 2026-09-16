namespace ClearMark;

internal enum BenchTest
{
    Integer1T, IntegerNT,
    Float1T, FloatNT,
    Crypto1T, CryptoNT,
    Compression1T, CompressionNT,
    SeqBandwidth1T, SeqBandwidthNT,
    CopyBandwidth1T, CopyBandwidthNT,
    RandomLatency,
    SeqRead, SeqWrite,
    RandRead4K, RandWrite4K,
    GpuFp32, GpuFp64, GpuInteger,
}

internal enum ScoreBucket { Cpu1T, CpuNT, Memory, Storage, Gpu, GpuFp64 }

internal readonly record struct TestSpec(
    string Name,
    string Unit,
    double Reference,
    bool LowerIsBetter,
    ScoreBucket Bucket);

/// <summary>
/// Transparent scoring. All weights and baselines are here — anyone can audit them.
/// Score = (yours / reference) × 100, inverted when lower is better.
/// </summary>
internal static class Scoring
{
    // Baselines ≈ mid-range 2024 desktop (i5-13400 / Ryzen 5 7600 / RTX 4060 / Gen4 NVMe).
    // nT CPU refs assume ~6–8 cores of scaling from the 1T baseline.
    public static TestSpec Spec(BenchTest test) => test switch
    {
        BenchTest.Integer1T       => new("Integer (1T)",       "Mops",   3_000,  false, ScoreBucket.Cpu1T),
        BenchTest.IntegerNT       => new("Integer (nT)",       "Mops",   20_000, false, ScoreBucket.CpuNT),
        BenchTest.Float1T         => new("Float (1T)",         "Mflops", 3_000,  false, ScoreBucket.Cpu1T),
        BenchTest.FloatNT         => new("Float (nT)",         "Mflops", 22_000, false, ScoreBucket.CpuNT),
        BenchTest.Crypto1T        => new("Crypto (1T)",        "MB/s",   2_000,  false, ScoreBucket.Cpu1T),
        BenchTest.CryptoNT        => new("Crypto (nT)",        "MB/s",   14_000, false, ScoreBucket.CpuNT),
        BenchTest.Compression1T   => new("Compression (1T)",   "MB/s",   2_500,  false, ScoreBucket.Cpu1T),
        BenchTest.CompressionNT   => new("Compression (nT)",   "MB/s",   18_000, false, ScoreBucket.CpuNT),
        BenchTest.SeqBandwidth1T  => new("Seq. Bandwidth (1T)", "MB/s",  40_000, false, ScoreBucket.Memory),
        BenchTest.SeqBandwidthNT  => new("Seq. Bandwidth (nT)", "MB/s",  80_000, false, ScoreBucket.Memory),
        BenchTest.CopyBandwidth1T => new("Copy Bandwidth (1T)", "MB/s",  70_000, false, ScoreBucket.Memory),
        BenchTest.CopyBandwidthNT => new("Copy Bandwidth (nT)", "MB/s",  70_000, false, ScoreBucket.Memory),
        BenchTest.RandomLatency   => new("Random Latency",     "ns",     70,     true,  ScoreBucket.Memory),
        BenchTest.SeqRead         => new("Seq. Read",          "MB/s",   5_000,  false, ScoreBucket.Storage),
        BenchTest.SeqWrite        => new("Seq. Write",         "MB/s",   4_000,  false, ScoreBucket.Storage),
        BenchTest.RandRead4K      => new("4K Rand Read",       "MB/s",   80,     false, ScoreBucket.Storage),
        BenchTest.RandWrite4K     => new("4K Rand Write",      "MB/s",   200,    false, ScoreBucket.Storage),
        BenchTest.GpuFp32         => new("GPU FP32",           "Mpix/s", 8_000,  false, ScoreBucket.Gpu),
        BenchTest.GpuFp64         => new("GPU FP64",           "Mpix/s", 150,    false, ScoreBucket.GpuFp64),
        BenchTest.GpuInteger      => new("GPU Integer",        "GIOPS",  10_000, false, ScoreBucket.Gpu),
        _ => throw new ArgumentOutOfRangeException(nameof(test), test, "Missing TestSpec"),
    };

    //                                   1T CPU  nT CPU  Memory  Storage  GPU
    public static readonly double[] GamingWeights      = [0.20,  0.10,   0.10,   0.20,   0.40];
    public static readonly double[] ProdWeights        = [0.10,  0.30,   0.15,   0.15,   0.30];
    public static readonly double[] BalancedWeights    = [0.20,  0.20,   0.20,   0.20,   0.20];

    public static double ScoreOne(BenchTest test, double value)
    {
        var spec = Spec(test);
        if (spec.LowerIsBetter)
            return spec.Reference / Math.Max(value, 0.001) * 100.0;
        return value / Math.Max(spec.Reference, 0.001) * 100.0;
    }

    /// <summary>
    /// Gaming never redistributes skipped categories (missing GPU/storage scores 0)
    /// and excludes FP64. Productivity and Balanced include FP64 and redistribute skips.
    /// </summary>
    public static (double Gaming, double Productivity, double Balanced) Composite(
        List<CpuResult> cpu, List<MemoryResult> mem, List<StorageResult> storage, List<GpuResult>? gpu)
    {
        double cpu1T = Average(cpu.Select(r => (r.Test, r.Value)), ScoreBucket.Cpu1T);
        double cpuNT = Average(cpu.Select(r => (r.Test, r.Value)), ScoreBucket.CpuNT);
        double memScore = Average(mem.Select(r => (r.Test, r.Value)));

        bool hasStorage = storage.Count > 0;
        double storScore = hasStorage ? Average(storage.Select(r => (r.Test, r.Value))) : 0;

        var primary = gpu?.Where(r => r.IsPrimary && !r.Unsupported && r.Value > 0).ToList() ?? [];
        bool hasGpu = primary.Count > 0;
        var gpuPairs = primary.Select(r => (r.Test, r.Value));
        double gpuNoFp64 = hasGpu ? Average(gpuPairs, exclude: ScoreBucket.GpuFp64) : 0;
        double gpuFull = hasGpu ? Average(gpuPairs) : 0;

        return (
            Weighted(GamingWeights, cpu1T, cpuNT, memScore, storScore, gpuNoFp64, hasStorage, hasGpu, redistribute: false),
            Weighted(ProdWeights, cpu1T, cpuNT, memScore, storScore, gpuFull, hasStorage, hasGpu, redistribute: true),
            Weighted(BalancedWeights, cpu1T, cpuNT, memScore, storScore, gpuFull, hasStorage, hasGpu, redistribute: true));
    }

    private static double Weighted(
        double[] w, double cpu1T, double cpuNT, double mem, double stor, double gpu,
        bool hasStorage, bool hasGpu, bool redistribute)
    {
        double[] scores = [cpu1T, cpuNT, mem, stor, gpu];
        bool[] active = [true, true, true, hasStorage, hasGpu];

        if (!redistribute)
        {
            double total = 0;
            for (int i = 0; i < 5; i++)
                total += scores[i] * w[i];
            return total;
        }

        double activeSum = 0;
        for (int i = 0; i < 5; i++)
            if (active[i]) activeSum += w[i];

        double redistributed = 0;
        for (int i = 0; i < 5; i++)
            if (active[i]) redistributed += scores[i] * w[i] / activeSum;
        return redistributed;
    }

    private static double Average(IEnumerable<(BenchTest Test, double Value)> results, ScoreBucket? only = null, ScoreBucket? exclude = null)
    {
        var scores = new List<double>();
        foreach (var (test, value) in results)
        {
            var bucket = Spec(test).Bucket;
            if (only is { } o && bucket != o)
                continue;
            if (exclude is { } e && bucket == e)
                continue;
            scores.Add(ScoreOne(test, value));
        }
        return scores.Count > 0 ? scores.Average() : 0;
    }
}
