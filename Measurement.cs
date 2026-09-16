namespace ClearMark;

/// <summary>
/// Shared sample policy: median of an odd iteration count.
/// CPU/memory: GC between samples, no warmup (median of 5 drops a cold JIT hit).
/// GPU: pass a warmup dispatch (shader compile). Storage: no GC.
/// </summary>
internal static class Measurement
{
    public static double Median(
        int iterations,
        Func<double> sample,
        bool collectGc = false,
        Action? warmup = null)
    {
        warmup?.Invoke();

        var samples = new double[iterations];
        for (int i = 0; i < iterations; i++)
        {
            if (collectGc)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }
            samples[i] = sample();
        }

        Array.Sort(samples);
        return samples[iterations / 2];
    }
}
