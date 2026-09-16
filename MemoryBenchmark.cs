using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace ClearMark;

internal record MemoryResult(BenchTest Test, double Value);
internal record LatencyLadderPoint(string SizeLabel, int SizeKB, double LatencyNs);

internal static class MemoryBenchmark
{
    private const int Iterations = 5;
    private const long MinDramBytes = 1L << 30;
    private const long MinPerThreadBytes = 64L * 1024 * 1024;
    private const long MinLatencyBytes = 128L * 1024 * 1024;
    private const int CacheLineBytes = 64;
    private const long MinTimedNBytes = 8L << 30;

    public static List<MemoryResult> Run(Action<string> onStatus)
    {
        var results = new List<MemoryResult>();

        onStatus("Memory Sequential Bandwidth (1T)...");
        results.Add(Measure1T(BenchTest.SeqBandwidth1T, SequentialBandwidth));

        onStatus("Memory Sequential Bandwidth (nT)...");
        results.Add(Measure(BenchTest.SeqBandwidthNT, SequentialBandwidthMT));

        onStatus("Memory Random Latency...");
        results.Add(ThreadPinning.RunOnPreferred1T(() =>
        {
            int size = (int)Math.Min(LatencyWorkingSetBytes(), int.MaxValue & ~63L);
            var (chain, start) = BuildChaseChain(size);
            double ns = Measurement.Median(Iterations, () => ChaseLatency(chain, start), collectGc: true);
            return new MemoryResult(BenchTest.RandomLatency, ns);
        }));

        onStatus("Memory Copy Bandwidth (1T)...");
        results.Add(Measure1T(BenchTest.CopyBandwidth1T, CopyBandwidth));

        onStatus("Memory Copy Bandwidth (nT)...");
        results.Add(Measure(BenchTest.CopyBandwidthNT, CopyBandwidthMT));

        return results;
    }

    private static MemoryResult Measure(BenchTest test, Func<double> work)
        => new(test, Measurement.Median(Iterations, work, collectGc: true));

    private static MemoryResult Measure1T(BenchTest test, Func<double> work)
        => ThreadPinning.RunOnPreferred1T(() => Measure(test, work));

    private static long Align64(long bytes) => bytes & ~63L;

    /// <summary>STREAM working set: 4× L3, at least 1 GB. Bandwidth tests can afford a large fill.</summary>
    private static long DramWorkingSetBytes()
        => Align64(Math.Max(CpuTopology.MaxL3Bytes * 4, MinDramBytes));

    /// <summary>
    /// RAM latency working set: 4× L3, at least 128 MB. No 1 GB floor — Sattolo on a 1 GB int[] is tens of seconds.
    /// </summary>
    private static long LatencyWorkingSetBytes()
        => Align64(Math.Max(CpuTopology.MaxL3Bytes * 4, MinLatencyBytes));

    private static long PerThreadBytes(int threads)
    {
        long total = Align64(Math.Max(CpuTopology.TotalL3Bytes * 4, MinDramBytes));
        long per = Align64(Math.Max(total / threads, MinPerThreadBytes));
        return per;
    }

    private static double MbPerSec(double bytes, double seconds)
        => bytes / seconds / (1024.0 * 1024.0);

    private static unsafe void CopyNt(long* src, long* dst, long start, long end)
    {
        if (Avx2.IsSupported)
        {
            int vecLen = Vector256<long>.Count;
            for (long i = start; i < end; i += vecLen)
            {
                var vec = Avx.LoadAlignedVector256((float*)(src + i));
                Avx.StoreAlignedNonTemporal((float*)(dst + i), vec);
            }
            Sse2.MemoryFence();
        }
        else
        {
            for (long i = start; i < end; i++)
                dst[i] = src[i];
        }
    }

    private static unsafe void FillNt(long* dst, long start, long end, long value)
    {
        if (Avx2.IsSupported)
        {
            int vecLen = Vector256<long>.Count;
            var vec = Vector256.Create(value);
            for (long i = start; i < end; i += vecLen)
                Avx2.StoreAlignedNonTemporal(dst + i, vec);
            Sse2.MemoryFence();
        }
        else
        {
            for (long i = start; i < end; i++)
                dst[i] = value;
        }
    }

    /// <summary>Sequential read of src then NT-store write of dst. Returns STREAM-style MB/s (2× payload).</summary>
    private static unsafe double SequentialBandwidth()
    {
        long totalBytes = DramWorkingSetBytes();
        long count = totalBytes / sizeof(long);
        long* src = (long*)NativeMemory.AlignedAlloc((nuint)totalBytes, 64);
        long* dst = (long*)NativeMemory.AlignedAlloc((nuint)totalBytes, 64);
        try
        {
            for (long i = 0; i < count; i++) { src[i] = i; dst[i] = 0; }

            var sw = Stopwatch.StartNew();
            long sum = 0;
            for (long i = 0; i < count; i++)
                sum += src[i];
            FillNt(dst, 0, count, sum);
            sw.Stop();

            _ = dst[0];
            return MbPerSec(totalBytes * 2.0, sw.Elapsed.TotalSeconds);
        }
        finally
        {
            NativeMemory.AlignedFree(src);
            NativeMemory.AlignedFree(dst);
        }
    }

    /// <summary>
    /// One node per cache line so the working set is <paramref name="sizeBytes"/> but Sattolo is size/64, not size/4.
    /// Single cycle (Sattolo) defeats prefetchers.
    /// </summary>
    private static (int[] chain, int start) BuildChaseChain(int sizeBytes)
    {
        int stride = CacheLineBytes / sizeof(int);
        int nodes = Math.Max(2, sizeBytes / CacheLineBytes);
        int[] chain = new int[nodes * stride];
        int[] order = new int[nodes];
        for (int i = 0; i < nodes; i++)
            order[i] = i;

        var rng = new Random(42);
        for (int i = nodes - 1; i > 0; i--)
        {
            int j = rng.Next(i);
            (order[i], order[j]) = (order[j], order[i]);
        }

        for (int i = 0; i < nodes; i++)
            chain[order[i] * stride] = order[(i + 1) % nodes] * stride;

        return (chain, order[0] * stride);
    }

    private static double ChaseLatency(int[] chain, int start)
    {
        const int chases = 2_000_000;
        int idx = start;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < chases; i++)
            idx = chain[idx];
        sw.Stop();
        _ = idx;
        return sw.Elapsed.TotalNanoseconds / chases;
    }

    public static List<LatencyLadderPoint> RunLatencyLadder(Action<string> onStatus)
    {
        onStatus("Memory Latency Ladder...");
        return ThreadPinning.RunOnPreferred1T(() =>
        {
            var results = new List<LatencyLadderPoint>();
            foreach (int kb in LadderSizesKB())
            {
                string label = kb >= 1024 ? $"{kb / 1024} MB" : $"{kb} KB";
                var (chain, start) = BuildChaseChain(kb * 1024);
                double[] samples =
                [
                    ChaseLatency(chain, start),
                    ChaseLatency(chain, start),
                    ChaseLatency(chain, start)
                ];
                Array.Sort(samples);
                results.Add(new LatencyLadderPoint(label, kb, samples[1]));
            }
            return results;
        });
    }

    private static int[] LadderSizesKB()
    {
        long maxBytes = Math.Max(LatencyWorkingSetBytes(), Math.Max(128L * 1024 * 1024, CpuTopology.MaxL3Bytes * 2));
        int maxKB = (int)Math.Min(maxBytes / 1024, int.MaxValue / 2);
        var sizes = new List<int>();
        for (int kb = 4; kb <= maxKB && kb > 0; kb *= 2)
            sizes.Add(kb);
        return [.. sizes];
    }

    /// <summary>NT-store copy. STREAM-style MB/s (read+write = 2× payload).</summary>
    private static unsafe double CopyBandwidth()
    {
        long totalBytes = DramWorkingSetBytes();
        long count = totalBytes / sizeof(long);
        long* src = (long*)NativeMemory.AlignedAlloc((nuint)totalBytes, 64);
        long* dst = (long*)NativeMemory.AlignedAlloc((nuint)totalBytes, 64);
        try
        {
            for (long i = 0; i < count; i++) { src[i] = i; dst[i] = 0; }

            var sw = Stopwatch.StartNew();
            CopyNt(src, dst, 0, count);
            sw.Stop();

            return MbPerSec(totalBytes * 2.0, sw.Elapsed.TotalSeconds);
        }
        finally
        {
            NativeMemory.AlignedFree(src);
            NativeMemory.AlignedFree(dst);
        }
    }

    /// <summary>
    /// nT sequential: read src, NT-store fill dst (separate arrays so the write is not cache-hot).
    /// </summary>
    private static unsafe double SequentialBandwidthMT()
    {
        int threads = ThreadPinning.LogicalProcessorCount;
        long perThreadBytes = PerThreadBytes(threads);
        long perThread = perThreadBytes / sizeof(long);
        long count = perThread * threads;
        long totalBytes = count * sizeof(long);

        long* src = (long*)NativeMemory.AlignedAlloc((nuint)totalBytes, 64);
        long* dst = (long*)NativeMemory.AlignedAlloc((nuint)totalBytes, 64);
        try
        {
            int repeats = (int)Math.Max(1, MinTimedNBytes / totalBytes);
            double seconds = ThreadPinning.PinnedForTimed(threads,
                t =>
                {
                    long start = t * perThread;
                    long end = start + perThread;
                    for (long i = start; i < end; i++) { src[i] = i; dst[i] = 0; }
                },
                t =>
                {
                    long start = t * perThread;
                    long end = start + perThread;
                    for (int r = 0; r < repeats; r++)
                    {
                        long sum = 0;
                        for (long i = start; i < end; i++)
                            sum += src[i];
                        FillNt(dst, start, end, sum);
                        dst[start] = sum;
                    }
                });

            return MbPerSec(totalBytes * 2.0 * repeats, seconds);
        }
        finally
        {
            NativeMemory.AlignedFree(src);
            NativeMemory.AlignedFree(dst);
        }
    }

    private static unsafe double CopyBandwidthMT()
    {
        int threads = ThreadPinning.LogicalProcessorCount;
        long perThreadBytes = PerThreadBytes(threads);
        long perThread = perThreadBytes / sizeof(long);
        long count = perThread * threads;
        long totalBytes = count * sizeof(long);

        long* src = (long*)NativeMemory.AlignedAlloc((nuint)totalBytes, 64);
        long* dst = (long*)NativeMemory.AlignedAlloc((nuint)totalBytes, 64);
        try
        {
            int repeats = (int)Math.Max(1, MinTimedNBytes / totalBytes);
            double seconds = ThreadPinning.PinnedForTimed(threads,
                t =>
                {
                    long start = t * perThread;
                    long end = start + perThread;
                    for (long i = start; i < end; i++) { src[i] = i; dst[i] = 0; }
                },
                t =>
                {
                    long start = t * perThread;
                    long end = start + perThread;
                    for (int r = 0; r < repeats; r++)
                        CopyNt(src, dst, start, end);
                });

            return MbPerSec(totalBytes * 2.0 * repeats, seconds);
        }
        finally
        {
            NativeMemory.AlignedFree(src);
            NativeMemory.AlignedFree(dst);
        }
    }
}
