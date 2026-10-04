using System.Runtime.InteropServices;
using System.Text;

namespace RamTrace.Core;

// Windows' built-in SQLite: no service, bundled database engine, or NuGet dependency.
public sealed class Sqlite : IDisposable
{
    private IntPtr db;
    private const string Dll = "winsqlite3.dll";
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2(byte[] name, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_extended_errcode(IntPtr db);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_system_errno(IntPtr db);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int length, out IntPtr stmt, IntPtr tail);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_step(IntPtr stmt);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_int64(IntPtr stmt, int i, long value);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_text(IntPtr stmt, int i, byte[] value, int len, IntPtr destroy);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern long sqlite3_column_int64(IntPtr stmt, int i);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern double sqlite3_column_double(IntPtr stmt, int i);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr stmt, int i);
    public Sqlite(string path)
    {
        var result = sqlite3_open_v2(Utf8(path), out db, 2 | 4 | 0x10000, IntPtr.Zero);
        if (result != 0) { var error = Error(); Dispose(); throw error; }
        sqlite3_busy_timeout(db, 5000);
        Exec("PRAGMA foreign_keys=ON");
        Exec("PRAGMA cache_size=-2048");
    }
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + '\0');
    internal Exception Error() => new IOException((Marshal.PtrToStringUTF8(sqlite3_errmsg(db)) ?? "SQLite error") + $" (SQLite {sqlite3_extended_errcode(db)}, Windows {sqlite3_system_errno(db)})");
    public Statement Prepare(string sql, params object[] values)
    {
        if (sqlite3_prepare_v2(db, Utf8(sql), -1, out var p, IntPtr.Zero) != 0) throw Error();
        var stmt = new Statement(this, p);
        try
        {
            for (var i = 0; i < values.Length; i++)
            {
                var rc = values[i] is string s ? sqlite3_bind_text(p, i + 1, Utf8(s), Encoding.UTF8.GetByteCount(s), new IntPtr(-1)) : sqlite3_bind_int64(p, i + 1, Convert.ToInt64(values[i]));
                if (rc != 0) throw Error();
            }
            return stmt;
        }
        catch { stmt.Dispose(); throw; }
    }
    public void Exec(string sql, params object[] values) { using var s = Prepare(sql, values); while (s.Read()) { } }
    public long Scalar(string sql, params object[] values) { using var s = Prepare(sql, values); return s.Read() ? s.Long(0) : 0; }
    public void Dispose() { if (db != IntPtr.Zero) { sqlite3_close_v2(db); db = IntPtr.Zero; } }
    public sealed class Statement : IDisposable
    {
        private IntPtr p;
        private readonly Sqlite db;
        internal Statement(Sqlite db, IntPtr p) { this.db = db; this.p = p; }
        public bool Read() { var rc = sqlite3_step(p); if (rc == 100) return true; if (rc == 101) return false; throw db.Error(); }
        public long Long(int i) => sqlite3_column_int64(p, i);
        public double Double(int i) => sqlite3_column_double(p, i);
        public string Text(int i) => Marshal.PtrToStringUTF8(sqlite3_column_text(p, i)) ?? "";
        public void Dispose() { if (p != IntPtr.Zero) { sqlite3_finalize(p); p = IntPtr.Zero; } }
    }
}
