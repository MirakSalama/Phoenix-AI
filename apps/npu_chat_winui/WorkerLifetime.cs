using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NpuChat;

internal static class WorkerLifetime
{
    // Assign the app before starting Python so the venv launcher, its Python
    // child, and any compilers or audio processes inherit the same job.
    internal static SafeFileHandle? BindToApp()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
        const int extendedLimitInformation = 9;
        if (!job.IsInvalid &&
            SetInformationJobObject(job, extendedLimitInformation, ref limits, Marshal.SizeOf<ExtendedLimits>()) &&
            AssignProcessToJobObject(job, GetCurrentProcess()))
            return job;

        // Some hosts restrict nested jobs. The window still stops each worker
        // tree explicitly when it closes.
        job.Dispose();
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTime, PerJobUserTime;
        public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong Reads, Writes, OtherOperations, ReadBytes, WrittenBytes, OtherBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int type,
        ref ExtendedLimits information, int size);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
