using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace ClearMark;

/// <summary>
/// Negotiated PCIe link (generation × width) from live PCI devices (SetupAPI),
/// the same properties Device Manager shows as "PCI current link speed/width".
/// </summary>
[SuppressMessage("Interoperability", "CA1060", Justification = "SetupAPI surface lives with the PCI lookup, not NativeMethods.")]
internal static partial class PcieInfo
{
    public readonly record struct Link(int Generation, int Width)
    {
        public override string ToString() => Width > 0
            ? $"PCIe {Generation}.0 x{Width}"
            : $"PCIe {Generation}.0";
    }

    private readonly record struct PciDevice(string InstanceId, string Name, Link Link);

    public static string? Format(string? pnpDeviceId, string? nameHint = null)
        => Query(pnpDeviceId, nameHint)?.ToString();

    public static Link? Query(string? pnpDeviceId, string? nameHint = null)
    {
        var devices = Snapshot();
        if (devices.Count == 0)
            return null;

        string? id = pnpDeviceId;
        for (int i = 0; i < 12 && !string.IsNullOrEmpty(id); i++)
        {
            foreach (var d in devices)
            {
                if (d.InstanceId.Equals(id, StringComparison.OrdinalIgnoreCase))
                    return d.Link;
            }
            id = ParentInstanceId(id);
        }

        if (string.IsNullOrWhiteSpace(nameHint) || nameHint is "Unknown")
            return null;

        PciDevice? best = null;
        foreach (var d in devices)
        {
            if (d.Name.Length == 0)
                continue;
            if (nameHint.Contains(d.Name, StringComparison.OrdinalIgnoreCase)
                || d.Name.Contains(nameHint, StringComparison.OrdinalIgnoreCase))
            {
                if (best is null || d.Name.Length > best.Value.Name.Length)
                    best = d;
            }
        }
        return best?.Link;
    }

    private static List<PciDevice>? s_cache;

    private static List<PciDevice> Snapshot()
    {
        if (s_cache is not null)
            return s_cache;

        var list = new List<PciDevice>();
        IntPtr set = SetupDiGetClassDevs(nint.Zero, "PCI", nint.Zero, DigcfPresent | DigcfAllClasses);
        if (set == nint.Zero || set == unchecked((IntPtr)(-1)))
            return list;

        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(set, i, ref data))
                    break;
                if (!TryGetUInt32(set, ref data, CurrentLinkSpeed, out uint speed) || speed == 0)
                    continue;
                TryGetUInt32(set, ref data, CurrentLinkWidth, out uint width);
                string inst = GetInstanceId(set, in data);
                if (inst.Length == 0)
                    continue;
                string name = GetString(set, ref data, FriendlyName) ?? GetString(set, ref data, DeviceDesc) ?? "";
                list.Add(new PciDevice(inst, name, new Link((int)speed, (int)width)));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        s_cache = list;
        return list;
    }

    private static string GetInstanceId(IntPtr set, in SP_DEVINFO_DATA data)
    {
        var buf = new char[512];
        if (!SetupDiGetDeviceInstanceId(set, in data, buf, (uint)buf.Length, out _))
            return "";
        int len = Array.IndexOf(buf, '\0');
        return new string(buf, 0, len < 0 ? buf.Length : len);
    }

    private static string? GetString(IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
    {
        var buf = new byte[1024];
        if (!SetupDiGetDevicePropertyW(set, ref data, ref key, out _, buf, (uint)buf.Length, out uint required, 0)
            || required < 2)
            return null;
        int chars = (int)required / 2;
        return new string(System.Text.Encoding.Unicode.GetChars(buf, 0, (int)required), 0, Math.Max(0, chars - 1)).Trim();
    }

    private static bool TryGetUInt32(IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key, out uint value)
    {
        var buf = new byte[4];
        if (!SetupDiGetDevicePropertyW(set, ref data, ref key, out _, buf, 4, out uint required, 0)
            || required < 4)
        {
            value = 0;
            return false;
        }
        value = BitConverter.ToUInt32(buf, 0);
        return true;
    }

    private static string? ParentInstanceId(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint inst, instanceId, 0) != 0)
            return null;
        if (CM_Get_Parent(out uint parent, inst, 0) != 0)
            return null;
        if (CM_Get_Device_ID_Size(out uint chars, parent, 0) != 0)
            return null;

        var buf = new char[chars + 1];
        if (CM_Get_Device_IDW(parent, buf, (uint)buf.Length, 0) != 0)
            return null;
        int len = Array.IndexOf(buf, '\0');
        return new string(buf, 0, len < 0 ? buf.Length : len);
    }

    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;

    // DEFINE_PCI_DEVICE_DEVPKEY — pciprop.h  {3AB22E31-8264-4b4e-9AF5-A8D2D8E33E62}
    private static readonly DEVPROPKEY CurrentLinkSpeed = Key(0x3ab22e31, 0x8264, 0x4b4e, 0x9a, 0xf5, 0xa8, 0xd2, 0xd8, 0xe3, 0x3e, 0x62, 9);
    private static readonly DEVPROPKEY CurrentLinkWidth = Key(0x3ab22e31, 0x8264, 0x4b4e, 0x9a, 0xf5, 0xa8, 0xd2, 0xd8, 0xe3, 0x3e, 0x62, 10);
    // DEVPKEY_Device_FriendlyName / DeviceDesc — devpkey.h
    private static readonly DEVPROPKEY FriendlyName = Key(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0, 14);
    private static readonly DEVPROPKEY DeviceDesc = Key(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0, 2);

    private static DEVPROPKEY Key(uint a, ushort b, ushort c, byte d, byte e, byte f, byte g, byte h, byte i, byte j, byte k, uint pid)
        => new() { fmtid = new Guid(a, b, c, d, e, f, g, h, i, j, k), pid = pid };

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nuint Reserved;
    }

    [LibraryImport("setupapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16, EntryPoint = "SetupDiGetClassDevsW")]
    private static partial IntPtr SetupDiGetClassDevs(nint classGuid, string? enumerator, nint hwndParent, uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16, EntryPoint = "SetupDiGetDeviceInstanceIdW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet, in SP_DEVINFO_DATA deviceInfoData, [Out] char[] deviceInstanceId, uint deviceInstanceIdSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDevicePropertyW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDevicePropertyW(
        IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref DEVPROPKEY propertyKey,
        out uint propertyType, byte[] propertyBuffer, uint propertyBufferSize, out uint requiredSize, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_Parent(out uint parentDevInst, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_Device_ID_Size(out uint length, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_IDW(uint devInst, [Out] char[] buffer, uint bufferLen, uint flags);
}
