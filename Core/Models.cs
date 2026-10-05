using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace RamTrace.Core;

public sealed class Settings
{
    public int IntervalSeconds { get; set; } = 10;
    public int RetentionDays { get; set; } = 7;
    public string Theme { get; set; } = "System";
    public bool Paused { get; set; }
    public bool GraphExpanded { get; set; } = true;
    public bool CompactHistory { get; set; } = true;
    public static Settings Load(string folder)
    {
        try
        {
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(folder, "settings.json"))) ?? new();
            s.IntervalSeconds = new[] { 10, 15, 30, 60, 120 }.Contains(s.IntervalSeconds) ? s.IntervalSeconds : 10;
            s.RetentionDays = new[] { 0, 7, 30, 90 }.Contains(s.RetentionDays) ? s.RetentionDays : 7;
            s.Theme = new[] { "System", "Light", "Dark" }.Contains(s.Theme) ? s.Theme : "System";
            return s;
        }
        catch (IOException) { return new(); }
        catch (JsonException) { return new(); }
    }
    public void Save(string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "settings.json");
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, true);
    }
}

public record AppSample(string Name, long WorkingSet, long PrivateBytes, int Processes, long Vram = 0, long SharedGpu = 0, long GpuActivity = 0);
public record Sample(long Time, long Total, long Used, int Skipped, List<AppSample> Apps, GpuReading? Gpu = null);
public record Point(long Time, double Value, double Peak, int Duration = 0);
public sealed record AppRow(int Id, string Name, double Latest, double Average, double Peak, long PeakTime, long FirstSeen, long LastSeen, double Processes)
{
    public bool Percent { get; init; }
    public string LatestText => Percent ? (Latest / 100).ToString("0.0") + "%" : Format.Bytes(Latest);
    public string AverageText => Percent ? (Average / 100).ToString("0.0") + "%" : Format.Bytes(Average);
    public string PeakText => Percent ? (Peak / 100).ToString("0.0") + "%" : Format.Bytes(Peak);
    public string PeakTimeText => Format.Local(PeakTime).ToString("dd MMM, HH:mm:ss");
    public string LastSeenText => Format.Local(LastSeen).ToString("dd MMM, HH:mm:ss");
    public string ProcessText => Processes.ToString("0.#");
}
public record Overview(long LastTime, long Total, double Used, double Peak, long FirstTime, int Apps, int Skipped, long Samples);
public static class Format
{
    public static DateTime Local(long time) => DateTimeOffset.FromUnixTimeMilliseconds(time).LocalDateTime;
    public static string Bytes(double b) => b >= 1073741824 ? (b / 1073741824).ToString("0.00") + " GB" : (b / 1048576).ToString("0.0") + " MB";
}

public static class Sampler
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    public static Sample Capture(GpuSampler? gpuSampler = null)
    {
        var m = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref m)) throw new System.ComponentModel.Win32Exception();
        var gpu = gpuSampler?.Capture();
        var groups = new Dictionary<string, AppSample>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == 0) continue;
                    var name = process.ProcessName + ".exe";
                    var ws = process.WorkingSet64;
                    var pb = process.PrivateMemorySize64;
                    var g = gpu?.Processes.GetValueOrDefault(process.Id) ?? new GpuProcess(0, 0, 0);
                    if (groups.TryGetValue(name, out var prior)) groups[name] = prior with { WorkingSet = prior.WorkingSet + ws, PrivateBytes = prior.PrivateBytes + pb, Processes = prior.Processes + 1, Vram = prior.Vram + g.Dedicated, SharedGpu = prior.SharedGpu + g.Shared, GpuActivity = Math.Min(10000, prior.GpuActivity + g.Activity) };
                    else groups[name] = new(name, ws, pb, 1, g.Dedicated, g.Shared, g.Activity);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { skipped++; }
            }
        }
        return new(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), (long)m.TotalPhys, (long)(m.TotalPhys - m.AvailPhys), skipped, groups.Values.ToList(), gpu);
    }
}
