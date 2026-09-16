using ComputeSharp;
using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;

namespace ClearMark;

internal record GpuAdapterInfo(string Name, string Driver, string? Pcie, bool IsPrimary);

internal record HardwareInfo(
    string CpuName, int Cores, int Threads, string Architecture,
    long TotalRamBytes, string RamSpeed,
    IReadOnlyList<GpuAdapterInfo> Gpus,
    string OsDrive, string? DiskPcie,
    string OsVersion);

internal static class SystemInfo
{
    public static HardwareInfo Detect()
    {
        string cpu = "Unknown", ramSpeed = "Unknown", osDrive = "Unknown";
        string? diskPnp = null;
        var gpus = new List<GpuAdapterInfo>();
        int cores = Environment.ProcessorCount, threads = Environment.ProcessorCount;

        try
        {
            using var cpuSearcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores, ThreadCount FROM Win32_Processor");
            int sockets = 0;
            int totalCores = 0, totalThreads = 0;
            string cpuName = cpu;

            foreach (var obj in cpuSearcher.Get())
            {
                sockets++;
                cpuName = obj["Name"]?.ToString()?.Trim() ?? cpuName;
                totalCores += Convert.ToInt32(obj["NumberOfCores"]);
                totalThreads += Convert.ToInt32(obj["ThreadCount"]);
            }

            if (sockets > 0)
            {
                cpu = sockets > 1 ? $"{sockets}× {cpuName}" : cpuName;
                cores = totalCores;
                threads = totalThreads;
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException) { }

        try
        {
            using var ramSearcher = new ManagementObjectSearcher("SELECT Speed FROM Win32_PhysicalMemory");

            foreach (var obj in ramSearcher.Get())
            {
                ramSpeed = $"{obj["Speed"]} MHz";
                break;
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException) { }

        try
        {
            string? primaryName = null;
            try { primaryName = GraphicsDevice.GetDefault()?.Name; }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or Win32Exception) { }

            using var gpuSearcher = new ManagementObjectSearcher("SELECT Name, DriverVersion, PNPDeviceID FROM Win32_VideoController");

            foreach (var obj in gpuSearcher.Get())
            {
                string name = obj["Name"]?.ToString()?.Trim() ?? "";
                if (name.Length == 0 || IsVirtualAdapter(name))
                    continue;
                string driver = obj["DriverVersion"]?.ToString() ?? "";
                string? pnp = obj["PNPDeviceID"]?.ToString();
                bool isPrimary = primaryName is not null
                    && (name.Contains(primaryName, StringComparison.OrdinalIgnoreCase)
                        || primaryName.Contains(name, StringComparison.OrdinalIgnoreCase));
                gpus.Add(new GpuAdapterInfo(name, driver, PcieInfo.Format(pnp, name), isPrimary));
            }

            if (gpus.Count > 0 && !gpus.Any(g => g.IsPrimary))
                gpus[0] = gpus[0] with { IsPrimary = true };
            gpus.Sort((a, b) => b.IsPrimary.CompareTo(a.IsPrimary));
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException) { }

        try
        {
            string sysRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            string driveLetter = sysRoot.TrimEnd('\\');  // e.g. "C:"

            // Walk WMI: LogicalDisk → Partition → DiskDrive
            using var partAssoc = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{driveLetter}'}} WHERE AssocClass=Win32_LogicalDiskToPartition");

            foreach (var partition in partAssoc.Get())
            {
                using var diskAssoc = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition");

                foreach (var disk in diskAssoc.Get())
                {
                    osDrive = disk["Model"]?.ToString()?.Trim() ?? osDrive;
                    diskPnp = disk["PNPDeviceID"]?.ToString() ?? diskPnp;
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException) { }

        var gcInfo = GC.GetGCMemoryInfo();

        if (gpus.Count == 0)
            gpus.Add(new GpuAdapterInfo("Unknown", "", null, true));

        return new HardwareInfo(
            cpu, cores, threads, RuntimeInformation.ProcessArchitecture.ToString(),
            gcInfo.TotalAvailableMemoryBytes, ramSpeed,
            gpus,
            osDrive, PcieInfo.Format(diskPnp, osDrive),
            $"{RuntimeInformation.OSDescription}");
    }

    private static bool IsVirtualAdapter(string name)
        => name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Remote Desktop", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase);
}
