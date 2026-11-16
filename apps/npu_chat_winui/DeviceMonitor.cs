using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace NpuChat;

internal sealed record DeviceReading(double? Utilization = null, double? AppUtilization = null, double? ClockMhz = null,
    double? MemoryUsed = null, double? MemoryCapacity = null, double? AppMemory = null,
    double? SharedMemoryUsed = null, double? SharedMemoryCapacity = null, double? AppSharedMemory = null);

internal sealed record DeviceSample(DeviceReading Cpu, IReadOnlyDictionary<string, DeviceReading> Accelerators);

internal sealed partial class DeviceMonitor : IDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<int, (long Start, double Cpu)> prior = [];
    internal const string NpuKey = "npu";
    private readonly Dictionary<AdapterLuid, AdapterInfo> adapters = [];
    private readonly Dictionary<string, IntPtr> counters = [];
    private long last;
    private IntPtr query;
    private bool triedCounters, disposed;

    internal DeviceSample Read()
    {
        lock (sync)
        {
            if (disposed) return new(new(), new Dictionary<string, DeviceReading>());
            InitializeCounters();
            var pids = Descendants();
            double? appCpu = ReadCpu(pids);
            bool sampled = query != IntPtr.Zero && PdhCollectQueryData(query) == 0;
            return new(ReadCpuReading(pids, appCpu, sampled), ReadAccelerators(pids, sampled));
        }
    }

    private double? ReadCpu(HashSet<int> pids)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = last == 0 ? 0 : Stopwatch.GetElapsedTime(last, now).TotalSeconds;
        last = now;
        double seconds = 0;
        foreach (int id in pids)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                long start = process.StartTime.Ticks;
                double cpu = process.TotalProcessorTime.TotalSeconds;
                if (prior.TryGetValue(id, out var before) && start == before.Start)
                    seconds += Math.Max(0, cpu - before.Cpu);
                prior[id] = (start, cpu);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        foreach (int stale in prior.Keys.Where(id => !pids.Contains(id)).ToArray()) prior.Remove(stale);
        return elapsed > 0 ? Math.Clamp(100 * seconds / (elapsed * Environment.ProcessorCount), 0, 100) : null;
    }

    [GeneratedRegex(@"^(?:pid_(\d+)_)?luid_0x([0-9a-f]+)_0x([0-9a-f]+)_(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex CounterName();

    private void InitializeCounters()
    {
        if (triedCounters) return;
        triedCounters = true;
        ReadAdapters();
        if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0) return;
        foreach (var (key, path) in new[]
        {
            ("engines", @"\GPU Engine(*)\Utilization Percentage"),
            ("shared", @"\GPU Adapter Memory(*)\Shared Usage"),
            ("dedicated", @"\GPU Adapter Memory(*)\Dedicated Usage"),
            ("appShared", @"\GPU Process Memory(*)\Shared Usage"),
            ("appDedicated", @"\GPU Process Memory(*)\Dedicated Usage"),
            ("cpu", @"\Processor Information(_Total)\% Processor Time"),
            ("clock", @"\Processor Information(_Total)\Actual Frequency"),
            ("clockFallback", @"\Processor Information(_Total)\Processor Frequency"),
            ("cpuPrivate", @"\Process V2(*)\Working Set - Private")
        })
        {
            if (PdhAddEnglishCounterW(query, path, IntPtr.Zero, out var handle) == 0)
                counters.Add(key, handle);
        }
    }

    private DeviceReading ReadCpuReading(HashSet<int> pids, double? appCpu, bool sampled)
    {
        double? used = null, total = null, privateRam = null;
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref memory))
        {
            total = memory.TotalPhysical;
            used = memory.TotalPhysical - Math.Min(memory.AvailablePhysical, memory.TotalPhysical);
        }
        if (sampled && ReadArray("cpuPrivate") is { } processes)
        {
            privateRam = 0;
            foreach (var (name, bytes) in processes)
            {
                int separator = name.LastIndexOf(':');
                if (separator >= 0 && int.TryParse(name.AsSpan(separator + 1), out int pid) && pids.Contains(pid))
                    privateRam += bytes;
            }
        }
        return new(sampled ? ReadValue("cpu") : null, appCpu,
            sampled ? ReadValue("clock") ?? ReadValue("clockFallback") : null, used, total, privateRam);
    }

    private Dictionary<string, DeviceReading> ReadAccelerators(HashSet<int> pids, bool sampled)
    {
        var engineRows = sampled ? ReadArray("engines") : null;
        var engines = new Dictionary<(AdapterLuid Adapter, string Engine), (double Total, double App)>();
        foreach (var (instance, value) in engineRows ?? [])
        {
            if (!TryCounter(instance, out var luid, out var match) || !adapters.ContainsKey(luid)) continue;
            var key = (luid, match.Groups[4].Value);
            var sums = engines.GetValueOrDefault(key);
            sums.Total += value;
            if (int.TryParse(match.Groups[1].Value, out int pid) && pids.Contains(pid)) sums.App += value;
            engines[key] = sums;
        }
        var shared = sampled ? ReadMemory("shared") : [];
        var dedicated = sampled ? ReadMemory("dedicated") : [];
        var appShared = sampled ? ReadMemory("appShared", pids) : [];
        var appDedicated = sampled ? ReadMemory("appDedicated", pids) : [];
        var result = new Dictionary<string, DeviceReading>(StringComparer.OrdinalIgnoreCase);
        foreach (var (luid, adapter) in adapters)
        {
            var usage = engines.Where(row => row.Key.Adapter == luid).Select(row => row.Value).ToArray();
            double? busy = usage.Length > 0 ? Math.Clamp(usage.Max(row => row.Total), 0, 100) : ReadEngineTime(adapter);
            double? app = usage.Length > 0 ? Math.Clamp(usage.Max(row => row.App), 0, 100) : null;
            double? sharedUsed = shared.GetValueOrDefault(luid) ?? ReadResidentMemory(adapter, shared: true);
            double? dedicatedUsed = dedicated.GetValueOrDefault(luid) ?? ReadResidentMemory(adapter, shared: false);
            double? clock = ReadEngineClock(adapter);
            if (clock is null && (busy is > 0 || dedicatedUsed is > 0 || sharedUsed is > 1048576))
                clock = ReadWddmClock(luid);
            result[adapter.Key] = new(busy, app, clock, dedicatedUsed, adapter.DedicatedCapacity,
                appDedicated.GetValueOrDefault(luid), sharedUsed, adapter.SharedCapacity, appShared.GetValueOrDefault(luid));
        }
        return result;
    }

    private Dictionary<AdapterLuid, double?> ReadMemory(string key, HashSet<int>? pids = null)
    {
        var result = new Dictionary<AdapterLuid, double?>();
        if (ReadArray(key) is not { } rows) return result;
        foreach (var (instance, bytes) in rows)
        {
            if (!TryCounter(instance, out var luid, out var match) || !adapters.ContainsKey(luid)) continue;
            result.TryAdd(luid, 0);
            if (pids is null || int.TryParse(match.Groups[1].Value, out int pid) && pids.Contains(pid))
                result[luid] += bytes;
        }
        return result;
    }

    private static bool TryCounter(string instance, out AdapterLuid luid, out Match match)
    {
        luid = default;
        match = CounterName().Match(instance);
        if (!match.Success ||
            !uint.TryParse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint high) ||
            !uint.TryParse(match.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint low)) return false;
        luid = new(low, high);
        return true;
    }

    private double? ReadValue(string key) =>
        counters.TryGetValue(key, out var counter) &&
        PdhGetFormattedCounterValue(counter, 0x200, IntPtr.Zero, out var value) == 0 &&
        value.Status <= 1 && double.IsFinite(value.Value) ? Math.Max(0, value.Value) : null;

    private List<(string Name, double Value)>? ReadArray(string key)
    {
        if (!counters.TryGetValue(key, out var counter)) return null;
        const uint moreData = 0x800007D2;
        const uint formatDouble = 0x200;
        uint size = 0;
        if (PdhGetFormattedCounterArrayW(counter, formatDouble, ref size, out _, IntPtr.Zero) != moreData ||
            size == 0 || size > 16 * 1024 * 1024) return null;
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, formatDouble, ref size, out uint count, buffer) != 0)
                return null;
            var result = new List<(string Name, double Value)>((int)count);
            int stride = Marshal.SizeOf<CounterItem>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(buffer + i * stride);
                if (item.Status > 1 || !double.IsFinite(item.Value)) continue;
                result.Add((Marshal.PtrToStringUni(item.Name) ?? "", Math.Max(0, item.Value)));
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static HashSet<int> Descendants()
    {
        var result = new HashSet<int> { Environment.ProcessId };
        var children = new Dictionary<int, List<int>>();
        IntPtr snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) return result;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (Process32FirstW(snapshot, ref entry))
            {
                do
                {
                    int parent = (int)entry.Parent;
                    if (!children.TryGetValue(parent, out var list)) children[parent] = list = [];
                    list.Add((int)entry.Id);
                } while (Process32NextW(snapshot, ref entry));
            }
        }
        finally { CloseHandle(snapshot); }
        var pending = new Queue<int>(result);
        while (pending.TryDequeue(out int parent))
            if (children.TryGetValue(parent, out var list))
                foreach (int pid in list)
                    if (result.Add(pid)) pending.Enqueue(pid);
        return result;
    }

    internal static string AdapterKey(string name) =>
        new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private void ReadAdapters()
    {
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        if (CreateDXGIFactory1(ref iid, out IntPtr factory) >= 0)
        {
            try
            {
                // IDXGIFactory1::EnumAdapters1 and IDXGIAdapter1::GetDesc1.
                var enumerate = ComMethod<EnumAdapter>(factory, 12);
                for (uint index = 0; enumerate(factory, index, out IntPtr adapter) >= 0; index++)
                {
                    try
                    {
                        if (ComMethod<GetAdapterDesc>(adapter, 10)(adapter, out var desc) >= 0 && (desc.Flags & 2) == 0)
                            adapters[new(desc.Low, desc.High)] = new(AdapterKey(desc.Description),
                                desc.Shared.ToUInt64(), desc.Dedicated.ToUInt64() + desc.DedicatedSystem.ToUInt64());
                    }
                    finally { Marshal.Release(adapter); }
                }
            }
            finally { Marshal.Release(factory); }
        }
        AttachDxCoreAdapters();
    }

    private static T ComMethod<T>(IntPtr instance, int index) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), index * IntPtr.Size));

    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            if (query != IntPtr.Zero) PdhCloseQuery(query);
            query = IntPtr.Zero;
            foreach (var adapter in adapters.Values)
                if (adapter.Native != IntPtr.Zero) Marshal.Release(adapter.Native);
            adapters.Clear();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem { internal IntPtr Name; internal uint Status; internal double Value; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue { internal uint Status; internal double Value; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        internal uint Length, Load;
        internal ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, ExtendedVirtual;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Description;
        internal uint Vendor, Device, Subsystem, Revision;
        internal UIntPtr Dedicated, DedicatedSystem, Shared;
        internal uint Low, High, Flags;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        internal uint Size, Usage, Id;
        internal UIntPtr Heap;
        internal uint Module, Threads, Parent;
        internal int Priority;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Exe;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapter(IntPtr instance, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAdapterDesc(IntPtr instance, out AdapterDesc desc);
    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
    [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll")] private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string? source, IntPtr data, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr data, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format,
        ref uint size, out uint count, IntPtr items);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format,
        IntPtr type, out CounterValue value);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}
