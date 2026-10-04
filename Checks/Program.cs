using RamTrace.Core;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine("work", "checks-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
if (args.Contains("--seed"))
{
    using var seeded = new History(root);
    for (int i = 720; i >= 1; i--)
    {
        long t = now - i * 30000L;
        var apps = new List<AppSample> { new("Demo Browser.exe", (long)((1.6 + .5 * Math.Sin(i / 38.0)) * 1073741824), 2500000000, 12), new("Demo Editor.exe", (long)((.75 + .15 * Math.Sin(i / 62.0)) * 1073741824), 1000000000, 7), new("Demo Music.exe", 240000000, 300000000, 3) };
        if (i < 250) apps.Add(new("Demo Game.exe", (long)((3 + 2 * Math.Sin(i / 90.0)) * 1073741824), 6000000000, 2));
        seeded.Add(new(t, 32L * 1073741824, (long)((12 + 1.5 * Math.Sin(i / 60.0)) * 1073741824), 2, apps));
    }
    new Settings { IntervalSeconds = 15 }.Save(root);
    Console.WriteLine("Seeded isolated UI test history: " + root);
    return;
}
int passed = 0;
void Check(bool condition, string title) { if (!condition) throw new Exception("FAIL: " + title); passed++; Console.WriteLine("PASS: " + title); }
using (var history = new History(root))
{
    long old = (now - 3 * 86400000L) / 300000 * 300000;
    history.Add(new(old + 1000, 10000, 6000, 0, new() { new("Alpha.exe", 100, 200, 1) }));
    history.Add(new(old + 31000, 10000, 8000, 1, new() { new("Alpha.exe", 300, 250, 2), new("Beta.exe", 900, 950, 1) }));
    history.Add(new(old + 61000, 10000, 7000, 0, new() { new("Alpha.exe", 200, 450, 1) }));
    history.Add(new(now - 31000, 10000, 8000, 0, new() { new("Alpha.exe", 400, 700, 2), new("=formula.exe", 20, 40, 1) }));
    history.Add(new(now - 1000, 10000, 9000, 0, new() { new("Alpha.exe", 600, 800, 2) }));
    var rows = history.Rows(0, now, false);
    var alpha = rows.Single(a => a.Name == "Alpha.exe");
    Check(alpha.Average == 320 && alpha.Peak == 600 && alpha.Latest == 600, "Average, peak, and latest aggregation");
    Check(alpha.PeakTime == now - 1000 && alpha.FirstSeen == old + 1000, "Exact peak and first-seen timestamps");
    Check(rows.Single(a => a.Name == "Beta.exe").Latest == 0, "Exited app is zero in latest sample");
    Check(history.Rows(0, now, true).Single(a => a.Name == "Alpha.exe").Average == 480, "Private bytes remain distinct from resident RAM");
    var series = history.Series(now - 60000, now, rows.Single(a => a.Name == "=formula.exe").Id, false);
    Check(series.Count == 2 && series[1].Value == 0, "Missing app has zero usage on system timeline");
    try { history.Add(new(now - 1000, 10000, 1, 0, new())); } catch (IOException) { }
    Check(history.Summary(0, now).Samples == 5, "Duplicate sample rolls back without corrupting history");
    history.Maintain(now, 30);
    var after = history.Rows(0, now, false).Single(a => a.Name == "Alpha.exe");
    Check(after.Average == alpha.Average && after.Peak == alpha.Peak && after.PeakTime == alpha.PeakTime, "Compaction preserves weighted averages and peaks");
    var rolled = history.Rows(old, old + 300000, false).Single(a => a.Name == "Alpha.exe");
    Check(rolled.Peak == 300 && rolled.PeakTime == old + 31000 && rolled.Average == 200, "Rolled working-set peak keeps original timestamp");
    var committed = history.Rows(old, old + 300000, true).Single(a => a.Name == "Alpha.exe");
    Check(committed.Peak == 450 && committed.PeakTime == old + 61000, "Rolled private peak keeps its own timestamp");
    Check(history.Summary(old, old + 300000).Samples == 3 && history.Summary(old, old + 300000).Used == 7000, "Compaction preserves system sample counts and mean");
    history.Maintain(now, 30);
    Check(history.Summary(0, now).Samples == 5, "Maintenance is idempotent");
    var csv = Path.Combine(root, "filtered.csv");
    history.Export(csv, 0, now, false, "alpha");
    Check(File.ReadAllLines(csv).Length == 4 && !File.ReadAllText(csv).Contains("Beta.exe"), "CSV respects search and exports compacted data");
    history.Export(csv, 0, now, false, "formula");
    Check(File.ReadAllText(csv).Contains("'=formula.exe"), "CSV neutralizes spreadsheet formulas");
    history.Export(csv, 0, now, false, "", null, true, 500);
    Check(File.ReadAllLines(csv).Skip(1).All(l => l.Contains("Alpha.exe")), "CSV respects activity and peak filters");
    Check(history.Summary(now + 1000, now + 2000).Samples == 0 && history.Series(now + 1000, now + 2000, null, false).Count == 0, "Empty ranges return no invented data");
    history.Maintain(now, 1);
    Check(history.Rows(0, now, false).All(a => a.Name != "Beta.exe") && history.Summary(0, now).Samples == 2, "Retention deletes expired samples and dependent rows");
}
using (var reopened = new History(root)) Check(reopened.Summary(0, now).Samples == 2, "History survives reopening");
var settings = new Settings { Theme = "Dark", IntervalSeconds = 60, RetentionDays = 7, Paused = true }; settings.Save(root);
var loaded = Settings.Load(root);
Check(loaded.Theme == "Dark" && loaded.IntervalSeconds == 60 && loaded.Paused, "Settings survive atomic save and reload");
File.WriteAllText(Path.Combine(root, "settings.json"), "{broken");
Check(Settings.Load(root).Theme == "System", "Corrupt settings fall back safely");
var real = Sampler.Capture();
Check(real.Total > 0 && real.Used > 0 && real.Used <= real.Total && real.Apps.Count > 0 && real.Apps.All(a => a.WorkingSet >= 0 && a.Processes > 0), "Live Windows memory capture returns valid system and app values");
using (var history = new History(Path.Combine(root, "gpu-checks")))
{
    long old = (now - 100 * 86400000L) / 300000 * 300000;
    var gpu1 = new GpuReading(true, true, 2000, 800, 2000, new(), new());
    var gpu2 = new GpuReading(true, true, 4000, 600, 9000, new(), new());
    history.Add(new(old + 1000, 10000, 5000, 0, new() { new("GPU app.exe", 100, 150, 2, 1000, 400, 1500) }, gpu1));
    history.Add(new(old + 11000, 10000, 6000, 0, new() { new("GPU app.exe", 120, 160, 2, 3000, 200, 8000) }, gpu2));
    history.Add(new(now - 1000, 10000, 5000, 0, new() { new("GPU app.exe", 100, 150, 2) }));
    history.Maintain(now, 0, false);
    Check(history.Summary(0, now).Samples == 3 && history.Series(old, old + 300000, null, 2).Count == 2, "Forever with full detail preserves every sample beyond 90 days");
    history.Maintain(now, 0);
    var vram = history.Rows(0, now, 2).Single();
    Check(vram.Average == 2000 && vram.Peak == 3000 && vram.PeakTime == old + 11000, "GPU compaction preserves VRAM averages and exact peak timestamps");
    Check(history.Rows(0, now, 3).Single().Average == 300 && history.Rows(0, now, 4).Single().PeakText == "80.0%", "Shared GPU memory and GPU activity use distinct metrics and units");
    Check(history.Series(now - 10000, now, null, 2).Count == 0, "Unavailable GPU counters produce a gap, not a false zero");
    Check(history.GpuSummary(0, now).Value == 3000 && history.GpuSummary(0, now).Peak == 4000, "System GPU totals remain separate from per-app shared allocations");
    history.Export(Path.Combine(root, "gpu.csv"), 0, now, 4, "GPU app");
    Check(File.ReadAllText(Path.Combine(root, "gpu.csv")).Contains(",80,percent,"), "GPU CSV exports percentages with explicit units");
}
using (var gpu = new GpuSampler())
{
    var g = gpu.Capture();
    Check(g.MemoryAvailable && g.Devices.Count > 0 && g.Dedicated >= 0, "Live GPU memory counters are available on this machine");
    foreach (var d in g.Devices) Console.WriteLine($"GPU: {d.Name}; capacity={Format.Bytes(d.Capacity)}; dedicated={Format.Bytes(d.Dedicated)}; shared={Format.Bytes(d.Shared)}");
    Thread.Sleep(1100);
    var g2 = gpu.Capture();
    Check(g2.ActivityAvailable && g2.Activity >= 0 && g2.Activity <= 10000, "Live GPU engine activity warms up and returns a valid percentage");
}
Console.WriteLine($"{passed} checks passed.");
