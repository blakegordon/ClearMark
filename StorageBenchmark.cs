using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ClearMark;

internal record StorageResult(BenchTest Test, double Value);

internal static class StorageBenchmark
{
    private const int Iterations = 3;
    private const long TestFileSizeBytes = 4L * 1024 * 1024 * 1024;
    private const int SequentialBlockSize = 1024 * 1024;
    private const int RandomBlockSize = 4096;

    // FILE_FLAG_NO_BUFFERING. Not a public FileOptions member, but File.OpenHandle accepts the bit.
    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    public static List<StorageResult> Run(Action<string> onStatus, string? targetPath = null)
    {
        var results = new List<StorageResult>();
        string dir = targetPath ?? Path.GetTempPath();
        string testFile = Path.Combine(dir, "clearmark_storage_test.tmp");
        SafeFileHandle? lifetime = null;

        try
        {
            // Held open with DeleteOnClose so a killed process does not leave a 4 GB temp file.
            lifetime = File.OpenHandle(
                testFile,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete,
                NoBuffering | FileOptions.DeleteOnClose,
                TestFileSizeBytes);
            RandomAccess.SetLength(lifetime, TestFileSizeBytes);

            onStatus($"Storage Sequential Write (4 GB to {dir})...");
            results.Add(MeasureWrite(testFile, BenchTest.SeqWrite, SequentialBlockSize, sequential: true));

            onStatus("Storage Sequential Read...");
            results.Add(MeasureRead(testFile, BenchTest.SeqRead, SequentialBlockSize, sequential: true));

            onStatus("Storage Random 4K Write...");
            results.Add(MeasureWrite(testFile, BenchTest.RandWrite4K, RandomBlockSize, sequential: false));

            onStatus("Storage Random 4K Read...");
            results.Add(MeasureRead(testFile, BenchTest.RandRead4K, RandomBlockSize, sequential: false));
        }
        finally
        {
            lifetime?.Dispose();
            try { File.Delete(testFile); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        return results;
    }

    private static StorageResult MeasureWrite(string path, BenchTest test, int blockSize, bool sequential)
        => new(test, Measurement.Median(Iterations, () => DoWrite(path, blockSize, sequential)));

    private static StorageResult MeasureRead(string path, BenchTest test, int blockSize, bool sequential)
        => new(test, Measurement.Median(Iterations, () => DoRead(path, blockSize, sequential)));

    private static SafeFileHandle OpenUnbuffered(
        string path, FileAccess access, bool sequential, bool writeThrough)
    {
        FileOptions options = NoBuffering
            | (sequential ? FileOptions.SequentialScan : FileOptions.RandomAccess)
            | (writeThrough ? FileOptions.WriteThrough : 0);
        return File.OpenHandle(path, FileMode.Open, access, FileShare.ReadWrite | FileShare.Delete, options);
    }

    private static double DoWrite(string path, int blockSize, bool sequential)
    {
        long blocksToWrite = sequential ? TestFileSizeBytes / blockSize : 50_000;
        long bytesWritten = blocksToWrite * blockSize;

        unsafe
        {
            byte* raw = (byte*)NativeMemory.AlignedAlloc((nuint)blockSize, 4096);
            try
            {
                var span = new Span<byte>(raw, blockSize);
                Random.Shared.NextBytes(span);

                using var handle = OpenUnbuffered(path, FileAccess.Write, sequential, writeThrough: !sequential);

                var rng = new Random(123);
                long maxBlock = TestFileSizeBytes / blockSize - 1;

                var sw = Stopwatch.StartNew();
                for (long i = 0; i < blocksToWrite; i++)
                {
                    long offset = sequential ? i * blockSize : (long)(rng.NextDouble() * maxBlock) * blockSize;
                    RandomAccess.Write(handle, span, offset);
                }
                RandomAccess.FlushToDisk(handle);
                sw.Stop();

                return bytesWritten / sw.Elapsed.TotalSeconds / (1024.0 * 1024.0);
            }
            finally
            {
                NativeMemory.AlignedFree(raw);
            }
        }
    }

    private static double DoRead(string path, int blockSize, bool sequential)
    {
        long blocksToRead = sequential ? TestFileSizeBytes / blockSize : 50_000;
        long bytesRead = blocksToRead * blockSize;

        unsafe
        {
            byte* raw = (byte*)NativeMemory.AlignedAlloc((nuint)blockSize, 4096);
            try
            {
                var span = new Span<byte>(raw, blockSize);
                using var handle = OpenUnbuffered(path, FileAccess.Read, sequential, writeThrough: false);

                long fileSize = RandomAccess.GetLength(handle);
                if (fileSize < TestFileSizeBytes)
                    throw new IOException($"Storage test file is {fileSize} bytes, expected {TestFileSizeBytes}");

                var rng = new Random(123);
                long maxBlock = fileSize / blockSize - 1;

                var sw = Stopwatch.StartNew();
                for (long i = 0; i < blocksToRead; i++)
                {
                    long offset = sequential ? i * blockSize : (long)(rng.NextDouble() * maxBlock) * blockSize;
                    int n = RandomAccess.Read(handle, span, offset);
                    if (n != blockSize)
                        throw new IOException($"Read expected {blockSize} bytes, got {n}");
                }
                sw.Stop();

                return bytesRead / sw.Elapsed.TotalSeconds / (1024.0 * 1024.0);
            }
            finally
            {
                NativeMemory.AlignedFree(raw);
            }
        }
    }
}
