using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NpuChat;

internal sealed partial class DeviceMonitor
{
    private static readonly Guid CoreFactoryId = new("78ee5945-c36e-4b13-a669-005dd11c0f06");
    private static readonly Guid CoreListId = new("526c7776-40e9-459b-b711-f32ad76dfc28");
    private static readonly Guid CoreAdapterId = new("f0db4c7f-fe5a-42a2-bd62-f2a6cf6fc83e");
    private static readonly Guid CoreAdapter1Id = new("a0783366-cfa3-43be-9d79-55b2da97c63c");
    private static readonly Guid NpuAttribute = new("d46140c4-add7-451b-9e56-06fe8c3b58ed");

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct AdapterLuid(uint Low, uint High);

    private sealed class AdapterInfo(string key, double? sharedCapacity, double? dedicatedCapacity)
    {
        internal string Key { get; } = key;
        internal double? SharedCapacity { get; } = sharedCapacity;
        internal double? DedicatedCapacity { get; } = dedicatedCapacity;
        internal IntPtr Native { get; set; }
        internal uint EngineCount { get; set; } = 1;
        internal Dictionary<uint, (long Timestamp, ulong Microseconds)> EngineTimes { get; } = [];
    }

    private void AttachDxCoreAdapters()
    {
        IntPtr factory;
        try
        {
            if (DXCoreCreateAdapterFactory(in CoreFactoryId, out factory) < 0) return;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return; }
        try
        {
            var byLuid = ComMethod<GetAdapterByLuid>(factory, 4);
            foreach (var (luid, adapter) in adapters)
                if (byLuid(factory, in luid, in CoreAdapterId, out var native) >= 0)
                    Attach(adapter, native);

            var createList = ComMethod<CreateAdapterList>(factory, 3);
            if (createList(factory, 1, in NpuAttribute, in CoreListId, out var list) < 0) return;
            try
            {
                var get = ComMethod<GetCoreAdapter>(list, 3);
                uint count = ComMethod<GetCoreAdapterCount>(list, 4)(list);
                for (uint index = 0; index < count; index++)
                {
                    if (get(list, index, in CoreAdapterId, out var native) < 0) continue;
                    if (!ReadCoreProperty(native, 0, out var identifier))
                    {
                        Marshal.Release(native);
                        continue;
                    }
                    var luid = new AdapterLuid((uint)identifier, (uint)(identifier >> 32));
                    var npu = new AdapterInfo(NpuKey, CoreMemoryCapacity(native, 9), CoreMemoryCapacity(native, 7));
                    if (adapters.Remove(luid, out var existing) && existing.Native != IntPtr.Zero)
                        Marshal.Release(existing.Native);
                    Attach(npu, native);
                    adapters.Add(luid, npu);
                    break;
                }
            }
            finally { Marshal.Release(list); }
        }
        finally { Marshal.Release(factory); }
    }

    private static void Attach(AdapterInfo adapter, IntPtr native)
    {
        adapter.Native = native;
        var iid = CoreAdapter1Id;
        if (Marshal.QueryInterface(native, in iid, out var version1) < 0) return;
        try
        {
            // IDXCoreAdapter1::GetPropertyWithInput(AdapterEngineCount, physical adapter zero).
            uint physical = 0;
            if (ComMethod<GetEngineCount>(version1, 13)(version1, 16, 4, in physical, 4, out uint count) >= 0)
                adapter.EngineCount = Math.Clamp(count, 1u, 128u);
        }
        finally { Marshal.Release(version1); }
    }

    private static bool ReadCoreProperty(IntPtr adapter, uint property, out ulong value) =>
        ComMethod<GetCoreProperty>(adapter, 6)(adapter, property, 8, out value) >= 0;

    private static double? CoreMemoryCapacity(IntPtr adapter, uint property) =>
        ReadCoreProperty(adapter, property, out ulong value) ? value : null;

    private static double? ReadResidentMemory(AdapterInfo adapter, bool shared)
    {
        var native = adapter.Native;
        if (native == IntPtr.Zero) return null;
        var index = new EngineIndex(0, shared ? 1u : 0u);
        return ComMethod<QueryMemory>(native, 9)(native, 2, 8, in index, 16, out var memory) >= 0
            ? memory.Resident : null;
    }

    private static double? ReadEngineClock(AdapterInfo adapter)
    {
        var native = adapter.Native;
        if (native == IntPtr.Zero) return null;
        var index = new EngineIndex(0, 0);
        return ComMethod<QueryFrequency>(native, 9)(native, 9, 8, in index, 24, out var clock) >= 0 &&
            clock.Current > 0 ? clock.Current / 1_000_000.0 : null;
    }

    private static double? ReadWddmClock(AdapterLuid luid)
    {
        var open = new OpenAdapter { Luid = luid };
        if (D3DKMTOpenAdapterFromLuid(ref open) != 0) return null;
        try
        {
            uint size = (uint)Marshal.SizeOf<NodePerformance>();
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                Marshal.StructureToPtr(default(NodePerformance), buffer, false);
                const uint nodePerformanceType = 61; // KMTQAITYPE_NODEPERFDATA
                var query = new AdapterQuery(open.Handle, nodePerformanceType, buffer, size);
                if (D3DKMTQueryAdapterInfo(in query) != 0) return null;
                ulong frequency = Marshal.PtrToStructure<NodePerformance>(buffer).Frequency;
                return frequency > 0 ? frequency / 1_000_000.0 : null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { D3DKMTCloseAdapter(in open.Handle); }
    }

    private static double? ReadEngineTime(AdapterInfo adapter)
    {
        var native = adapter.Native;
        if (native == IntPtr.Zero) return null;
        long timestamp = Stopwatch.GetTimestamp();
        double? maximum = null;
        var query = ComMethod<QueryEngineTime>(native, 9);
        for (uint node = 0; node < adapter.EngineCount; node++)
        {
            var index = new EngineIndex(0, node);
            if (query(native, 4, 8, in index, 8, out var time) < 0 &&
                query(native, 4, 8, in index, 16, out time) < 0) continue;
            if (adapter.EngineTimes.TryGetValue(node, out var previous) && time.Microseconds >= previous.Microseconds)
            {
                double elapsed = Stopwatch.GetElapsedTime(previous.Timestamp, timestamp).TotalMicroseconds;
                if (elapsed > 0)
                    maximum = Math.Max(maximum ?? 0, Math.Clamp(100 * (time.Microseconds - previous.Microseconds) / elapsed, 0, 100));
            }
            adapter.EngineTimes[node] = (timestamp, time.Microseconds);
        }
        return maximum;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct EngineIndex(uint Physical, uint Node);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CoreMemory(ulong Committed, ulong Resident);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CoreFrequency(ulong Current, ulong Maximum, ulong MaxOverclocked);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CoreEngineTime(ulong Microseconds, ulong Status);
    [StructLayout(LayoutKind.Sequential)]
    private struct OpenAdapter { internal AdapterLuid Luid; internal uint Handle; }
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct AdapterQuery(uint Handle, uint Type, IntPtr Data, uint Size);
    [StructLayout(LayoutKind.Sequential)]
    private struct NodePerformance
    {
        internal uint NodeOrdinal, PhysicalAdapterIndex;
        internal ulong Frequency, MaxFrequency, MaxFrequencyOverclocked;
        internal uint Voltage, MaxVoltage, MaxVoltageOverclocked;
        internal ulong MaxTransitionLatency;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateAdapterList(IntPtr factory, uint attributeCount, in Guid attribute, in Guid iid, out IntPtr list);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAdapterByLuid(IntPtr factory, in AdapterLuid luid, in Guid iid, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCoreAdapter(IntPtr list, uint index, in Guid iid, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GetCoreAdapterCount(IntPtr list);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCoreProperty(IntPtr adapter, uint property, nuint size, out ulong value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetEngineCount(IntPtr adapter, uint property, nuint inputSize, in uint physical,
        nuint outputSize, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryMemory(IntPtr adapter, uint state, nuint inputSize, in EngineIndex index,
        nuint outputSize, out CoreMemory memory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryFrequency(IntPtr adapter, uint state, nuint inputSize, in EngineIndex index,
        nuint outputSize, out CoreFrequency frequency);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryEngineTime(IntPtr adapter, uint state, nuint inputSize, in EngineIndex index,
        nuint outputSize, out CoreEngineTime runningTime);
    [DllImport("dxcore.dll")]
    private static extern int DXCoreCreateAdapterFactory(in Guid iid, out IntPtr factory);
    [DllImport("gdi32.dll")]
    private static extern int D3DKMTOpenAdapterFromLuid(ref OpenAdapter open);
    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(in AdapterQuery query);
    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(in uint handle);
}
