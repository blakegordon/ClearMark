using System.Runtime.InteropServices;

namespace ClearMark;

/// <summary>Win32 interop that has no managed equivalent (processor groups and topology).</summary>
internal static partial class NativeMethods
{
    internal const ushort ALL_PROCESSOR_GROUPS = 0xFFFF;

    [StructLayout(LayoutKind.Sequential)]
    internal struct GROUP_AFFINITY
    {
        public UIntPtr Mask;
        public ushort Group;
        public ushort Reserved0;
        public ushort Reserved1;
        public ushort Reserved2;
    }

    [LibraryImport("kernel32.dll")]
    internal static partial IntPtr GetCurrentThread();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetThreadGroupAffinity(
        IntPtr hThread,
        in GROUP_AFFINITY groupAffinity,
        out GROUP_AFFINITY previousGroupAffinity);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetActiveProcessorCount(ushort groupNumber);

    [LibraryImport("kernel32.dll")]
    internal static partial ushort GetActiveProcessorGroupCount();

    internal enum LOGICAL_PROCESSOR_RELATIONSHIP
    {
        RelationProcessorCore = 0,
        RelationCache = 2,
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetLogicalProcessorInformationEx(
        LOGICAL_PROCESSOR_RELATIONSHIP relationshipType,
        nint buffer,
        ref uint returnedLength);
}
