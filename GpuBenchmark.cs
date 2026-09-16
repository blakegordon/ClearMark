using ComputeSharp;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClearMark;

internal record GpuResult(string DeviceName, bool IsPrimary, BenchTest Test, double Value, bool Unsupported = false);

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

    public static List<GpuResult>? Run(Action<string> onStatus)
    {
        GraphicsDevice[] devices;
        try { devices = [.. GraphicsDevice.QueryDevices(d => d.IsHardwareAccelerated)]; }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or Win32Exception) { return null; }

        if (devices.Length == 0) return null;

        var results = new List<GpuResult>();
        GraphicsDevice primaryDevice = ResolvePrimary(devices);

        for (int g = 0; g < devices.Length; g++)
        {
            var device = devices[g];
            bool isPrimary = device.Luid.Equals(primaryDevice.Luid);
            string name = device.Name;
            string label = devices.Length > 1 ? $"[[{name}]] " : "";

            // ── FP32: Mandelbrot ────────────────────────────────────────
            onStatus($"{label}GPU FP32 (Mandelbrot 4K×4K)...");
            using (var buf = device.AllocateReadWriteBuffer<int>(Pixels))
            {
                double val = MeasureMedian(() => device.For(Pixels, new MandelbrotShader(buf, Size, MaxIter)),
                                           elapsed => Pixels / elapsed / 1e6);
                results.Add(new GpuResult(name, isPrimary, BenchTest.GpuFp32, val));
            }

            // ── FP64: Mandelbrot (double precision) ─────────────────────
            onStatus($"{label}GPU FP64 (Mandelbrot 4K×4K double)...");
            try
            {
                using var buf = device.AllocateReadWriteBuffer<int>(Pixels);
                double val = MeasureMedian(() => device.For(Pixels, new MandelbrotFP64Shader(buf, Size, MaxIter)),
                                           elapsed => Pixels / elapsed / 1e6);
                results.Add(new GpuResult(name, isPrimary, BenchTest.GpuFp64, val));
            }
            catch (Exception ex) when (ex is NotSupportedException or COMException or InvalidOperationException or Win32Exception)
            { results.Add(new GpuResult(name, isPrimary, BenchTest.GpuFp64, 0, Unsupported: true)); }

            // ── Integer: Hash mixing ────────────────────────────────────
            onStatus($"{label}GPU Integer (Hash 16M×10K)...");
            using (var buf = device.AllocateReadWriteBuffer<uint>(Pixels))
            {
                double totalOps = (double)Pixels * HashIter * 7.0;
                double val = MeasureMedian(() => device.For(Pixels, new IntHashShader(buf, HashIter)),
                                           elapsed => totalOps / elapsed / 1e9);
                results.Add(new GpuResult(name, isPrimary, BenchTest.GpuInteger, val));
            }
        }

        return results;
    }

    /// <summary>DXGI adapter with the primary output, not the last WMI video controller.</summary>
    private static GraphicsDevice ResolvePrimary(GraphicsDevice[] devices)
    {
        try
        {
            var dxgi = GraphicsDevice.GetDefault();
            if (dxgi.IsHardwareAccelerated)
            {
                foreach (var d in devices)
                {
                    if (d.Luid.Equals(dxgi.Luid))
                        return d;
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or Win32Exception) { }

        return devices.OrderByDescending(d => d.DedicatedMemorySize).First();
    }

    /// <summary>Warm-up + median of N runs. scoreFunc converts elapsed seconds to a result value.</summary>
    private static double MeasureMedian(Action dispatch, Func<double, double> scoreFunc)
        => Measurement.Median(Runs, () =>
        {
            var sw = Stopwatch.StartNew();
            dispatch();
            sw.Stop();
            return scoreFunc(sw.Elapsed.TotalSeconds);
        }, warmup: dispatch);
}
