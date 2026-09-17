using System.Buffers.Binary;
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
    public static long MaxL1Bytes { get; }
    public static long MaxL2Bytes { get; }
    public static long MaxL3Bytes { get; }
    public static long TotalL3Bytes { get; }

    static CpuTopology()
    {
        Preferred1TLogicalIndex = QueryPreferred1T() ?? 0;
        var (l1, l2, l3, totalL3) = QueryCaches();
        if (l3 == 0)
            (l2, l3, totalL3) = QueryCacheFromWmi(l2);
        MaxL1Bytes = l1 > 0 ? l1 : 32 * 1024;
        MaxL2Bytes = l2 > 0 ? l2 : 512 * 1024;
        MaxL3Bytes = l3;
        TotalL3Bytes = totalL3;
    }

    private static int? QueryPreferred1T()
    {
        byte[]? buf = QueryRelation(NativeMethods.LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore);
        if (buf is null)
            return null;

        int bestIndex = 0;
        int bestEff = -1;
        int maskOffset = 32;
        int groupOffset = 32 + nint.Size;
        int minCoreSize = groupOffset + 2;

        ReadOnlySpan<byte> buffer = buf;
        int offset = 0;
        while (offset + 8 <= buffer.Length)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset + 4));
            if (size < 8 || offset + (int)size > buffer.Length)
                break;
            ReadOnlySpan<byte> rec = buffer.Slice(offset, (int)size);
            offset += (int)size;

            int relationship = BinaryPrimitives.ReadInt32LittleEndian(rec);
            if (relationship != (int)NativeMethods.LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore
                || rec.Length < minCoreSize)
                continue;

            byte efficiency = rec[9];
            nuint mask = MemoryMarshal.Read<nuint>(rec[maskOffset..]);
            ushort group = BinaryPrimitives.ReadUInt16LittleEndian(rec[groupOffset..]);
            int idx = LogicalIndex(group, mask);
            if (efficiency > bestEff || (efficiency == bestEff && idx < bestIndex))
            {
                bestEff = efficiency;
                bestIndex = idx;
            }
        }

        return bestIndex;
    }

    private const int CacheInstruction = 1;

    private static (long l1, long l2, long l3, long totalL3) QueryCaches()
    {
        byte[]? buf = QueryRelation(NativeMethods.LOGICAL_PROCESSOR_RELATIONSHIP.RelationCache);
        if (buf is null)
            return (0, 0, 0, 0);

        long l1 = 0, l2 = 0, l3 = 0, totalL3 = 0;
        ReadOnlySpan<byte> buffer = buf;
        int offset = 0;
        while (offset + 8 <= buffer.Length)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset + 4));
            if (size < 8 || offset + (int)size > buffer.Length)
                break;
            ReadOnlySpan<byte> rec = buffer.Slice(offset, (int)size);
            offset += (int)size;

            if (rec.Length < 20)
                continue;
            int relationship = BinaryPrimitives.ReadInt32LittleEndian(rec);
            if (relationship != (int)NativeMethods.LOGICAL_PROCESSOR_RELATIONSHIP.RelationCache)
                continue;

            byte level = rec[8];
            uint cacheSize = BinaryPrimitives.ReadUInt32LittleEndian(rec[12..]);
            int type = BinaryPrimitives.ReadInt32LittleEndian(rec[16..]);
            if (cacheSize == 0 || type == CacheInstruction)
                continue;

            if (level == 1 && cacheSize > l1) l1 = cacheSize;
            else if (level == 2 && cacheSize > l2) l2 = cacheSize;
            else if (level == 3)
            {
                if (cacheSize > l3) l3 = cacheSize;
                totalL3 += cacheSize;
            }
        }

        return (l1, l2, l3, totalL3);
    }

    private static (long l2, long l3, long totalL3) QueryCacheFromWmi(long existingL2)
    {
        long l2 = existingL2, l3 = 0, totalL3 = 0;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT L2CacheSize, L3CacheSize FROM Win32_Processor");
            foreach (var obj in searcher.Get())
            {
                long l2b = Convert.ToInt64(obj["L2CacheSize"]) * 1024;
                long l3b = Convert.ToInt64(obj["L3CacheSize"]) * 1024;
                if (l2b > l2) l2 = l2b;
                if (l3b <= 0) continue;
                if (l3b > l3) l3 = l3b;
                totalL3 += l3b;
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException) { }

        return (l2, l3, totalL3);
    }

    private static byte[]? QueryRelation(NativeMethods.LOGICAL_PROCESSOR_RELATIONSHIP relation)
    {
        uint len = 0;
        NativeMethods.GetLogicalProcessorInformationEx(relation, [], ref len);
        if (len == 0)
            return null;

        var buf = new byte[len];
        uint len2 = len;
        if (!NativeMethods.GetLogicalProcessorInformationEx(relation, buf, ref len2))
            return null;
        return buf;
    }

    private static int LogicalIndex(ushort group, nuint mask)
    {
        ulong m = mask;
        int bit = m == 0 ? 0 : BitOperations.TrailingZeroCount(m);
        int index = bit;
        for (ushort g = 0; g < group; g++)
            index += (int)NativeMethods.GetActiveProcessorCount(g);
        return index;
    }
}
