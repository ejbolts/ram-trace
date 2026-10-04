using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using RamTrace.Core;

namespace RamTrace;

internal static class DashboardHost
{
    public static void Run()
    {
        using var mutex = new Mutex(true, @"Local\" + Program.Pipe + "-ui", out var first);
        if (!first) { try { using var e = EventWaitHandle.OpenExisting(@"Local\" + Program.Pipe + "-show"); e.Set(); } catch { } return; }
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) => { Program.Log(e.Exception); MessageBox.Show("RamTrace: " + e.Exception.Message, "RamTrace"); e.Handled = true; };
        app.Run(new MainWindow());
    }
}

public partial class MainWindow : Window
{
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly EventWaitHandle showEvent;
    private readonly RegisteredWaitHandle showWait;
    private List<AppRow> rows = new();
    private Settings settings;
    private int? selectedApp;
    private bool ready, binding, loading, pending, closed;
    private long from, to;
    private string status = "OFFLINE";
    private int Metric => MetricBox.SelectedIndex;
    private long Minimum => Metric == 4 ? MinimumBox.SelectedIndex switch { 1 => 1000, 2 => 5000, 3 => 9000, _ => 0 } : MinimumBox.SelectedIndex switch { 1 => 100 * 1048576L, 2 => 500 * 1048576L, 3 => 1073741824, _ => 0 };
    public MainWindow()
    {
        settings = Settings.Load(Program.Folder);
        ApplyTheme();
        InitializeComponent();
        ThemeBox.SelectedIndex = Array.IndexOf(new[] { "System", "Light", "Dark" }, settings.Theme);
        IntervalBox.SelectedIndex = Array.IndexOf(new[] { 10, 15, 30, 60, 120 }, settings.IntervalSeconds);
        RetentionBox.SelectedIndex = Array.IndexOf(new[] { 0, 7, 30, 90 }, settings.RetentionDays);
        CompactCheck.IsChecked = settings.CompactHistory;
        ApplyGraphVisibility();
        StartupCheck.IsChecked = Program.StartupEnabled();
        if (Program.TestMode) { Title = "RamTrace • test workspace"; StartupCheck.IsEnabled = false; }
        FromDate.SelectedDate = DateTime.Today; ToDate.SelectedDate = DateTime.Today;
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + Program.Pipe + "-show");
        showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => Dispatcher.BeginInvoke(new Action(() => { if (closed) return; Show(); WindowState = WindowState.Normal; Activate(); Topmost = true; Topmost = false; })), null, -1, false);
        SystemEvents.UserPreferenceChanged += OnSystemTheme;
        SourceInitialized += (_, _) => ApplyTitleBar();
        Loaded += async (_, _) => { ready = true; await RefreshData(); refresh.Start(); };
        refresh.Tick += async (_, _) => await RefreshData();
        Closed += (_, _) => { closed = true; refresh.Stop(); SystemEvents.UserPreferenceChanged -= OnSystemTheme; showWait.Unregister(null); showEvent.Dispose(); };
    }
    private void OnSystemTheme(object sender, UserPreferenceChangedEventArgs e) { if (!closed) Dispatcher.BeginInvoke(new Action(() => { if (settings.Theme == "System") ApplyTheme(); })); }
    private bool IsDark()
    {
        if (settings.Theme != "System") return settings.Theme == "Dark";
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("AppsUseLightTheme") is int n && n == 0;
    }
    private void ApplyTheme()
    {
        bool dark = IsDark();
        var colors = new Dictionary<string, string>
        {
            ["Bg"] = dark ? "#11171D" : "#F4F6F8", ["Sidebar"] = dark ? "#151D25" : "#EDF1F4",
            ["Surface"] = dark ? "#1A242E" : "#FFFFFF", ["Ink"] = dark ? "#E7EEF5" : "#1E2C3B",
            ["Muted"] = dark ? "#A2B2C1" : "#627388", ["Stroke"] = dark ? "#2B3947" : "#DCE3EA",
            ["Accent"] = dark ? "#61D8C0" : "#117F70", ["AccentSoft"] = dark ? "#23443F" : "#DCF1EA", ["Hover"] = dark ? "#2A3946" : "#E9EFF3"
        };
        foreach (var c in colors) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c.Value)); b.Freeze(); Resources[c.Key] = b; }
        // InitializeComponent supplies styles but does not replace these keyed brushes.
        Chart?.InvalidateVisual(); ApplyTitleBar();
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    private void ApplyTitleBar() { var h = new WindowInteropHelper(this).Handle; if (h != IntPtr.Zero) { int d = IsDark() ? 1 : 0; DwmSetWindowAttribute(h, 20, ref d, sizeof(int)); } }
    private bool ResolveRange()
    {
        var endNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1;
        var begin = RangeBox.SelectedIndex switch { 0 => endNow - 3600000, 1 => endNow - 6 * 3600000, 2 => endNow - 24 * 3600000, 3 => endNow - 7 * 86400000L, 4 => endNow - 30 * 86400000L, _ => 0 };
        if (RangeBox.SelectedIndex != 6) { from = begin; to = endNow; return true; }
        if (!FromDate.SelectedDate.HasValue || !ToDate.SelectedDate.HasValue || !TimeSpan.TryParseExact(FromTime.Text, @"hh\:mm", CultureInfo.InvariantCulture, out var startTime) || !TimeSpan.TryParseExact(ToTime.Text, @"hh\:mm", CultureInfo.InvariantCulture, out var endTime))
        { Footer.Text = "Choose both dates and enter times as HH:mm (24-hour clock)."; return false; }
        var start = DateTime.SpecifyKind(FromDate.SelectedDate.Value.Date + startTime, DateTimeKind.Local);
        var end = DateTime.SpecifyKind(ToDate.SelectedDate.Value.Date + endTime, DateTimeKind.Local).AddMinutes(1);
        if (end <= start || TimeZoneInfo.Local.IsInvalidTime(start) || TimeZoneInfo.Local.IsInvalidTime(end)) { Footer.Text = "The end must be after the start, with valid local times."; return false; }
        from = new DateTimeOffset(start).ToUnixTimeMilliseconds(); to = new DateTimeOffset(end).ToUnixTimeMilliseconds();
        return true;
    }
    private async Task RefreshData()
    {
        if (!ready || closed) return;
        if (loading) { pending = true; return; }
        if (!ResolveRange()) return;
        loading = true;
        var f = from; var t = to; var p = Metric; var id = selectedApp; var all = RangeBox.SelectedIndex == 5;
        try
        {
            var data = await Task.Run(() =>
            {
                var state = Program.Send("STATUS");
                using var db = new History(Program.Folder);
                var actualFrom = all ? db.FirstTime : f;
                if (actualFrom == 0) actualFrom = t - 3600000;
                return (state, actualFrom, summary: db.Summary(actualFrom, t), gpu: db.GpuSummary(actualFrom, t), apps: db.Rows(actualFrom, t, p), series: db.Series(actualFrom, t, id, p));
            });
            if (closed) return;
            status = data.state; from = data.actualFrom;
            rows = data.apps;
            var s = data.summary;
            UsedValue.Text = s.Samples == 0 ? "—" : Format.Bytes(s.Used);
            UsedHint.Text = s.Samples == 0 ? "No samples in this range" : (100 * s.Used / s.Total).ToString("0") + "% of " + Format.Bytes(s.Total) + " · latest in range";
            PeakValue.Text = data.gpu.Samples == 0 ? "—" : Format.Bytes(data.gpu.Value);
            PeakHint.Text = data.gpu.Samples == 0 ? "GPU counters not available in range" : "Peak " + Format.Bytes(data.gpu.Peak) + " · all GPUs in range";
            AppsValue.Text = rows.Count.ToString("N0");
            AppsHint.Text = "Grouped by executable name";
            UpdateStatus();
            ApplyList();
            var selected = rows.FirstOrDefault(a => a.Id == id);
            ChartTitle.Text = selected?.Name ?? (p >= 2 ? "GPU " + (p == 4 ? "activity" : "memory") + " · all adapters" : "System memory");
            ResetButton.Visibility = id.HasValue ? Visibility.Visible : Visibility.Collapsed;
            var metricName = p switch { 1 => "Private bytes · committed memory", 2 => "Dedicated VRAM · all adapters", 3 => "Shared GPU memory · all adapters", 4 => "GPU activity estimate · %", _ => "Working set · resident RAM" };
            ChartSubtitle.Text = id.HasValue || p >= 2 ? metricName + "    ·    dashed: sampled peak" : "Physical RAM in use    ·    dashed: sampled peak";
            ChartHint.Text = selected == null ? "Select an app to explore its timeline. Hover the graph for exact values." : "Peak " + selected.PeakText + " at " + selected.PeakTimeText + "   ·   " + selected.ProcessText + " processes on average";
            Chart.SetData(data.series, from, to, id == null && p < 2 ? s.Total : 0, settings.IntervalSeconds * 1000, p == 4);
            Footer.Text = s.Samples == 0 ? "No history in this range yet." : "Updated " + Format.Local(s.LastTime).ToString("dd MMM, HH:mm:ss") + "   ·   " + s.Samples.ToString("N0") + " samples   ·   " + (p >= 2 ? "GPU readings cover all adapters. Driver counters may differ from Task Manager." : s.Skipped + " processes unavailable   ·   " + (p == 1 ? "Private bytes include paged-out memory." : "Shared pages mean app totals can overlap."));
            if (status.StartsWith("ERROR|")) Footer.Text = "Logging needs attention: " + status[6..];
            UpdateStorage();
        }
        catch (Exception e) { Program.Log(e); Footer.Text = "Could not read history: " + e.Message; }
        finally { loading = false; if (pending && !closed) { pending = false; _ = RefreshData(); } }
    }
    private void UpdateStatus()
    {
        var offline = status == "OFFLINE";
        var paused = status.StartsWith("PAUSED");
        StatusLabel.Text = offline ? "○  Logger stopped" : paused ? "Ⅱ  Paused" : status.StartsWith("ERROR") ? "!  Logging error" : "●  Logging";
        PauseButton.Content = offline ? "Start logging" : paused ? "Resume logging" : "Pause logging";
        settings = Settings.Load(Program.Folder);
        IntervalLabel.Text = "Every " + settings.IntervalSeconds + " seconds";
    }
    private void ApplyList()
    {
        if (!ready) return;
        var query = rows.Where(a => a.Name.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase) && (ActivityBox.SelectedIndex != 1 || a.Latest > 0) && a.Peak >= Minimum).ToList();
        var sorts = AppGrid.Items.SortDescriptions.ToList();
        binding = true;
        AppGrid.ItemsSource = query;
        if (sorts.Count == 0) sorts.Add(new SortDescription(nameof(AppRow.Peak), ListSortDirection.Descending));
        foreach (var s in sorts) AppGrid.Items.SortDescriptions.Add(s);
        AppGrid.SelectedItem = query.FirstOrDefault(a => a.Id == selectedApp);
        binding = false;
        EmptyLabel.Visibility = query.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyLabel.Text = rows.Count == 0 ? (Metric >= 2 ? "No GPU readings in this range. Counters may be unavailable or warming up." : "No samples in this range yet.") : "No apps match these filters.";
    }
    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return;
        CustomRangePanel.Visibility = RangeBox.SelectedIndex == 6 ? Visibility.Visible : Visibility.Collapsed;
        var percent = Metric == 4;
        ((ComboBoxItem)MinimumBox.Items[1]).Content = percent ? "Peak ≥ 10%" : "Peak ≥ 100 MB";
        ((ComboBoxItem)MinimumBox.Items[2]).Content = percent ? "Peak ≥ 50%" : "Peak ≥ 500 MB";
        ((ComboBoxItem)MinimumBox.Items[3]).Content = percent ? "Peak ≥ 90%" : "Peak ≥ 1 GB";
        _ = RefreshData();
    }
    private void ListFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyList();
    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyList();
    private void ApplyRange_Click(object sender, RoutedEventArgs e) => _ = RefreshData();
    private void App_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || binding || AppGrid.SelectedItem is not AppRow app || selectedApp == app.Id) return;
        selectedApp = app.Id; _ = RefreshData();
    }
    private void Reset_Click(object sender, RoutedEventArgs e) { selectedApp = null; AppGrid.UnselectAll(); _ = RefreshData(); }
    private void Overview_Click(object sender, RoutedEventArgs e) { OverviewPage.Visibility = Visibility.Visible; SettingsPage.Visibility = Visibility.Collapsed; OverviewNav.SetResourceReference(BackgroundProperty, "AccentSoft"); SettingsNav.Background = Brushes.Transparent; }
    private void Settings_Click(object sender, RoutedEventArgs e) { OverviewPage.Visibility = Visibility.Collapsed; SettingsPage.Visibility = Visibility.Visible; SettingsNav.SetResourceReference(BackgroundProperty, "AccentSoft"); OverviewNav.Background = Brushes.Transparent; UpdateStorage(); }
    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        PauseButton.IsEnabled = false;
        try
        {
            if (status == "OFFLINE") { Program.Launch("--background"); await Task.Delay(800); }
            else await Task.Run(() => Program.Send(status.StartsWith("PAUSED") ? "RESUME" : "PAUSE"));
            await RefreshData();
        }
        finally { PauseButton.IsEnabled = true; }
    }
    private void SaveSettings()
    {
        if (!ready) return;
        try
        {
            var fresh = Settings.Load(Program.Folder);
            fresh.Theme = ((ComboBoxItem)ThemeBox.SelectedItem).Content.ToString()!;
            fresh.IntervalSeconds = new[] { 10, 15, 30, 60, 120 }[IntervalBox.SelectedIndex];
            fresh.RetentionDays = new[] { 0, 7, 30, 90 }[RetentionBox.SelectedIndex];
            fresh.CompactHistory = CompactCheck.IsChecked == true;
            fresh.Save(Program.Folder); settings = fresh;
            _ = Task.Run(() => Program.Send("RELOAD"));
            SettingsFeedback.Text = "Settings saved.";
        }
        catch (Exception e) { SettingsFeedback.Text = "Could not save settings: " + e.Message; }
    }
    private void Theme_Changed(object sender, SelectionChangedEventArgs e) { if (!ready) return; SaveSettings(); ApplyTheme(); }
    private void Compact_Changed(object sender, RoutedEventArgs e) => SaveSettings();
    private void ApplyGraphVisibility()
    {
        Chart.Visibility = settings.GraphExpanded ? Visibility.Visible : Visibility.Collapsed;
        ChartHint.Visibility = settings.GraphExpanded ? Visibility.Visible : Visibility.Collapsed;
        GraphToggle.Content = settings.GraphExpanded ? "Hide graph ⌃" : "Show graph ⌄";
    }
    private void GraphToggle_Click(object sender, RoutedEventArgs e)
    {
        settings = Settings.Load(Program.Folder); settings.GraphExpanded = !settings.GraphExpanded;
        settings.Save(Program.Folder); ApplyGraphVisibility();
    }
    private void SettingsValue_Changed(object sender, SelectionChangedEventArgs e) { SaveSettings(); if (ready) _ = RefreshData(); }
    private void Startup_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        try { Program.SetStartup(StartupCheck.IsChecked == true); SettingsFeedback.Text = StartupCheck.IsChecked == true ? "RamTrace will start quietly at your next Windows sign-in." : "Automatic startup is off."; }
        catch (Exception ex) { SettingsFeedback.Text = "Could not change startup: " + ex.Message; ready = false; StartupCheck.IsChecked = Program.StartupEnabled(); ready = true; }
    }
    private void UpdateStorage()
    {
        long bytes = Directory.EnumerateFiles(Program.Folder, "history.db*").Sum(f => new FileInfo(f).Length);
        StorageLabel.Text = Format.Bytes(bytes) + " on disk\n" + Program.Folder;
        try
        {
            var devices = System.Text.Json.JsonSerializer.Deserialize<List<GpuDevice>>(File.ReadAllText(Path.Combine(Program.Folder, "gpu-devices.json")));
            GpuDevicesLabel.Text = string.Join("\n", (devices ?? new()).Select(d => d.Name + "  ·  VRAM " + Format.Bytes(d.Dedicated) + (d.Capacity > 0 ? " / " + Format.Bytes(d.Capacity) : "") + "  ·  shared " + Format.Bytes(d.Shared)));
        }
        catch (IOException) { GpuDevicesLabel.Text = "Waiting for GPU counters…"; }
        catch (System.Text.Json.JsonException) { }
    }
    private void DataFolder_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("explorer.exe", Program.Folder) { UseShellExecute = true });
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Title = "Export filtered memory history" + (selectedApp.HasValue ? " · selected app" : ""), FileName = "RamTrace-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".csv", Filter = "CSV files (*.csv)|*.csv", DefaultExt = ".csv", AddExtension = true };
        if (dlg.ShowDialog(this) != true) return;
        var f = from; var t = to; var p = Metric; var q = SearchBox.Text; var id = selectedApp; var active = ActivityBox.SelectedIndex == 1; var min = Minimum;
        try { await Task.Run(() => { using var db = new History(Program.Folder); db.Export(dlg.FileName, f, t, p, q, id, active, min); }); Footer.Text = "Export saved: " + dlg.FileName; }
        catch (Exception ex) { Footer.Text = "Export failed: " + ex.Message; }
    }
}
