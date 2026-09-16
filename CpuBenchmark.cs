using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace ClearMark;

internal record CpuResult(BenchTest Test, double Value);

internal static class CpuBenchmark
{
    private const int Iterations = 5;
    private const double MinSampleSeconds = 0.15;

    public static List<CpuResult> Run(Action<string> onStatus)
    {
        var results = new List<CpuResult>();
        int threadCount = ThreadPinning.LogicalProcessorCount;

        onStatus("CPU Integer (single-thread)...");
        results.Add(Measure1T(BenchTest.Integer1T, () => SieveOfEratosthenes(500_000)));

        onStatus("CPU Integer (multi-thread)...");
        results.Add(Measure(BenchTest.IntegerNT, () =>
        {
            var perThread = new double[threadCount];
            ThreadPinning.PinnedFor(threadCount, i => perThread[i] = SieveOfEratosthenes(500_000));
            return perThread.Sum();
        }));

        onStatus("CPU Float (single-thread)...");
        results.Add(Measure1T(BenchTest.Float1T, () => MatrixMultiply(256)));

        onStatus("CPU Float (multi-thread)...");
        results.Add(Measure(BenchTest.FloatNT, () =>
        {
            var perThread = new double[threadCount];
            ThreadPinning.PinnedFor(threadCount, i => perThread[i] = MatrixMultiply(256));
            return perThread.Sum();
        }));

        onStatus("CPU Crypto (single-thread)...");
        results.Add(Measure1T(BenchTest.Crypto1T, () => CryptoHash(64 * 1024 * 1024)));

        onStatus("CPU Crypto (multi-thread)...");
        results.Add(Measure(BenchTest.CryptoNT, () =>
        {
            var perThread = new double[threadCount];
            ThreadPinning.PinnedFor(threadCount, i => perThread[i] = CryptoHash(64 * 1024 * 1024));
            return perThread.Sum();
        }));

        onStatus("CPU Compression (single-thread)...");
        results.Add(Measure1T(BenchTest.Compression1T, () => BrotliCompress(16 * 1024 * 1024)));

        onStatus("CPU Compression (multi-thread)...");
        results.Add(Measure(BenchTest.CompressionNT, () =>
        {
            var perThread = new double[threadCount];
            ThreadPinning.PinnedFor(threadCount, i => perThread[i] = BrotliCompress(16 * 1024 * 1024));
            return perThread.Sum();
        }));

        return results;
    }

    private static CpuResult Measure(BenchTest test, Func<double> work)
        => new(test, Measurement.Median(Iterations, work, collectGc: true));

    private static CpuResult Measure1T(BenchTest test, Func<double> work)
        => ThreadPinning.RunOnPreferred1T(() => Measure(test, work));

    /// <summary>Repeat kernel until the sample is long enough that thread spawn and JIT are a small fraction of the time.</summary>
    private static double TimeRepeats(Action kernel, Func<double> unitsPerRun)
    {
        kernel();
        var sw = Stopwatch.StartNew();
        int runs = 0;
        do
        {
            kernel();
            runs++;
        } while (sw.Elapsed.TotalSeconds < MinSampleSeconds);
        sw.Stop();
        return unitsPerRun() * runs / sw.Elapsed.TotalSeconds;
    }

    private static double SieveOfEratosthenes(int limit)
    {
        bool[] isComposite = new bool[limit + 1];
        long opsPerPass = 0;

        return TimeRepeats(() =>
        {
            Array.Clear(isComposite);
            long ops = 0;
            for (int i = 2; (long)i * i <= limit; i++)
            {
                if (!isComposite[i])
                {
                    for (int j = i * i; j <= limit; j += i)
                    {
                        isComposite[j] = true;
                        ops++;
                    }
                }
            }

            int count = 0;
            for (int i = 2; i <= limit; i++)
                if (!isComposite[i]) count++;

            ops += limit;
            _ = count;
            opsPerPass = ops;
        }, () => opsPerPass / 1_000_000.0);
    }

    private static double MatrixMultiply(int n)
    {
        double[,] a = new double[n, n], b = new double[n, n], c = new double[n, n];
        var rng = new Random(42);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                a[i, j] = rng.NextDouble();
                b[i, j] = rng.NextDouble();
            }

        double flops = 2.0 * n * n * n;
        return TimeRepeats(() =>
        {
            for (int i = 0; i < n; i++)
                for (int k = 0; k < n; k++)
                {
                    double aik = a[i, k];
                    for (int j = 0; j < n; j++)
                        c[i, j] += aik * b[k, j];
                }
            _ = c[0, 0];
        }, () => flops / 1_000_000.0);
    }

    private static double CryptoHash(int bytes)
    {
        byte[] data = new byte[bytes];
        Random.Shared.NextBytes(data);
        return TimeRepeats(() =>
        {
            byte[] hash = SHA256.HashData(data);
            _ = hash[0];
        }, () => bytes / (1024.0 * 1024.0));
    }

    private static double BrotliCompress(int bytes)
    {
        byte[] data = new byte[bytes];
        Random.Shared.NextBytes(data);
        using var output = new MemoryStream(bytes);
        return TimeRepeats(() =>
        {
            output.SetLength(0);
            using var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true);
            brotli.Write(data, 0, data.Length);
        }, () => bytes / (1024.0 * 1024.0));
    }
}
