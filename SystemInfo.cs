using System.Management;
using System.Runtime.InteropServices;

namespace ClearMark;

internal record HardwareInfo(
    string CpuName, int Cores, int Threads, string Architecture,
    long TotalRamBytes, string RamSpeed,
    string GpuName, string GpuDriver,
    string OsDrive, string OsVersion);

internal static class SystemInfo
{
    public static HardwareInfo Detect()
    {
        string cpu = "Unknown", ramSpeed = "Unknown", gpu = "Unknown", gpuDriver = "Unknown", osDrive = "Unknown";
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
            using var gpuSearcher = new ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController");

            foreach (var obj in gpuSearcher.Get())
            {
                gpu = obj["Name"]?.ToString()?.Trim() ?? gpu;
                gpuDriver = obj["DriverVersion"]?.ToString() ?? gpuDriver;
            }
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
                    osDrive = disk["Model"]?.ToString()?.Trim() ?? osDrive;
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException) { }

        var gcInfo = GC.GetGCMemoryInfo();

        return new HardwareInfo(
            cpu, cores, threads, RuntimeInformation.ProcessArchitecture.ToString(),
            gcInfo.TotalAvailableMemoryBytes, ramSpeed,
            gpu, gpuDriver, osDrive,
            $"{RuntimeInformation.OSDescription}");
    }
}
