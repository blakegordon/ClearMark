using System.IO.Compression;
using System.Security.Cryptography;

namespace SysBench;

public record CpuResult(string TestName, double Value, string Unit);

public static class CpuBenchmark
{
    private const int Iterations = 5;

    public static List<CpuResult> Run(Action<string> onStatus)
    {
        var results = new List<CpuResult>();
        int threadCount = Environment.ProcessorCount;

        // --- Integer: Sieve of Eratosthenes ---
        onStatus("CPU Integer (single-thread)...");
        results.Add(Measure("Integer (1T)", "Mops", () => SieveOfEratosthenes(500_000)));

        onStatus("CPU Integer (multi-thread)...");
        results.Add(Measure("Integer (nT)", "Mops", () =>
        {
            var perThread = new double[threadCount];
            Parallel.For(0, threadCount, i => perThread[i] = SieveOfEratosthenes(500_000));
            return perThread.Sum();
        }));

        // --- Floating-Point: Matrix Multiply ---
        onStatus("CPU Float (single-thread)...");
        results.Add(Measure("Float (1T)", "Mflops", () => MatrixMultiply(256)));

        onStatus("CPU Float (multi-thread)...");
        results.Add(Measure("Float (nT)", "Mflops", () =>
        {
            var perThread = new double[threadCount];
            Parallel.For(0, threadCount, i => perThread[i] = MatrixMultiply(256));
            return perThread.Sum();
        }));

        // --- Crypto: SHA-256 ---
        onStatus("CPU Crypto (single-thread)...");
        results.Add(Measure("Crypto (1T)", "MB/s", () => CryptoHash(64 * 1024 * 1024)));

        onStatus("CPU Crypto (multi-thread)...");
        results.Add(Measure("Crypto (nT)", "MB/s", () =>
        {
            var perThread = new double[threadCount];
            Parallel.For(0, threadCount, i => perThread[i] = CryptoHash(64 * 1024 * 1024));
            return perThread.Sum();
        }));

        // --- Compression: Brotli ---
        onStatus("CPU Compression (single-thread)...");
        results.Add(Measure("Compression (1T)", "MB/s", () => BrotliCompress(16 * 1024 * 1024)));

        onStatus("CPU Compression (multi-thread)...");
        results.Add(Measure("Compression (nT)", "MB/s", () =>
        {
            var perThread = new double[threadCount];
            Parallel.For(0, threadCount, i => perThread[i] = BrotliCompress(16 * 1024 * 1024));
            return perThread.Sum();
        }));

        return results;
    }

    /// <summary>Runs a benchmark function multiple times and returns the median result.</summary>
    private static CpuResult Measure(string name, string unit, Func<double> work)
    {
        var samples = new double[Iterations];
        for (int i = 0; i < Iterations; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            samples[i] = work();
        }
        Array.Sort(samples);
        double median = samples[Iterations / 2];
        return new CpuResult(name, median, unit);
    }

    // ── Workloads ──────────────────────────────────────────────────────────

    /// <summary>Count primes up to limit using a sieve. Returns millions of operations per second.</summary>
    private static double SieveOfEratosthenes(int limit)
    {
        long ops = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        bool[] isComposite = new bool[limit + 1];
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
        // Count primes to ensure the work isn't optimized away
        int count = 0;
        for (int i = 2; i <= limit; i++)
            if (!isComposite[i]) count++;

        ops += limit; // counting pass
        sw.Stop();
        _ = count; // prevent dead-code elimination
        return ops / sw.Elapsed.TotalSeconds / 1_000_000.0;
    }

    /// <summary>Dense matrix multiply (C = A × B). Returns megaflops.</summary>
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

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < n; i++)
            for (int k = 0; k < n; k++)
            {
                double aik = a[i, k];
                for (int j = 0; j < n; j++)
                    c[i, j] += aik * b[k, j];
            }
        sw.Stop();

        // 2 * n^3 floating-point ops (multiply + add per inner iteration)
        double flops = 2.0 * n * n * n;
        _ = c[0, 0]; // prevent dead-code elimination
        return flops / sw.Elapsed.TotalSeconds / 1_000_000.0;
    }

    /// <summary>SHA-256 hash a block of data. Returns MB/s.</summary>
    private static double CryptoHash(int bytes)
    {
        byte[] data = new byte[bytes];
        Random.Shared.NextBytes(data);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        byte[] hash = SHA256.HashData(data);
        sw.Stop();

        _ = hash[0]; // prevent dead-code elimination
        return bytes / sw.Elapsed.TotalSeconds / (1024.0 * 1024.0);
    }

    /// <summary>Brotli-compress a block of data. Returns MB/s of input processed.</summary>
    private static double BrotliCompress(int bytes)
    {
        byte[] data = new byte[bytes];
        Random.Shared.NextBytes(data);

        using var output = new MemoryStream();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
            brotli.Write(data, 0, data.Length);
        sw.Stop();

        return bytes / sw.Elapsed.TotalSeconds / (1024.0 * 1024.0);
    }
}
