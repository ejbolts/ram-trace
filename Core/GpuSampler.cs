using System.Runtime.InteropServices;

namespace RamTrace.Core;

public record GpuProcess(long Dedicated, long Shared, long Activity);
public record GpuDevice(string Name, long Capacity, long Dedicated, long Shared, long Activity);
public record GpuReading(bool MemoryAvailable, bool ActivityAvailable, long Dedicated, long Shared, long Activity, Dictionary<int, GpuProcess> Processes, List<GpuDevice> Devices);

public sealed class GpuSampler : IDisposable
{
    private IntPtr query;
    private IntPtr processDedicated, processShared, adapterDedicated, adapterShared, engines;
    private readonly Dictionary<string, (string Name, long Capacity)> adapters;
    private bool warmed;
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, out uint count, IntPtr items);
    [StructLayout(LayoutKind.Explicit)] private struct CounterItem
    {
        [FieldOffset(0)] public IntPtr Name;
        [FieldOffset(8)] public uint Status;
        [FieldOffset(16)] public double Value;
    }
    public GpuSampler()
    {
        adapters = DxgiAdapters.Read();
        if (PdhOpenQuery(null, IntPtr.Zero, out query) != 0) return;
        Add(@"\GPU Process Memory(*)\Dedicated Usage", out processDedicated);
        Add(@"\GPU Process Memory(*)\Shared Usage", out processShared);
        Add(@"\GPU Adapter Memory(*)\Dedicated Usage", out adapterDedicated);
        Add(@"\GPU Adapter Memory(*)\Shared Usage", out adapterShared);
        Add(@"\GPU Engine(*)\Utilization Percentage", out engines);
    }
    private void Add(string path, out IntPtr counter) { if (PdhAddEnglishCounter(query, path, IntPtr.Zero, out counter) != 0) counter = IntPtr.Zero; }
    private static Dictionary<string, double>? Read(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;
        uint bytes = 0;
        var rc = PdhGetFormattedCounterArray(counter, 0x200 | 0x8000, ref bytes, out _, IntPtr.Zero);
        if (rc != 0x800007D2 || bytes == 0) return null;
        var ptr = Marshal.AllocHGlobal((int)bytes);
        try
        {
            if (PdhGetFormattedCounterArray(counter, 0x200 | 0x8000, ref bytes, out var count, ptr) != 0) return null;
            var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(ptr + i * Marshal.SizeOf<CounterItem>());
                if (item.Status is 0 or 1 && double.IsFinite(item.Value)) values[Marshal.PtrToStringUni(item.Name)!] = Math.Max(0, item.Value);
            }
            return values;
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }
    private static int Pid(string name) => name.StartsWith("pid_") && int.TryParse(name.AsSpan(4, name.IndexOf('_', 4) - 4), out var pid) ? pid : -1;
    private static string AdapterKey(string name)
    {
        var start = name.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        var end = name.IndexOf("_eng_", StringComparison.OrdinalIgnoreCase);
        return start < 0 ? name : name[start..(end < 0 ? name.Length : end)];
    }
    public GpuReading Capture()
    {
        if (query == IntPtr.Zero || PdhCollectQueryData(query) != 0) return new(false, false, 0, 0, 0, new(), new());
        var pd = Read(processDedicated); var ps = Read(processShared); var ad = Read(adapterDedicated); var ash = Read(adapterShared); var en = Read(engines);
        bool available = pd != null && ps != null && ad?.Count > 0 && ash?.Count > 0;
        bool activity = warmed && en != null && en.Count > 0;
        warmed = true;
        var pids = new Dictionary<int, GpuProcess>();
        foreach (var name in (pd?.Keys ?? Enumerable.Empty<string>()).Union(ps?.Keys ?? Enumerable.Empty<string>()))
        {
            var pid = Pid(name); if (pid < 0) continue;
            var prev = pids.GetValueOrDefault(pid) ?? new(0, 0, 0);
            pids[pid] = prev with { Dedicated = prev.Dedicated + (long)(pd?.GetValueOrDefault(name) ?? 0), Shared = prev.Shared + (long)(ps?.GetValueOrDefault(name) ?? 0) };
        }
        var engineTotals = new Dictionary<string, double>();
        if (activity && en != null)
        {
            foreach (var item in en)
            {
                var pid = Pid(item.Key); if (pid < 0) continue;
                var prev = pids.GetValueOrDefault(pid) ?? new(0, 0, 0);
                pids[pid] = prev with { Activity = Math.Max(prev.Activity, (long)Math.Clamp(item.Value * 100, 0, 10000)) };
                var key = item.Key[(item.Key.IndexOf('_', 4) + 1)..];
                engineTotals[key] = engineTotals.GetValueOrDefault(key) + item.Value;
            }
        }
        var devices = new List<GpuDevice>();
        foreach (var key in (ad?.Keys ?? Enumerable.Empty<string>()).Union(ash?.Keys ?? Enumerable.Empty<string>()))
        {
            var info = adapters.GetValueOrDefault(key, (key, 0));
            var load = engineTotals.Where(e => AdapterKey(e.Key) == key).Select(e => e.Value).DefaultIfEmpty().Max();
            devices.Add(new(info.Item1, info.Item2, (long)(ad?.GetValueOrDefault(key) ?? 0), (long)(ash?.GetValueOrDefault(key) ?? 0), (long)Math.Clamp(load * 100, 0, 10000)));
        }
        return new(available, activity, devices.Sum(d => d.Dedicated), devices.Sum(d => d.Shared), devices.Select(d => d.Activity).DefaultIfEmpty().Max(), pids, devices);
    }
    public void Dispose() { if (query != IntPtr.Zero) { PdhCloseQuery(query); query = IntPtr.Zero; } }
}

internal static class DxgiAdapters
{
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumAdapter(IntPtr self, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDescription(IntPtr self, out Description description);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Description
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Name;
        public uint Vendor, Device, Subsystem, Revision;
        public UIntPtr DedicatedVideo, DedicatedSystem, SharedSystem;
        public uint LuidLow; public int LuidHigh; public uint Flags;
    }
    public static Dictionary<string, (string Name, long Capacity)> Read()
    {
        var result = new Dictionary<string, (string, long)>(StringComparer.OrdinalIgnoreCase);
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        if (CreateDXGIFactory1(ref iid, out var factory) < 0) return result;
        try
        {
            // IDXGIFactory1::EnumAdapters1 and IDXGIAdapter1::GetDesc1 vtable slots.
            var enumerate = Marshal.GetDelegateForFunctionPointer<EnumAdapter>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), 12 * IntPtr.Size));
            for (uint index = 0; enumerate(factory, index, out var adapter) == 0; index++)
            {
                try
                {
                    var describe = Marshal.GetDelegateForFunctionPointer<GetDescription>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(adapter), 10 * IntPtr.Size));
                    if (describe(adapter, out var d) == 0)
                    {
                        string key = $"luid_0x{d.LuidHigh:x8}_0x{d.LuidLow:x8}_phys_0";
                        result[key] = (d.Name, (long)d.DedicatedVideo.ToUInt64());
                    }
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        return result;
    }
}
