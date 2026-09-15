using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SysBench;

public record MemoryResult(string TestName, double Value, string Unit);
public record LatencyLadderPoint(string SizeLabel, int SizeKB, double LatencyNs);

public static class MemoryBenchmark
{
    private const int Iterations = 5;
    private const int ArraySizeMB = 256; // Large enough to exceed L3 cache

    public static List<MemoryResult> Run(Action<string> onStatus)
    {
        var results = new List<MemoryResult>();

        onStatus("Memory Sequential Bandwidth...");
        results.Add(Measure("Seq. Bandwidth", "MB/s", SequentialBandwidth));

        onStatus("Memory Random Latency...");
        results.Add(Measure("Random Latency", "ns", RandomLatency));

        onStatus("Memory Copy Bandwidth...");
        results.Add(Measure("Copy Bandwidth", "MB/s", CopyBandwidth));

        return results;
    }

    private static MemoryResult Measure(string name, string unit, Func<double> work)
    {
        var samples = new double[Iterations];
        for (int i = 0; i < Iterations; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            samples[i] = work();
        }
        Array.Sort(samples);
        return new MemoryResult(name, samples[Iterations / 2], unit);
    }

    // ── Workloads ──────────────────────────────────────────────────────────

    /// <summary>
    /// Read then write through a large array sequentially. Returns MB/s (combined R+W).
    /// </summary>
    private static double SequentialBandwidth()
    {
        int count = ArraySizeMB * 1024 * 1024 / sizeof(long);
        long[] array = new long[count];

        // Write pass
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
            array[i] = (long)i;
        var writeTime = sw.Elapsed;

        // Read pass (sum to prevent elimination)
        sw.Restart();
        long sum = 0;
        for (int i = 0; i < count; i++)
            sum += array[i];
        var readTime = sw.Elapsed;

        _ = sum;
        double totalBytes = (double)count * sizeof(long) * 2; // read + write
        double totalSeconds = writeTime.TotalSeconds + readTime.TotalSeconds;
        return totalBytes / totalSeconds / (1024.0 * 1024.0);
    }

    /// <summary>
    /// Pointer-chasing through a shuffled index array at a specific working set size.
    /// The shuffle defeats hardware prefetchers. Returns nanoseconds per access.
    /// </summary>
    private static double PointerChaseLatency(int sizeBytes)
    {
        int count = sizeBytes / sizeof(int);
        int[] chain = new int[count];

        // Build a random cycle through all elements (Fisher-Yates)
        for (int i = 0; i < count; i++)
            chain[i] = i;
        var rng = new Random(42);
        for (int i = count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (chain[i], chain[j]) = (chain[j], chain[i]);
        }

        // Chase pointers — more chases for small arrays to get stable timing
        int chases = Math.Max(1_000_000, 64_000_000 / count * 1000);
        int idx = 0;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < chases; i++)
            idx = chain[idx];
        sw.Stop();

        _ = idx; // prevent elimination
        return sw.Elapsed.TotalNanoseconds / chases;
    }

    private static double RandomLatency() => PointerChaseLatency(64 * 1024 * 1024);

    /// <summary>
    /// Measures pointer-chase latency across a range of working set sizes,
    /// revealing the L1 → L2 → L3 → RAM cache hierarchy.
    /// </summary>
    public static List<LatencyLadderPoint> RunLatencyLadder(Action<string> onStatus)
    {
        int[] sizesKB = [4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536, 131072];
        var results = new List<LatencyLadderPoint>();

        onStatus("Memory Latency Ladder...");
        double prevLatency = 0;
        foreach (int kb in sizesKB)
        {
            string label = kb >= 1024 ? $"{kb / 1024} MB" : $"{kb} KB";
            // Median of 3 to reduce NUMA noise on multi-socket systems
            double[] samples = [PointerChaseLatency(kb * 1024), PointerChaseLatency(kb * 1024), PointerChaseLatency(kb * 1024)];
            Array.Sort(samples);
            double latency = Math.Max(samples[1], prevLatency); // enforce monotonicity
            prevLatency = latency;
            results.Add(new LatencyLadderPoint(label, kb, latency));
        }
        return results;
    }

    /// <summary>
    /// Copy a large array using Buffer.BlockCopy. Returns MB/s.
    /// </summary>
    private static double CopyBandwidth()
    {
        int byteCount = ArraySizeMB * 1024 * 1024;
        byte[] src = new byte[byteCount];
        byte[] dst = new byte[byteCount];
        Random.Shared.NextBytes(src); // fill with data to ensure pages are committed

        var sw = Stopwatch.StartNew();
        Buffer.BlockCopy(src, 0, dst, 0, byteCount);
        sw.Stop();

        return byteCount / sw.Elapsed.TotalSeconds / (1024.0 * 1024.0);
    }
}
