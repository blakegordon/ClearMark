namespace SysBench;

/// <summary>
/// Transparent scoring system. All weights and reference baselines are constants —
/// anyone can read, audit, or modify them.
/// </summary>
public static class Scoring
{
    // ── Reference baselines (≈ mid-range 2024 desktop) ──────────────────
    // Each value represents "good" performance; score = (yours / reference) × 100
    // These are hand-tuned to make a modern mid-range system score ~80-90.

    // CPU (Mops, Mflops, MB/s, MB/s for the 4 test types, single-thread)
    // Calibrated so a mid-range 2024 desktop (e.g. i5-13400 / Ryzen 5 7600) scores ~80-90.
    // The nT references assume ~8 cores of scaling from the 1T baseline.
    public static readonly Dictionary<string, double> References = new()
    {
        // CPU — 1T baselines from expected mid-range 2024 desktop performance
        // Calibrated from real data: i5-1135G7 (2020 laptop) ≈ 2,000 Mops 1T.
        // A 2024 i5-13400 is ~40% faster IPC + higher clocks ≈ 3,000 Mops 1T.
        ["Integer (1T)"]       = 3_000,    // Mops — 500K sieve fits in L2, tests ALU
        ["Integer (nT)"]       = 20_000,   // Mops — ~6-8 cores @ 3,000 each
        ["Float (1T)"]         = 3_000,    // Mflops
        ["Float (nT)"]         = 22_000,   // Mflops
        ["Crypto (1T)"]        = 2_000,    // MB/s — SHA-256 with hardware acceleration
        ["Crypto (nT)"]        = 14_000,   // MB/s
        ["Compression (1T)"]   = 2_500,    // MB/s — Brotli Fastest is very fast on modern CPUs
        ["Compression (nT)"]   = 18_000,   // MB/s
        // Memory — 1T (single-channel limited)
        ["Seq. Bandwidth (1T)"] = 40_000,   // MB/s — DDR5 single-thread
        ["Random Latency"]      = 70,       // ns (lower is better — scoring inverted)
        ["Copy Bandwidth (1T)"] = 35_000,   // MB/s
        // Memory — nT (all channels saturated)
        ["Seq. Bandwidth (nT)"] = 80_000,   // MB/s — DDR5 dual-channel fully saturated
        ["Copy Bandwidth (nT)"] = 70_000,   // MB/s
        // Storage — modern Gen4 NVMe
        ["Seq. Read"]          = 5_000,    // MB/s
        ["Seq. Write"]         = 4_000,    // MB/s
        ["4K Rand Read"]       = 80,       // MB/s — good Gen4 NVMe at QD1
        ["4K Rand Write"]      = 200,      // MB/s — good Gen4 NVMe at QD1
        // GPU — mid-range 2024 (e.g. RTX 4060)
        // Calibrated from real data: RTX 4090 ≈ 26,000 FP32 / 30,000 INT (≈ 3× mid-range)
        //   Titan V ≈ 7,000 FP32 / 3,200 FP64 (Volta 1:2 ratio)
        //   Iris Xe ≈ 500 FP32 / 485 INT (integrated)
        ["GPU FP32"]           = 8_000,    // Mpix/s — Mandelbrot 4K×4K single-precision
        ["GPU FP64"]           = 150,      // Mpix/s — Mandelbrot 4K×4K double-precision
        ["GPU Integer"]        = 10_000,   // GIOPS — xorshift-multiply hash
    };

    // Tests where lower values are better
    private static readonly HashSet<string> LowerIsBetter = ["Random Latency"];

    // ── Composite profile weights ────────────────────────────────────────
    // Weights must sum to 1.0 within each profile.
    //                                   1T CPU  nT CPU  Memory  Storage  GPU
    public static readonly double[] GamingWeights      = [0.20,  0.10,   0.10,   0.20,   0.40];
    public static readonly double[] ProdWeights        = [0.10,  0.30,   0.15,   0.15,   0.30];
    public static readonly double[] BalancedWeights    = [0.20,  0.20,   0.20,   0.20,   0.20];

    /// <summary>Score a single test result (0–100+). Scores above 100 mean better than baseline.</summary>
    public static double ScoreOne(string testName, double value)
    {
        if (!References.TryGetValue(testName, out double reference))
            return 0;

        if (LowerIsBetter.Contains(testName))
            return reference / Math.Max(value, 0.001) * 100.0;

        return value / Math.Max(reference, 0.001) * 100.0;
    }

    /// <summary>Calculate Gaming/Productivity/Balanced composite scores.</summary>
    public static (double Gaming, double Productivity, double Balanced) Composite(
        List<CpuResult> cpu, List<MemoryResult> mem, List<StorageResult> storage, List<GpuResult>? gpu)
    {
        double cpu1T = AverageScore(cpu.Where(r => r.TestName.EndsWith("(1T)")).Select(r => ScoreOne(r.TestName, r.Value)));
        double cpuNT = AverageScore(cpu.Where(r => r.TestName.EndsWith("(nT)")).Select(r => ScoreOne(r.TestName, r.Value)));
        double memScore = AverageScore(mem.Select(r => ScoreOne(r.TestName, r.Value)));

        bool hasStorage = storage.Count > 0;
        double storScore = hasStorage ? AverageScore(storage.Select(r => ScoreOne(r.TestName, r.Value))) : 0;

        // Only score primary GPU tests that actually ran (exclude "unsupported" and secondary GPUs)
        var primaryGpu = gpu?.Where(r => r.IsPrimary && r.Value > 0).ToList();
        bool hasGpu = primaryGpu != null && primaryGpu.Count > 0;
        double gpuScore = hasGpu ? AverageScore(primaryGpu!.Select(r => ScoreOne(r.TestName, r.Value))) : 0;

        double[] scores = [cpu1T, cpuNT, memScore, storScore, gpuScore];
        bool[] active = [true, true, true, hasStorage, hasGpu];

        double Calc(double[] weights)
        {
            // Redistribute weight of skipped categories proportionally
            double activeSum = 0;
            for (int i = 0; i < 5; i++) if (active[i]) activeSum += weights[i];
            double total = 0;
            for (int i = 0; i < 5; i++) if (active[i]) total += scores[i] * weights[i] / activeSum;
            return total;
        }

        return (Calc(GamingWeights), Calc(ProdWeights), Calc(BalancedWeights));
    }

    private static double AverageScore(IEnumerable<double> scores)
    {
        var list = scores.ToList();
        return list.Count > 0 ? list.Average() : 0;
    }
}
