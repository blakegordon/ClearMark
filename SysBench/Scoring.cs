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
        // Memory
        ["Seq. Bandwidth"]     = 40_000,   // MB/s — DDR5 dual-channel
        ["Random Latency"]     = 70,       // ns (lower is better — scoring inverted)
        ["Copy Bandwidth"]     = 35_000,   // MB/s
        // Storage — modern Gen4 NVMe
        ["Seq. Read"]          = 5_000,    // MB/s
        ["Seq. Write"]         = 4_000,    // MB/s
        ["4K Rand Read"]       = 80,       // MB/s — good Gen4 NVMe at QD1
        ["4K Rand Write"]      = 200,      // MB/s — good Gen4 NVMe at QD1
    };

    // Tests where lower values are better
    private static readonly HashSet<string> LowerIsBetter = ["Random Latency"];

    // ── Composite profile weights ────────────────────────────────────────
    // Weights must sum to 1.0 within each profile.

    public const double GamingSingleCore  = 0.35, GamingMultiCore  = 0.15, GamingMemory  = 0.20, GamingStorage  = 0.30;
    public const double ProdSingleCore    = 0.15, ProdMultiCore    = 0.40, ProdMemory    = 0.25, ProdStorage    = 0.20;
    public const double BalancedSingleCore = 0.25, BalancedMultiCore = 0.25, BalancedMemory = 0.25, BalancedStorage = 0.25;

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
        List<CpuResult> cpu, List<MemoryResult> mem, List<StorageResult> storage)
    {
        double cpu1T = AverageScore(cpu.Where(r => r.TestName.EndsWith("(1T)")).Select(r => ScoreOne(r.TestName, r.Value)));
        double cpuNT = AverageScore(cpu.Where(r => r.TestName.EndsWith("(nT)")).Select(r => ScoreOne(r.TestName, r.Value)));
        double memScore = AverageScore(mem.Select(r => ScoreOne(r.TestName, r.Value)));
        // When storage is skipped, redistribute its weight proportionally
        bool hasStorage = storage.Count > 0;
        double storScore = hasStorage ? AverageScore(storage.Select(r => ScoreOne(r.TestName, r.Value))) : 0;

        double Calc(double w1T, double wNT, double wMem, double wStor)
        {
            if (!hasStorage) { double s = 1.0 / (1.0 - wStor); w1T *= s; wNT *= s; wMem *= s; wStor = 0; }
            return cpu1T * w1T + cpuNT * wNT + memScore * wMem + storScore * wStor;
        }

        double gaming  = Calc(GamingSingleCore, GamingMultiCore, GamingMemory, GamingStorage);
        double prod    = Calc(ProdSingleCore, ProdMultiCore, ProdMemory, ProdStorage);
        double balanced = Calc(BalancedSingleCore, BalancedMultiCore, BalancedMemory, BalancedStorage);

        return (gaming, prod, balanced);
    }

    private static double AverageScore(IEnumerable<double> scores)
    {
        var list = scores.ToList();
        return list.Count > 0 ? list.Average() : 0;
    }
}
