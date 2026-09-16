using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ClearMark;

internal record StorageResult(string TestName, double Value, string Unit);

internal static class StorageBenchmark
{
    private const int Iterations = 3; // Fewer iterations since storage tests are slow
    private const long TestFileSizeBytes = 4L * 1024 * 1024 * 1024; // 4 GB to defeat SSD caches
    private const int SequentialBlockSize = 1024 * 1024; // 1 MB blocks
    private const int RandomBlockSize = 4096; // 4 KB blocks

    // ── Win32 interop for true unbuffered I/O ────────────────────────────
    // .NET's FileStream cannot reliably pass FILE_FLAG_NO_BUFFERING through
    // a cast because it manages its own internal buffers. We P/Invoke to
    // CreateFile/ReadFile/WriteFile directly to guarantee the OS page cache
    // is fully bypassed — critical on machines with hundreds of GB of RAM.

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint CREATE_ALWAYS = 2;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    private const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;
    private const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;
    private const uint FILE_FLAG_RANDOM_ACCESS = 0x10000000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        SafeFileHandle hFile, IntPtr lpBuffer, uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        SafeFileHandle hFile, IntPtr lpBuffer, uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFilePointerEx(
        SafeFileHandle hFile, long liDistanceToMove,
        out long lpNewFilePointer, uint dwMoveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEndOfFile(SafeFileHandle hFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushFileBuffers(SafeFileHandle hFile);

    private const uint FILE_BEGIN = 0;

    public static List<StorageResult> Run(Action<string> onStatus, string? targetPath = null)
    {
        var results = new List<StorageResult>();
        string dir = targetPath ?? Path.GetTempPath();
        string testFile = Path.Combine(dir, "sysbench_storage_test.tmp");

        try
        {
            // --- Sequential Write ---
            onStatus($"Storage Sequential Write (4 GB to {dir})...");
            results.Add(MeasureWrite(testFile, "Seq. Write", SequentialBlockSize, sequential: true));

            // --- Sequential Read ---
            onStatus("Storage Sequential Read...");
            results.Add(MeasureRead(testFile, "Seq. Read", SequentialBlockSize, sequential: true));

            // --- Random 4K Write (rewrite the existing file with random seeks) ---
            onStatus("Storage Random 4K Write...");
            results.Add(MeasureWrite(testFile, "4K Rand Write", RandomBlockSize, sequential: false));

            // --- Random 4K Read ---
            onStatus("Storage Random 4K Read...");
            results.Add(MeasureRead(testFile, "4K Rand Read", RandomBlockSize, sequential: false));
        }
        finally
        {
            try { File.Delete(testFile); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        return results;
    }

    private static StorageResult MeasureWrite(string path, string name, int blockSize, bool sequential)
    {
        var samples = new double[Iterations];
        for (int i = 0; i < Iterations; i++)
            samples[i] = DoWrite(path, blockSize, sequential);
        Array.Sort(samples);
        return new StorageResult(name, samples[Iterations / 2], "MB/s");
    }

    private static StorageResult MeasureRead(string path, string name, int blockSize, bool sequential)
    {
        var samples = new double[Iterations];
        for (int i = 0; i < Iterations; i++)
            samples[i] = DoRead(path, blockSize, sequential);
        Array.Sort(samples);
        return new StorageResult(name, samples[Iterations / 2], "MB/s");
    }

    // ── I/O operations using direct Win32 calls ───────────────────────────

    private static double DoWrite(string path, int blockSize, bool sequential)
    {
        long totalBlocks = TestFileSizeBytes / blockSize;
        long blocksToWrite = sequential ? totalBlocks : 50_000;
        long bytesWritten = blocksToWrite * blockSize;

        // Allocate sector-aligned memory (required by FILE_FLAG_NO_BUFFERING)
        IntPtr buffer;
        unsafe { buffer = (IntPtr)NativeMemory.AlignedAlloc((nuint)blockSize, 4096); }
        try
        {
            // Fill with random data
            Span<byte> span;
            unsafe { span = new Span<byte>(buffer.ToPointer(), blockSize); }
            Random.Shared.NextBytes(span);

            // Sequential writes: NoBuffering only (let drive use its write buffer).
            // Random writes: NoBuffering + WriteThrough (honest per-I/O commit latency).
            uint flags = FILE_ATTRIBUTE_NORMAL | FILE_FLAG_NO_BUFFERING
                       | (sequential ? FILE_FLAG_SEQUENTIAL_SCAN : FILE_FLAG_RANDOM_ACCESS | FILE_FLAG_WRITE_THROUGH);

            // Ensure file exists at full size for random writes
            if (!sequential && !File.Exists(path))
            {
                using var setup = CreateFile(path, GENERIC_WRITE, 0, IntPtr.Zero, CREATE_ALWAYS,
                    FILE_ATTRIBUTE_NORMAL | FILE_FLAG_NO_BUFFERING, IntPtr.Zero);
                SetFilePointerEx(setup, TestFileSizeBytes, out _, FILE_BEGIN);
                SetEndOfFile(setup);
            }

            using var handle = CreateFile(path, GENERIC_WRITE, 0, IntPtr.Zero,
                sequential ? CREATE_ALWAYS : OPEN_EXISTING, flags, IntPtr.Zero);

            if (handle.IsInvalid)
                throw new IOException($"CreateFile failed for write: {Marshal.GetLastWin32Error()}");

            if (sequential)
            {
                SetFilePointerEx(handle, TestFileSizeBytes, out _, FILE_BEGIN);
                SetEndOfFile(handle);
                SetFilePointerEx(handle, 0, out _, FILE_BEGIN);
            }

            var rng = new Random(123);
            long maxBlock = TestFileSizeBytes / blockSize - 1;

            var sw = Stopwatch.StartNew();
            for (long i = 0; i < blocksToWrite; i++)
            {
                if (!sequential)
                {
                    long block = (long)(rng.NextDouble() * maxBlock);
                    SetFilePointerEx(handle, block * blockSize, out _, FILE_BEGIN);
                }
                WriteFile(handle, buffer, (uint)blockSize, out _, IntPtr.Zero);
            }
            FlushFileBuffers(handle);
            sw.Stop();

            return bytesWritten / sw.Elapsed.TotalSeconds / (1024.0 * 1024.0);
        }
        finally
        {
            unsafe { NativeMemory.AlignedFree(buffer.ToPointer()); }
        }
    }

    private static double DoRead(string path, int blockSize, bool sequential)
    {
        if (!File.Exists(path))
            return 0;

        long fileSize = new FileInfo(path).Length;
        long totalBlocks = fileSize / blockSize;
        long blocksToRead = sequential ? totalBlocks : 50_000;
        long bytesRead = blocksToRead * blockSize;

        // Allocate sector-aligned memory (required by FILE_FLAG_NO_BUFFERING)
        IntPtr buffer;
        unsafe { buffer = (IntPtr)NativeMemory.AlignedAlloc((nuint)blockSize, 4096); }
        try
        {
            uint flags = FILE_ATTRIBUTE_NORMAL | FILE_FLAG_NO_BUFFERING
                       | (sequential ? FILE_FLAG_SEQUENTIAL_SCAN : FILE_FLAG_RANDOM_ACCESS);

            using var handle = CreateFile(path, GENERIC_READ, 0, IntPtr.Zero,
                OPEN_EXISTING, flags, IntPtr.Zero);

            if (handle.IsInvalid)
                throw new IOException($"CreateFile failed for read: {Marshal.GetLastWin32Error()}");

            var rng = new Random(123);
            long maxBlock = fileSize / blockSize - 1;

            var sw = Stopwatch.StartNew();
            for (long i = 0; i < blocksToRead; i++)
            {
                if (!sequential)
                {
                    long block = (long)(rng.NextDouble() * maxBlock);
                    SetFilePointerEx(handle, block * blockSize, out _, FILE_BEGIN);
                }
                ReadFile(handle, buffer, (uint)blockSize, out uint read, IntPtr.Zero);
                if (read == 0) break;
            }
            sw.Stop();

            return bytesRead / sw.Elapsed.TotalSeconds / (1024.0 * 1024.0);
        }
        finally
        {
            unsafe { NativeMemory.AlignedFree(buffer.ToPointer()); }
        }
    }
}
