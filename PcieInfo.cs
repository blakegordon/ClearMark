using System.Runtime.InteropServices;

namespace ClearMark;

/// <summary>
/// PCIe link from SetupAPI current/max speed and current width (Device Manager
/// "PCI current/max link speed" and "PCI current link width"). Max generation
/// is shown in parentheses only when it differs from current.
/// </summary>
internal static class PcieInfo
{
    public readonly record struct Link(int Generation, int Width, int MaxGeneration)
    {
        public override string ToString()
        {
            string gen = MaxGeneration > 0 && MaxGeneration != Generation
                ? $"{Generation}.0 (max {MaxGeneration}.0)"
                : $"{Generation}.0";
            return Width > 0 ? $"PCIe {gen} x{Width}" : $"PCIe {gen}";
        }
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
        IntPtr set = NativeMethods.SetupDiGetClassDevs(
            nint.Zero, "PCI", nint.Zero, NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_ALLCLASSES);
        if (set == nint.Zero || set == unchecked((IntPtr)(-1)))
            return list;

        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new NativeMethods.SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<NativeMethods.SP_DEVINFO_DATA>() };
                if (!NativeMethods.SetupDiEnumDeviceInfo(set, i, ref data))
                    break;
                if (!TryGetUInt32(set, ref data, CurrentLinkSpeed, out uint speed) || speed == 0)
                    continue;
                TryGetUInt32(set, ref data, CurrentLinkWidth, out uint width);
                TryGetUInt32(set, ref data, MaxLinkSpeed, out uint maxSpeed);
                string inst = GetInstanceId(set, in data);
                if (inst.Length == 0)
                    continue;
                string name = GetString(set, ref data, FriendlyName) ?? GetString(set, ref data, DeviceDesc) ?? "";
                list.Add(new PciDevice(inst, name, new Link((int)speed, (int)width, (int)maxSpeed)));
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(set);
        }

        s_cache = list;
        return list;
    }

    private static string GetInstanceId(IntPtr set, in NativeMethods.SP_DEVINFO_DATA data)
    {
        var buf = new char[512];
        if (!NativeMethods.SetupDiGetDeviceInstanceId(set, in data, buf, (uint)buf.Length, out _))
            return "";
        int len = Array.IndexOf(buf, '\0');
        return new string(buf, 0, len < 0 ? buf.Length : len);
    }

    private static string? GetString(IntPtr set, ref NativeMethods.SP_DEVINFO_DATA data, NativeMethods.DEVPROPKEY key)
    {
        var buf = new byte[1024];
        if (!NativeMethods.SetupDiGetDevicePropertyW(set, ref data, ref key, out _, buf, (uint)buf.Length, out uint required, 0)
            || required < 2)
            return null;
        int chars = (int)required / 2;
        return new string(System.Text.Encoding.Unicode.GetChars(buf, 0, (int)required), 0, Math.Max(0, chars - 1)).Trim();
    }

    private static bool TryGetUInt32(IntPtr set, ref NativeMethods.SP_DEVINFO_DATA data, NativeMethods.DEVPROPKEY key, out uint value)
    {
        var buf = new byte[4];
        if (!NativeMethods.SetupDiGetDevicePropertyW(set, ref data, ref key, out _, buf, 4, out uint required, 0)
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
        if (NativeMethods.CM_Locate_DevNodeW(out uint inst, instanceId, 0) != 0)
            return null;
        if (NativeMethods.CM_Get_Parent(out uint parent, inst, 0) != 0)
            return null;
        if (NativeMethods.CM_Get_Device_ID_Size(out uint chars, parent, 0) != 0)
            return null;

        var buf = new char[chars + 1];
        if (NativeMethods.CM_Get_Device_IDW(parent, buf, (uint)buf.Length, 0) != 0)
            return null;
        int len = Array.IndexOf(buf, '\0');
        return new string(buf, 0, len < 0 ? buf.Length : len);
    }

    // DEFINE_PCI_DEVICE_DEVPKEY — pciprop.h  {3AB22E31-8264-4b4e-9AF5-A8D2D8E33E62}
    private static readonly NativeMethods.DEVPROPKEY CurrentLinkSpeed = Key(0x3ab22e31, 0x8264, 0x4b4e, 0x9a, 0xf5, 0xa8, 0xd2, 0xd8, 0xe3, 0x3e, 0x62, 9);
    private static readonly NativeMethods.DEVPROPKEY CurrentLinkWidth = Key(0x3ab22e31, 0x8264, 0x4b4e, 0x9a, 0xf5, 0xa8, 0xd2, 0xd8, 0xe3, 0x3e, 0x62, 10);
    private static readonly NativeMethods.DEVPROPKEY MaxLinkSpeed = Key(0x3ab22e31, 0x8264, 0x4b4e, 0x9a, 0xf5, 0xa8, 0xd2, 0xd8, 0xe3, 0x3e, 0x62, 11);
    // DEVPKEY_Device_FriendlyName / DeviceDesc — devpkey.h
    private static readonly NativeMethods.DEVPROPKEY FriendlyName = Key(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0, 14);
    private static readonly NativeMethods.DEVPROPKEY DeviceDesc = Key(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0, 2);

    private static NativeMethods.DEVPROPKEY Key(uint a, ushort b, ushort c, byte d, byte e, byte f, byte g, byte h, byte i, byte j, byte k, uint pid)
        => new() { fmtid = new Guid(a, b, c, d, e, f, g, h, i, j, k), pid = pid };
}
