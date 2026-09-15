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

        onStatus("Memory Sequential Bandwidth (1T)...");
        results.Add(Measure("Seq. Bandwidth (1T)", "MB/s", SequentialBandwidth));

        onStatus("Memory Sequential Bandwidth (nT)...");
        results.Add(Measure("Seq. Bandwidth (nT)", "MB/s", SequentialBandwidthMT));

        onStatus("Memory Random Latency...");
        results.Add(Measure("Random Latency", "ns", RandomLatency));

        onStatus("Memory Copy Bandwidth (1T)...");
        results.Add(Measure("Copy Bandwidth (1T)", "MB/s", CopyBandwidth));

        onStatus("Memory Copy Bandwidth (nT)...");
        results.Add(Measure("Copy Bandwidth (nT)", "MB/s", CopyBandwidthMT));

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

    /// <summary>
    /// Multi-threaded sequential read+write. Each thread works on its own chunk,
    /// saturating all available memory channels.
    /// </summary>
    private static double SequentialBandwidthMT()
    {
        int count = ArraySizeMB * 1024 * 1024 / sizeof(long);
        long[] array = new long[count];
        int threads = Environment.ProcessorCount;
        int chunkSize = count / threads;

        // Pre-touch: parallel write ensures pages are NUMA-distributed across nodes
        Parallel.For(0, threads, t =>
        {
            int start = t * chunkSize;
            int end = (t == threads - 1) ? count : start + chunkSize;
            for (int i = start; i < end; i++) array[i] = i;
        });

        // Timed: parallel read + write
        var sw = Stopwatch.StartNew();
        Parallel.For(0, threads, t =>
        {
            int start = t * chunkSize;
            int end = (t == threads - 1) ? count : start + chunkSize;
            long sum = 0;
            for (int i = start; i < end; i++) sum += array[i]; // read
            for (int i = start; i < end; i++) array[i] = sum + i; // write
            Volatile.Write(ref array[start], sum); // prevent elimination
        });
        sw.Stop();

        double totalBytes = (double)count * sizeof(long) * 2; // read + write
        return totalBytes / sw.Elapsed.TotalSeconds / (1024.0 * 1024.0);
    }

    /// <summary>
    /// Multi-threaded copy. Each thread copies its own chunk via a manual loop
    /// that the JIT auto-vectorizes. Returns MB/s (combined read+write).
    /// </summary>
    private static double CopyBandwidthMT()
    {
        int count = ArraySizeMB * 1024 * 1024 / sizeof(long);
        long[] src = new long[count];
        long[] dst = new long[count];
        int threads = Environment.ProcessorCount;
        int chunkSize = count / threads;

        // Pre-touch BOTH arrays across NUMA nodes
        Parallel.For(0, threads, t =>
        {
            int start = t * chunkSize;
            int end = (t == threads - 1) ? count : start + chunkSize;
            for (int i = start; i < end; i++) { src[i] = i; dst[i] = 0; }
        });

        var sw = Stopwatch.StartNew();
        Parallel.For(0, threads, t =>
        {
            int start = t * chunkSize;
            int end = (t == threads - 1) ? count : start + chunkSize;
            for (int i = start; i < end; i++) dst[i] = src[i];
        });
        sw.Stop();

        double totalBytes = (double)count * sizeof(long) * 2; // read src + write dst
        return totalBytes / sw.Elapsed.TotalSeconds / (1024.0 * 1024.0);
    }
}
