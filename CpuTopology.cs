using System.Management;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ClearMark;

/// <summary>
/// Processor topology: highest-efficiency-class core for 1T, L3 sizes for DRAM working sets.
/// </summary>
internal static class CpuTopology
{
    public static int Preferred1TLogicalIndex { get; }
    public static long MaxL3Bytes { get; }
    public static long TotalL3Bytes { get; }

    static CpuTopology()
    {
        Preferred1TLogicalIndex = QueryPreferred1T() ?? 0;
        var (maxL3, totalL3) = QueryL3();
        if (maxL3 == 0)
            (maxL3, totalL3) = QueryL3FromWmi();
        MaxL3Bytes = maxL3;
        TotalL3Bytes = totalL3;
    }

    private static int? QueryPreferred1T()
    {
        byte[]? buf = QueryRelation(NativeMethods.LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore);
        if (buf is null)
            return null;

        int bestIndex = 0;
        int bestEff = -1;

        unsafe
        {
            fixed (byte* p = buf)
            {
                byte* cur = p;
                byte* end = p + buf.Length;
                while (cur + 8 <= end)
                {
                    int relationship = *(int*)cur;
                    uint size = *(uint*)(cur + 4);
                    if (size < 8 || cur + size > end)
                        break;

                    if (relationship == (int)NativeMethods.LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore
                        && size >= 32 + (uint)sizeof(UIntPtr) + 2)
                    {
                        byte efficiency = cur[9];
                        UIntPtr mask = *(UIntPtr*)(cur + 32);
                        ushort group = *(ushort*)(cur + 32 + sizeof(UIntPtr));
                        int idx = LogicalIndex(group, mask);
                        if (efficiency > bestEff || (efficiency == bestEff && idx < bestIndex))
                        {
                            bestEff = efficiency;
                            bestIndex = idx;
                        }
                    }

                    cur += size;
                }
            }
        }

        return bestIndex;
    }

    private static (long maxBytes, long totalBytes) QueryL3()
    {
        byte[]? buf = QueryRelation(NativeMethods.LOGICAL_PROCESSOR_RELATIONSHIP.RelationCache);
        if (buf is null)
            return (0, 0);

        long max = 0, total = 0;
        unsafe
        {
            fixed (byte* p = buf)
            {
                byte* cur = p;
                byte* end = p + buf.Length;
                while (cur + 16 <= end)
                {
                    int relationship = *(int*)cur;
                    uint size = *(uint*)(cur + 4);
                    if (size < 16 || cur + size > end)
                        break;

                    if (relationship == (int)NativeMethods.LOGICAL_PROCESSOR_RELATIONSHIP.RelationCache)
                    {
                        byte level = cur[8];
                        uint cacheSize = *(uint*)(cur + 12);
                        if (level == 3 && cacheSize > 0)
                        {
                            if (cacheSize > max)
                                max = cacheSize;
                            total += cacheSize;
                        }
                    }

                    cur += size;
                }
            }
        }

        return (max, total);
    }

    private static (long maxBytes, long totalBytes) QueryL3FromWmi()
    {
        long max = 0, total = 0;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT L3CacheSize FROM Win32_Processor");
            foreach (var obj in searcher.Get())
            {
                long bytes = Convert.ToInt64(obj["L3CacheSize"]) * 1024;
                if (bytes <= 0)
                    continue;
                if (bytes > max)
                    max = bytes;
                total += bytes;
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException) { }

        return (max, total);
    }

    private static byte[]? QueryRelation(NativeMethods.LOGICAL_PROCESSOR_RELATIONSHIP relation)
    {
        uint len = 0;
        NativeMethods.GetLogicalProcessorInformationEx(relation, nint.Zero, ref len);
        if (len == 0)
            return null;

        var buf = new byte[len];
        unsafe
        {
            fixed (byte* p = buf)
            {
                uint len2 = len;
                if (!NativeMethods.GetLogicalProcessorInformationEx(relation, (nint)p, ref len2))
                    return null;
            }
        }
        return buf;
    }

    private static int LogicalIndex(ushort group, UIntPtr mask)
    {
        ulong m = mask.ToUInt64();
        int bit = m == 0 ? 0 : BitOperations.TrailingZeroCount(m);
        int index = bit;
        for (ushort g = 0; g < group; g++)
            index += (int)NativeMethods.GetActiveProcessorCount(g);
        return index;
    }
}
