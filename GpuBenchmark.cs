using ComputeSharp;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClearMark;

internal record GpuResult(string DeviceName, bool IsPrimary, string TestName, double Value, string Unit);

// ── Shader: Mandelbrot set (floating-point stress test) ──────────────
// Each thread computes one pixel. Heavy on FP multiply/add.
[ThreadGroupSize(512, 1, 1)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct MandelbrotShader(ReadWriteBuffer<int> output, int width, int maxIter) : IComputeShader
{
    public void Execute()
    {
        int px = ThreadIds.X;
        float cx = (px % width - width * 0.5f) * 4.0f / width;
        float cy = (px / width - width * 0.5f) * 4.0f / width;
        float zx = 0, zy = 0;
        int i = 0;
        for (; i < maxIter && zx * zx + zy * zy < 4.0f; i++)
        {
            float t = zx * zx - zy * zy + cx;
            zy = 2.0f * zx * zy + cy;
            zx = t;
        }
        output[px] = i;
    }
}

// ── Shader: Mandelbrot FP64 (double-precision stress test) ───────────
// Identical workload to FP32 but using double. Exposes the FP64:FP32 ratio.
[ThreadGroupSize(512, 1, 1)]
[GeneratedComputeShaderDescriptor]
[RequiresDoublePrecisionSupport]
public readonly partial struct MandelbrotFP64Shader(ReadWriteBuffer<int> output, int width, int maxIter) : IComputeShader
{
    public void Execute()
    {
        int px = ThreadIds.X;
        double cx = (px % width - width * 0.5) * 4.0 / width;
        double cy = (px / width - width * 0.5) * 4.0 / width;
        double zx = 0, zy = 0;
        int i = 0;
        for (; i < maxIter && zx * zx + zy * zy < 4.0; i++)
        {
            double t = zx * zx - zy * zy + cx;
            zy = 2.0 * zx * zy + cy;
            zx = t;
        }
        output[px] = i;
    }
}

// ── Shader: Integer hash mixing (integer ALU stress test) ────────────
// Each thread runs a xorshift-multiply chain. Heavy on shift/XOR/multiply.
[ThreadGroupSize(512, 1, 1)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct IntHashShader(ReadWriteBuffer<uint> output, int iterations) : IComputeShader
{
    public void Execute()
    {
        uint h = (uint)ThreadIds.X + 1u;
        for (int i = 0; i < iterations; i++)
        {
            h ^= h << 13;
            h ^= h >> 17;
            h ^= h << 5;
            h *= 0x85ebca6bu;
        }
        output[ThreadIds.X] = h;
    }
}

internal static class GpuBenchmark
{
    private const int Size = 4096;         // 4096×4096 = 16M threads
    private const int Pixels = Size * Size;
    private const int MaxIter = 1000;
    private const int HashIter = 10_000;
    private const int Runs = 3;

    public static List<GpuResult>? Run(Action<string> onStatus, string? primaryGpuName = null)
    {
        GraphicsDevice[] devices;
        try { devices = [.. GraphicsDevice.QueryDevices(d => d.IsHardwareAccelerated)]; }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or Win32Exception) { return null; }

        if (devices.Length == 0) return null;

        var results = new List<GpuResult>();

        // If we have a WMI-detected GPU name, match it; otherwise first device is primary
        bool MatchesPrimary(string deviceName) => primaryGpuName != null && 
            (deviceName.Contains(primaryGpuName, StringComparison.OrdinalIgnoreCase) || primaryGpuName.Contains(deviceName, StringComparison.OrdinalIgnoreCase));

        // If no device matches the WMI name, fall back to first device
        bool anyMatch = primaryGpuName != null && devices.Any(d => MatchesPrimary(d.Name));

        for (int g = 0; g < devices.Length; g++)
        {
            var device = devices[g];
            bool isPrimary = anyMatch ? MatchesPrimary(device.Name) : g == 0;
            string name = device.Name;
            string label = devices.Length > 1 ? $"[[{name}]] " : "";

            // ── FP32: Mandelbrot ────────────────────────────────────────
            onStatus($"{label}GPU FP32 (Mandelbrot 4K×4K)...");
            using (var buf = device.AllocateReadWriteBuffer<int>(Pixels))
            {
                double val = MeasureMedian(() => device.For(Pixels, new MandelbrotShader(buf, Size, MaxIter)),
                                           elapsed => Pixels / elapsed / 1e6);
                results.Add(new GpuResult(name, isPrimary, "GPU FP32", val, "Mpix/s"));
            }

            // ── FP64: Mandelbrot (double precision) ─────────────────────
            onStatus($"{label}GPU FP64 (Mandelbrot 4K×4K double)...");
            try
            {
                using var buf = device.AllocateReadWriteBuffer<int>(Pixels);
                double val = MeasureMedian(() => device.For(Pixels, new MandelbrotFP64Shader(buf, Size, MaxIter)),
                                           elapsed => Pixels / elapsed / 1e6);
                results.Add(new GpuResult(name, isPrimary, "GPU FP64", val, "Mpix/s"));
            }
            catch (Exception ex) when (ex is NotSupportedException or COMException or InvalidOperationException or Win32Exception)
            { results.Add(new GpuResult(name, isPrimary, "GPU FP64", 0, "N/A (unsupported)")); }

            // ── Integer: Hash mixing ────────────────────────────────────
            onStatus($"{label}GPU Integer (Hash 16M×10K)...");
            using (var buf = device.AllocateReadWriteBuffer<uint>(Pixels))
            {
                double totalOps = (double)Pixels * HashIter * 7.0;
                double val = MeasureMedian(() => device.For(Pixels, new IntHashShader(buf, HashIter)),
                                           elapsed => totalOps / elapsed / 1e9);
                results.Add(new GpuResult(name, isPrimary, "GPU Integer", val, "GIOPS"));
            }
        }

        return results;
    }

    /// <summary>Warm-up + median of N runs. scoreFunc converts elapsed seconds to a result value.</summary>
    private static double MeasureMedian(Action dispatch, Func<double, double> scoreFunc)
    {
        dispatch(); // warm-up (shader compile + JIT)

        var samples = new double[Runs];
        for (int i = 0; i < Runs; i++)
        {
            var sw = Stopwatch.StartNew();
            dispatch();
            sw.Stop();
            samples[i] = scoreFunc(sw.Elapsed.TotalSeconds);
        }
        Array.Sort(samples);
        return samples[Runs / 2];
    }
}
