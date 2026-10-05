using System.Globalization;
using System.Text;

namespace RamTrace.Core;

public sealed class History : IDisposable
{
    private readonly Sqlite db;
    public History(string folder)
    {
        Directory.CreateDirectory(folder);
        db = new Sqlite(Path.Combine(folder, "history.db"));
        try
        {
        db.Exec("PRAGMA synchronous=NORMAL");
        // Checkpoint about every MiB and trim reusable journal space on WAL reset.
        // Active readers can temporarily keep the journal above this target.
        db.Exec("PRAGMA wal_autocheckpoint=256");
        db.Exec("PRAGMA journal_size_limit=1048576");
        if (db.Scalar("PRAGMA user_version") == 2) return;
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(folder).ToLowerInvariant())))[..16];
        using var gate = new Mutex(false, @"Local\RamTrace-schema-" + key);
        try { if (!gate.WaitOne(TimeSpan.FromSeconds(10))) throw new IOException("History initialization is busy. Try again shortly."); }
        catch (AbandonedMutexException) { }
        try
        {
        if (db.Scalar("PRAGMA user_version") == 2) return;
        db.Exec("PRAGMA auto_vacuum=INCREMENTAL");
        db.Exec("CREATE TABLE IF NOT EXISTS snapshots(time INTEGER PRIMARY KEY, samples INTEGER NOT NULL, total INTEGER NOT NULL, used_sum INTEGER NOT NULL, used_peak INTEGER NOT NULL, skipped INTEGER NOT NULL, duration INTEGER NOT NULL DEFAULT 0)");
        db.Exec("CREATE TABLE IF NOT EXISTS apps(id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE)");
        db.Exec("CREATE TABLE IF NOT EXISTS usage(time INTEGER NOT NULL REFERENCES snapshots(time) ON DELETE CASCADE, app_id INTEGER NOT NULL REFERENCES apps(id), ws_sum INTEGER NOT NULL, ws_peak INTEGER NOT NULL, ws_peak_time INTEGER NOT NULL, private_sum INTEGER NOT NULL, private_peak INTEGER NOT NULL, private_peak_time INTEGER NOT NULL, processes_sum INTEGER NOT NULL, observations INTEGER NOT NULL, first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL, PRIMARY KEY(time,app_id)) WITHOUT ROWID");
        db.Exec("CREATE INDEX IF NOT EXISTS usage_app_time ON usage(app_id,time)");
        db.Exec("CREATE TABLE IF NOT EXISTS gpu(time INTEGER NOT NULL REFERENCES snapshots(time) ON DELETE CASCADE, app_id INTEGER NOT NULL, metric INTEGER NOT NULL, value_sum INTEGER NOT NULL, peak INTEGER NOT NULL, peak_time INTEGER NOT NULL, observations INTEGER NOT NULL, first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL, PRIMARY KEY(time,app_id,metric)) WITHOUT ROWID");
        db.Exec("CREATE INDEX IF NOT EXISTS gpu_app_metric_time ON gpu(app_id,metric,time)");
        // Materialize a new database before enabling its WAL sidecar files.
        db.Exec("PRAGMA journal_mode=WAL");
        db.Exec("PRAGMA user_version=2");
        }
        finally { gate.ReleaseMutex(); }
        }
        catch { db.Dispose(); throw; }
    }
    public void Add(Sample sample)
    {
        db.Exec("BEGIN IMMEDIATE");
        try
        {
            db.Exec("INSERT INTO snapshots VALUES(?,1,?,?,?,?,0)", sample.Time, sample.Total, sample.Used, sample.Used, sample.Skipped);
            foreach (var a in sample.Apps)
            {
                db.Exec("INSERT OR IGNORE INTO apps(name) VALUES(?)", a.Name);
                db.Exec("INSERT INTO usage SELECT ?,id,?,?,?,?,?,?,?,1,?,? FROM apps WHERE name=?", sample.Time, a.WorkingSet, a.WorkingSet, sample.Time, a.PrivateBytes, a.PrivateBytes, sample.Time, a.Processes, sample.Time, sample.Time, a.Name);
                if (sample.Gpu != null)
                {
                    var id = db.Scalar("SELECT id FROM apps WHERE name=?", a.Name);
                    if (sample.Gpu.MemoryAvailable) { AddGpu(sample.Time, id, 2, a.Vram); AddGpu(sample.Time, id, 3, a.SharedGpu); }
                    if (sample.Gpu.ActivityAvailable) AddGpu(sample.Time, id, 4, a.GpuActivity);
                }
            }
            if (sample.Gpu != null)
            {
                if (sample.Gpu.MemoryAvailable) { AddGpu(sample.Time, 0, 2, sample.Gpu.Dedicated); AddGpu(sample.Time, 0, 3, sample.Gpu.Shared); }
                if (sample.Gpu.ActivityAvailable) AddGpu(sample.Time, 0, 4, sample.Gpu.Activity);
            }
            db.Exec("COMMIT");
        }
        catch { db.Exec("ROLLBACK"); throw; }
    }
    private void AddGpu(long time, long app, int metric, long value) => db.Exec("INSERT INTO gpu VALUES(?,?,?,?,?,?,1,?,?)", time, app, metric, value, value, time, time, time);
    public List<AppRow> Rows(long from, long to, int metric)
    {
        if (metric < 2) return Rows(from, to, metric == 1);
        var result = new List<AppRow>();
        using var s = db.Prepare("""
            SELECT a.id,a.name,
            COALESCE((SELECT value_sum*1.0/observations FROM gpu WHERE app_id=a.id AND metric=? AND time=(SELECT MAX(time) FROM gpu WHERE app_id=0 AND metric=? AND time>=? AND time<?)),0),
            SUM(g.value_sum)*1.0/SUM(g.observations),MAX(g.peak),
            (SELECT peak_time FROM gpu WHERE app_id=a.id AND metric=? AND time>=? AND time<? ORDER BY peak DESC,time LIMIT 1),
            MIN(g.first_seen),MAX(g.last_seen),
            COALESCE((SELECT SUM(processes_sum)*1.0/SUM(observations) FROM usage WHERE app_id=a.id AND time>=? AND time<?),0)
            FROM gpu g JOIN apps a ON a.id=g.app_id WHERE g.metric=? AND g.time>=? AND g.time<? GROUP BY a.id ORDER BY 5 DESC
            """, metric, metric, from, to, metric, from, to, from, to, metric, from, to);
        while (s.Read()) result.Add(new((int)s.Long(0), s.Text(1), s.Double(2), s.Double(3), s.Double(4), s.Long(5), s.Long(6), s.Long(7), s.Double(8)) { Percent = metric == 4 });
        return result;
    }
    public (long Time, double Value, double Peak, long Samples) GpuSummary(long from, long to, int metric = 2)
    {
        using var s = db.Prepare("SELECT MAX(time),MAX(peak),SUM(observations) FROM gpu WHERE app_id=0 AND metric=? AND time>=? AND time<?", metric, from, to); s.Read();
        var time = s.Long(0);
        using var v = db.Prepare("SELECT value_sum*1.0/observations FROM gpu WHERE app_id=0 AND metric=? AND time=?", metric, time);
        return (time, v.Read() ? v.Double(0) : 0, s.Double(1), s.Long(2));
    }
    public List<Point> Series(long from, long to, int? appId, int metric)
    {
        if (metric < 2) return Series(from, to, appId, metric == 1);
        var width = Math.Max(1000, (to - from) / 360);
        using var s = db.Prepare("""
            SELECT MIN(s.time),SUM(COALESCE(g.value_sum,0))*1.0/SUM(available.observations),MAX(COALESCE(g.peak,0)),MAX(s.duration)
            FROM snapshots s JOIN gpu available ON available.time=s.time AND available.app_id=0 AND available.metric=?
            LEFT JOIN gpu g ON g.time=s.time AND g.app_id=? AND g.metric=?
            WHERE s.time>=? AND s.time<? GROUP BY (s.time-?)/? ORDER BY MIN(s.time)
            """, metric, appId ?? 0, metric, from, to, from, width);
        var result = new List<Point>(); while (s.Read()) result.Add(new(s.Long(0), s.Double(1), s.Double(2), (int)s.Long(3))); return result;
    }
    public Overview Summary(long from, long to)
    {
        using var s = db.Prepare("SELECT COALESCE(MAX(time),0),COALESCE(MAX(total),0),COALESCE(MAX(used_peak),0),COALESCE(MIN(time),0),COALESCE(SUM(samples),0) FROM snapshots WHERE time>=? AND time<?", from, to);
        s.Read();
        var last = s.Long(0); var total = s.Long(1); var peak = s.Double(2); var first = s.Long(3); var n = s.Long(4);
        using var l = db.Prepare("SELECT used_sum*1.0/samples,skipped,(SELECT COUNT(*) FROM usage WHERE time=s.time) FROM snapshots s WHERE time=?", last);
        return l.Read() ? new(last, total, l.Double(0), peak, first, (int)l.Long(2), (int)l.Long(1), n) : new(0, 0, 0, 0, 0, 0, 0, 0);
    }
    public List<AppRow> Rows(long from, long to, bool privateBytes)
    {
        var m = privateBytes ? "private" : "ws";
        var result = new List<AppRow>();
        using var s = db.Prepare($"""
            SELECT a.id,a.name,
              COALESCE((SELECT u2.{m}_sum*1.0/u2.observations FROM usage u2 WHERE u2.app_id=a.id AND u2.time=(SELECT MAX(time) FROM snapshots WHERE time>=? AND time<?)),0),
              SUM(u.{m}_sum)*1.0/SUM(u.observations),MAX(u.{m}_peak),
              (SELECT u3.{m}_peak_time FROM usage u3 WHERE u3.app_id=a.id AND u3.time>=? AND u3.time<? ORDER BY u3.{m}_peak DESC,u3.time LIMIT 1),
              MIN(u.first_seen),MAX(u.last_seen),SUM(u.processes_sum)*1.0/SUM(u.observations)
            FROM usage u JOIN apps a ON a.id=u.app_id WHERE u.time>=? AND u.time<? GROUP BY a.id ORDER BY 5 DESC
            """, from, to, from, to, from, to);
        while (s.Read()) result.Add(new((int)s.Long(0), s.Text(1), s.Double(2), s.Double(3), s.Double(4), s.Long(5), s.Long(6), s.Long(7), s.Double(8)));
        return result;
    }
    public List<Point> Series(long from, long to, int? appId, bool privateBytes)
    {
        var width = Math.Max(1000, (to - from) / 360);
        var m = privateBytes ? "private" : "ws";
        var metric = appId == null ? "s.used_sum" : $"COALESCE(u.{m}_sum,0)";
        var peak = appId == null ? "s.used_peak" : $"COALESCE(u.{m}_peak,0)";
        // Divide by system observations to show zero when a process is absent.
        using var s = db.Prepare($"SELECT MIN(s.time),SUM({metric})*1.0/SUM(s.samples),MAX({peak}),MAX(s.duration) FROM snapshots s LEFT JOIN usage u ON u.time=s.time AND u.app_id=? WHERE s.time>=? AND s.time<? GROUP BY (s.time-?)/? ORDER BY MIN(s.time)", appId ?? -1, from, to, from, width);
        var result = new List<Point>();
        while (s.Read()) result.Add(new(s.Long(0), s.Double(1), s.Double(2), (int)s.Long(3)));
        return result;
    }
    public long FirstTime => db.Scalar("SELECT COALESCE(MIN(time),0) FROM snapshots");
    public void Maintain(long now, int days, bool compact = true)
    {
        var cutoff = (now - 48L * 3600000) / 300000 * 300000;
        db.Exec("BEGIN IMMEDIATE");
        try
        {
            if (days > 0) db.Exec("DELETE FROM snapshots WHERE time<?", now - days * 86400000L);
            if (compact)
            {
                db.Exec("CREATE TEMP TABLE roll_s AS SELECT (time/300000)*300000 AS time,SUM(samples) AS samples,MAX(total) AS total,SUM(used_sum) AS used_sum,MAX(used_peak) AS used_peak,MAX(skipped) AS skipped,300000 AS duration FROM snapshots WHERE time<? AND duration=0 GROUP BY time/300000", cutoff);
                db.Exec("""
                    CREATE TEMP TABLE roll_u AS
                    SELECT (time/300000)*300000 AS time,app_id,SUM(ws_sum) AS ws_sum,MAX(ws_peak) AS ws_peak,
                    MAX(CASE WHEN wr=1 THEN ws_peak_time END) AS ws_peak_time,SUM(private_sum) AS private_sum,MAX(private_peak) AS private_peak,
                    MAX(CASE WHEN pr=1 THEN private_peak_time END) AS private_peak_time,SUM(processes_sum) AS processes_sum,SUM(observations) AS observations,
                    MIN(first_seen) AS first_seen,MAX(last_seen) AS last_seen
                    FROM (SELECT u.*,ROW_NUMBER() OVER(PARTITION BY u.time/300000,app_id ORDER BY ws_peak DESC,u.time) AS wr,
                        ROW_NUMBER() OVER(PARTITION BY u.time/300000,app_id ORDER BY private_peak DESC,u.time) AS pr
                        FROM usage u JOIN snapshots s ON s.time=u.time WHERE s.time<? AND s.duration=0)
                    GROUP BY time/300000,app_id
                    """, cutoff);
                db.Exec("""
                    CREATE TEMP TABLE roll_g AS
                    SELECT (time/300000)*300000 AS time,app_id,metric,SUM(value_sum) AS value_sum,MAX(peak) AS peak,
                    MAX(CASE WHEN r=1 THEN peak_time END) AS peak_time,SUM(observations) AS observations,MIN(first_seen) AS first_seen,MAX(last_seen) AS last_seen
                    FROM (SELECT g.*,ROW_NUMBER() OVER(PARTITION BY g.time/300000,app_id,metric ORDER BY peak DESC,g.time) AS r
                    FROM gpu g JOIN snapshots s ON s.time=g.time WHERE s.time<? AND s.duration=0)
                    GROUP BY time/300000,app_id,metric
                    """, cutoff);
                db.Exec("DELETE FROM snapshots WHERE time<? AND duration=0", cutoff);
                db.Exec("INSERT INTO snapshots SELECT * FROM roll_s");
                db.Exec("INSERT INTO usage SELECT * FROM roll_u");
                db.Exec("INSERT INTO gpu SELECT * FROM roll_g");
                db.Exec("DROP TABLE roll_s"); db.Exec("DROP TABLE roll_u"); db.Exec("DROP TABLE roll_g");
            }
            db.Exec("COMMIT");
        }
        catch { db.Exec("ROLLBACK"); throw; }
        db.Exec("PRAGMA incremental_vacuum(256)");
        db.Exec("PRAGMA wal_checkpoint(PASSIVE)");
    }
    public void Export(string path, long from, long to, int metric, string search, int? appId = null, bool activeOnly = false, long minimumPeak = 0)
    {
        if (metric < 2) { Export(path, from, to, metric == 1, search, appId, activeOnly, minimumPeak); return; }
        var eligible = Rows(from, to, metric).Where(a => a.Name.Contains(search, StringComparison.OrdinalIgnoreCase) && (!appId.HasValue || a.Id == appId) && (!activeOnly || a.Latest > 0) && a.Peak >= minimumPeak).Select(a => a.Id).ToHashSet();
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("Timestamp (local ISO 8601),App,Metric,Average,Peak,Unit,Peak at (local ISO 8601),Observations,Bucket seconds");
        using var s = db.Prepare("SELECT g.time,g.app_id,a.name,g.value_sum*1.0/g.observations,g.peak,g.peak_time,g.observations,s.duration FROM gpu g JOIN apps a ON a.id=g.app_id JOIN snapshots s ON s.time=g.time WHERE g.metric=? AND g.time>=? AND g.time<? ORDER BY g.time,a.name", metric, from, to);
        while (s.Read())
        {
            if (!eligible.Contains((int)s.Long(1))) continue;
            double divisor = metric == 4 ? 100 : 1;
            writer.WriteLine(string.Join(",", DateTimeOffset.FromUnixTimeMilliseconds(s.Long(0)).ToLocalTime().ToString("o"), Csv(s.Text(2)), metric == 2 ? "Dedicated GPU memory" : metric == 3 ? "Shared GPU memory" : "GPU activity estimate", (s.Double(3) / divisor).ToString("0.##", CultureInfo.InvariantCulture), (s.Double(4) / divisor).ToString("0.##", CultureInfo.InvariantCulture), metric == 4 ? "percent" : "bytes", DateTimeOffset.FromUnixTimeMilliseconds(s.Long(5)).ToLocalTime().ToString("o"), s.Long(6), s.Long(7) / 1000));
        }
    }
    public void Export(string path, long from, long to, bool privateBytes, string search, int? appId = null, bool activeOnly = false, long minimumPeak = 0)
    {
        var m = privateBytes ? "private" : "ws";
        var eligible = Rows(from, to, privateBytes).Where(a => a.Name.Contains(search, StringComparison.OrdinalIgnoreCase) && (!appId.HasValue || a.Id == appId) && (!activeOnly || a.Latest > 0) && a.Peak >= minimumPeak).Select(a => a.Id).ToHashSet();
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("Timestamp (local ISO 8601),App,Metric,Average bytes,Peak bytes,Peak at (local ISO 8601),Observations,Bucket seconds");
        using var s = db.Prepare($"SELECT u.time,a.id,a.name,u.{m}_sum*1.0/u.observations,u.{m}_peak,u.{m}_peak_time,u.observations,s.duration FROM usage u JOIN apps a ON a.id=u.app_id JOIN snapshots s ON s.time=u.time WHERE u.time>=? AND u.time<? ORDER BY u.time,a.name", from, to);
        while (s.Read())
        {
            if (!eligible.Contains((int)s.Long(1))) continue;
            writer.WriteLine(string.Join(",", DateTimeOffset.FromUnixTimeMilliseconds(s.Long(0)).ToLocalTime().ToString("o"), Csv(s.Text(2)), privateBytes ? "Private bytes (committed)" : "Working set (resident)", s.Double(3).ToString("0", CultureInfo.InvariantCulture), s.Long(4), DateTimeOffset.FromUnixTimeMilliseconds(s.Long(5)).ToLocalTime().ToString("o"), s.Long(6), s.Long(7) / 1000));
        }
    }
    private static string Csv(string value)
    {
        // Prevent spreadsheet formula execution from unusual executable names.
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
    public void Dispose() => db.Dispose();
}
