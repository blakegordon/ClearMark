using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ClearMark;

/// <summary>
/// Pins threads with SetThreadGroupAffinity so logical processors above 64
/// (and other processor groups) are addressable. SetThreadAffinityMask cannot.
/// </summary>
internal static class ThreadPinning
{
    public static int LogicalProcessorCount
    {
        get
        {
            uint n = NativeMethods.GetActiveProcessorCount(NativeMethods.ALL_PROCESSOR_GROUPS);
            return n > 0 ? (int)n : Environment.ProcessorCount;
        }
    }

    public static NativeMethods.GROUP_AFFINITY AffinityFor(int logicalIndex)
    {
        ushort groupCount = NativeMethods.GetActiveProcessorGroupCount();
        if (groupCount == 0)
            groupCount = 1;

        int remaining = logicalIndex;
        for (ushort g = 0; g < groupCount; g++)
        {
            uint count = NativeMethods.GetActiveProcessorCount(g);
            if (count == 0)
                continue;
            if (remaining < (int)count)
            {
                return new NativeMethods.GROUP_AFFINITY
                {
                    Mask = (UIntPtr)(1UL << remaining),
                    Group = g
                };
            }
            remaining -= (int)count;
        }

        return new NativeMethods.GROUP_AFFINITY { Mask = (UIntPtr)1, Group = 0 };
    }

    public static void PinCurrentThread(int logicalIndex)
    {
        var affinity = AffinityFor(logicalIndex);
        if (!NativeMethods.SetThreadGroupAffinity(NativeMethods.GetCurrentThread(), in affinity, out _))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Failed to pin thread to logical processor {logicalIndex}");
    }

    public static T RunOnPreferred1T<T>(Func<T> body)
        => RunOnLogicalProcessor(CpuTopology.Preferred1TLogicalIndex, body);

    public static T RunOnLogicalProcessor<T>(int logicalIndex, Func<T> body)
    {
        IntPtr thread = NativeMethods.GetCurrentThread();
        var affinity = AffinityFor(logicalIndex);
        if (!NativeMethods.SetThreadGroupAffinity(thread, in affinity, out var previous))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Failed to pin thread to logical processor {logicalIndex}");
        try
        {
            return body();
        }
        finally
        {
            NativeMethods.SetThreadGroupAffinity(thread, in previous, out _);
        }
    }

    /// <summary>
    /// Run body(t) for t in [0, count) on dedicated threads, each pinned to logical processor t.
    /// First-touch and nT bandwidth stay on the local NUMA node for that processor.
    /// </summary>
    public static void PinnedFor(int count, Action<int> body)
    {
        var threads = new Thread[count];

        for (int t = 0; t < count; t++)
        {
            int tid = t;
            threads[t] = new Thread(() =>
            {
                PinCurrentThread(tid);
                body(tid);
            })
            { IsBackground = true };
            threads[t].Start();
        }

        for (int t = 0; t < count; t++)
            threads[t].Join();
    }
}
