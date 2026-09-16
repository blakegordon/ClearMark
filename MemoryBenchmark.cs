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

    public static List<MemoryResult> Run(Action<string> onStatus)
    {
        var results = new List<MemoryResult>();

        onStatus("Memory Sequential Bandwidth (1T)...");
        results.Add(Measure1T(BenchTest.SeqBandwidth1T, SequentialBandwidth));

        onStatus("Memory Sequential Bandwidth (nT)...");
        results.Add(Measure(BenchTest.SeqBandwidthNT, SequentialBandwidthMT));

        onStatus("Memory Random Latency...");
        results.Add(Measure1T(BenchTest.RandomLatency, RandomLatency));

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

    /// <summary>Working set for DRAM tests: 4× the largest L3, at least 1 GB, 64-byte aligned.</summary>
    private static long DramWorkingSetBytes()
        => Align64(Math.Max(CpuTopology.MaxL3Bytes * 4, MinDramBytes));

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
    /// Single n-cycle (Sattolo) so the chase visits the whole working set. Shuffle defeats prefetchers.
    /// </summary>
    private static double PointerChaseLatency(int sizeBytes)
    {
        int count = sizeBytes / sizeof(int);
        int[] chain = new int[count];
        for (int i = 0; i < count; i++)
            chain[i] = i;

        var rng = new Random(42);
        for (int i = count - 1; i > 0; i--)
        {
            int j = rng.Next(i);
            (chain[i], chain[j]) = (chain[j], chain[i]);
        }

        int chases = Math.Max(1_000_000, 64_000_000 / count * 1000);
        int idx = 0;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < chases; i++)
            idx = chain[idx];
        sw.Stop();

        _ = idx;
        return sw.Elapsed.TotalNanoseconds / chases;
    }

    private static double RandomLatency()
    {
        long size = DramWorkingSetBytes();
        if (size > int.MaxValue)
            size = int.MaxValue & ~3L;
        return PointerChaseLatency((int)size);
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
                double[] samples =
                [
                    PointerChaseLatency(kb * 1024),
                    PointerChaseLatency(kb * 1024),
                    PointerChaseLatency(kb * 1024)
                ];
                Array.Sort(samples);
                results.Add(new LatencyLadderPoint(label, kb, samples[1]));
            }
            return results;
        });
    }

    private static int[] LadderSizesKB()
    {
        long maxL3 = CpuTopology.MaxL3Bytes;
        int maxKB = (int)Math.Max(128 * 1024, (maxL3 * 2) / 1024);
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
            ThreadPinning.PinnedFor(threads, t =>
            {
                long start = t * perThread;
                long end = start + perThread;
                for (long i = start; i < end; i++) { src[i] = i; dst[i] = 0; }
            });

            var sw = Stopwatch.StartNew();
            ThreadPinning.PinnedFor(threads, t =>
            {
                long start = t * perThread;
                long end = start + perThread;
                long sum = 0;
                for (long i = start; i < end; i++)
                    sum += src[i];
                FillNt(dst, start, end, sum);
                dst[start] = sum;
            });
            sw.Stop();

            return MbPerSec(totalBytes * 2.0, sw.Elapsed.TotalSeconds);
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
            ThreadPinning.PinnedFor(threads, t =>
            {
                long start = t * perThread;
                long end = start + perThread;
                for (long i = start; i < end; i++) { src[i] = i; dst[i] = 0; }
            });

            var sw = Stopwatch.StartNew();
            ThreadPinning.PinnedFor(threads, t =>
            {
                long start = t * perThread;
                long end = start + perThread;
                CopyNt(src, dst, start, end);
            });
            sw.Stop();

            return MbPerSec(totalBytes * 2.0, sw.Elapsed.TotalSeconds);
        }
        finally
        {
            NativeMemory.AlignedFree(src);
            NativeMemory.AlignedFree(dst);
        }
    }
}
