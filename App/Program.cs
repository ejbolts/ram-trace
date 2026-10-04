using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Win32;
using RamTrace.Core;
using Forms = System.Windows.Forms;

namespace RamTrace;

internal static class Program
{
    internal static string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RamTrace");
    internal static string Pipe => "RamTrace-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Folder.ToLowerInvariant())))[..16];
    internal static bool TestMode;
    [STAThread]
    private static void Main(string[] args)
    {
        var i = Array.IndexOf(args, "--data-dir");
        if (i >= 0 && i + 1 < args.Length) { Folder = Path.GetFullPath(args[i + 1]); TestMode = true; }
        Directory.CreateDirectory(Folder);
        try
        {
            if (args.Contains("--install")) { Installer.Install(); return; }
            if (args.Contains("--dashboard")) { DashboardHost.Run(); return; }
            if (args.Contains("--quit")) { Send("EXIT"); return; }
            if (args.Contains("--pause")) { Send("PAUSE"); return; }
            if (args.Contains("--resume")) { Send("RESUME"); return; }
            using var mutex = new Mutex(true, @"Local\" + Pipe, out bool first);
            if (!first) { if (!args.Contains("--background")) Send("SHOW"); return; }
            Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
            Forms.Application.EnableVisualStyles();
            Forms.Application.SetCompatibleTextRenderingDefault(false);
            using var ctx = new Collector(!args.Contains("--background"));
            Forms.Application.Run(ctx);
        }
        catch (Exception e)
        {
            Log(e);
            if (!args.Contains("--background")) Forms.MessageBox.Show("RamTrace could not start. " + e.Message + "\n\nDetails: " + Path.Combine(Folder, "errors.log"), "RamTrace");
        }
    }
    internal static void Log(Exception e)
    {
        try { var p = Path.Combine(Folder, "errors.log"); if (File.Exists(p) && new FileInfo(p).Length > 1048576) File.Move(p, p + ".previous", true); File.AppendAllText(p, DateTimeOffset.Now + " " + e + Environment.NewLine); } catch { }
    }
    internal static string Send(string command)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(1200);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);
            writer.WriteLine(command);
            return reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult() ?? "OFFLINE";
        }
        catch (Exception e) { if (TestMode) Log(new IOException("Pipe " + Pipe + " in " + Folder, e)); return "OFFLINE"; }
    }
    internal static Process? Launch(string role)
    {
        var p = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        p.ArgumentList.Add(role);
        if (TestMode) { p.ArgumentList.Add("--data-dir"); p.ArgumentList.Add(Folder); }
        return Process.Start(p);
    }
    internal static bool StartupEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return k?.GetValue("RamTrace") is string;
    }
    internal static void SetStartup(bool enabled)
    {
        if (TestMode) return;
        using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) k.SetValue("RamTrace", "\"" + Environment.ProcessPath + "\" --background");
        else k.DeleteValue("RamTrace", false);
    }
}

internal sealed class Collector : Forms.ApplicationContext
{
    private readonly Forms.NotifyIcon tray;
    private readonly Forms.Control dispatch = new();
    private readonly Forms.Timer timer = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly GpuSampler gpu = new();
    private History? history;
    private Settings settings;
    private Process? dashboard;
    private bool busy;
    private long lastSample;
    private long lastMaintenance;
    private string error = "";
    private readonly Forms.ToolStripMenuItem pause;
    public Collector(bool show)
    {
        settings = Settings.Load(Program.Folder);
        dispatch.CreateControl();
        tray = new Forms.NotifyIcon { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!), Text = "RamTrace • starting", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open RamTrace", null, (_, _) => ShowDashboard());
        pause = new Forms.ToolStripMenuItem(settings.Paused ? "Resume logging" : "Pause logging", null, (_, _) => TogglePause(!settings.Paused));
        menu.Items.Add(pause);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit RamTrace", null, (_, _) => ExitThread());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => ShowDashboard();
        timer.Interval = settings.IntervalSeconds * 1000;
        timer.Tick += async (_, _) => await Capture();
        timer.Start();
        _ = Listen();
        _ = Capture();
        if (show) ShowDashboard();
    }
    private void ShowDashboard()
    {
        // A named event brings an existing dashboard forward without another resident UI.
        try { using var e = EventWaitHandle.OpenExisting(@"Local\" + Program.Pipe + "-show"); e.Set(); return; }
        catch (WaitHandleCannotBeOpenedException) { }
        if (dashboard != null && !dashboard.HasExited) return;
        dashboard?.Dispose();
        dashboard = Program.Launch("--dashboard");
    }
    private async Task Capture()
    {
        if (busy || stopping.IsCancellationRequested || settings.Paused) { UpdateTray(); return; }
        busy = true;
        var retention = settings.RetentionDays;
        var compact = settings.CompactHistory;
        try
        {
            var sample = await Task.Run(() =>
            {
                var s = Sampler.Capture(gpu);
                var db = history ??= new History(Program.Folder);
                db.Add(s);
                if (s.Gpu != null)
                {
                    var file = Path.Combine(Program.Folder, "gpu-devices.json");
                    File.WriteAllText(file + ".tmp", System.Text.Json.JsonSerializer.Serialize(s.Gpu.Devices));
                    File.Move(file + ".tmp", file, true);
                }
                if (s.Time - lastMaintenance > 3600000) { db.Maintain(s.Time, retention, compact); lastMaintenance = s.Time; }
                return s;
            });
            lastSample = sample.Time; error = "";
            tray.Text = "RamTrace • " + (100.0 * sample.Used / sample.Total).ToString("0") + "% RAM • logging";
        }
        catch (Exception ex) { error = ex.Message; Program.Log(ex); tray.Text = "RamTrace • logging error — open for details"; }
        finally { busy = false; }
    }
    private void UpdateTray()
    {
        pause.Text = settings.Paused ? "Resume logging" : "Pause logging";
        if (settings.Paused) tray.Text = "RamTrace • logging paused";
    }
    private void TogglePause(bool value)
    {
        settings = Settings.Load(Program.Folder); settings.Paused = value; settings.Save(Program.Folder);
        UpdateTray(); if (!value) _ = Capture();
    }
    private async Task Listen()
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(Program.Pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(stopping.Token).ConfigureAwait(false);
                using var reader = new StreamReader(server, leaveOpen: true);
                using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
                var cmd = await reader.ReadLineAsync(stopping.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                dispatch.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        switch (cmd)
                        {
                            case "SHOW": ShowDashboard(); break;
                            case "PAUSE": TogglePause(true); break;
                            case "RESUME": TogglePause(false); break;
                            case "RELOAD": settings = Settings.Load(Program.Folder); timer.Interval = settings.IntervalSeconds * 1000; lastMaintenance = 0; UpdateTray(); _ = Capture(); break;
                            case "EXIT": tcs.SetResult("OK"); timer.Stop(); _ = QuitWhenIdle(); return;
                        }
                        tcs.SetResult(error.Length > 0 ? "ERROR|" + error : (settings.Paused ? "PAUSED|" : "LOGGING|") + lastSample);
                    }
                    catch (Exception e) { Program.Log(e); tcs.TrySetResult("ERROR|" + e.Message); }
                }));
                var reply = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                await writer.WriteLineAsync(reply).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { if (!stopping.IsCancellationRequested) Program.Log(ex); }
        }
    }
    private async Task QuitWhenIdle()
    {
        await Task.Delay(150);
        while (busy) await Task.Delay(100);
        ExitThread();
    }
    protected override void ExitThreadCore()
    {
        if (busy) { timer.Stop(); _ = QuitWhenIdle(); return; }
        stopping.Cancel(); timer.Stop(); tray.Visible = false; tray.Dispose(); dispatch.Dispose(); timer.Dispose(); gpu.Dispose(); history?.Dispose();
        base.ExitThreadCore();
    }
}
