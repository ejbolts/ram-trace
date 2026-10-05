# RamTrace

A native Windows tray app for RAM, GPU and VRAM history. Samples every **10 seconds** and keeps **7 days** of history by default. Existing saved retention choices are preserved.

## Use

Run `RamTrace.exe`. Open it again, or double-click its tray icon, to show the dashboard. Closing the dashboard releases its separate UI process; the collector keeps running. Right-click the tray icon to pause/resume or quit completely.

- Search executable names and filter by activity, peak usage and time range.
- Choose resident RAM, private committed memory, dedicated GPU memory, shared GPU memory, or GPU activity.
- Click an app to see its history; use **System total** to return to the overall graph.
- Hide the graph for a longer app list. Hover the expanded graph for values and timestamps.
- Click a column heading to sort it.
- Export CSV uses the selected range, metric, search and filters. If an app is selected, it exports that app only.
- Settings offers System/Light/Dark appearance, startup, sampling interval, and retention.

Expired history is removed at startup and during hourly maintenance while logging. Cleanup reclaims unused database pages even when summarization is off. The temporary SQLite write journal is normally kept near 1 MB; long-running readers or large maintenance transactions can temporarily exceed that target. The storage figure includes the database and its journal files. For fewer records, choose a longer sample interval (60 seconds records one sixth as many samples as 10 seconds).

**Forever** has no automatic expiry. By default, samples older than 48 hours are summarized into five-minute weighted averages and sampled peaks, preserving the original peak timestamps. Turn off **Summarize history after 48 hours** to retain every individual sample going forward. Summarization already performed cannot be reversed. Forever history continues to use more disk space over time.

## Install and startup

Run `RamTrace.exe --install` (or `Install-RamTrace.cmd`). This copies the app to `%LOCALAPPDATA%\Programs\RamTrace`, adds a Start menu shortcut, enables startup for the current Windows user, and starts the app. No administrator rights are needed. Startup runs silently in the tray at Windows sign-in, rather than before login. Windows may delay startup, and Task Manager can disable startup entries independently.

The executable uses the **.NET 8 Windows Desktop Runtime (x64)**, already available on the computer where this build was made. Windows 10/11 x64 is supported; GPU counters require driver support. The build is unsigned.

## Data and measurements

Data is in `%LOCALAPPDATA%\RamTrace\history.db` (SQLite). Settings and a small current GPU inventory are alongside it. No network service is used. Only executable names, process counts, timestamps, memory/activity readings and GPU model information are recorded. There are no window titles or command lines in history. Error logs are limited to approximately 1 MB plus one previous log.

Working set includes resident shared pages; private bytes is committed memory, which can be paged out. App totals can overlap and do not sum to system RAM. Executables with the same name are grouped together. Processes that exit during sampling or cannot be read may be skipped. Ten-second polling can miss shorter spikes. Sleep, pause, stopped logging and unavailable counters produce gaps.

Dedicated GPU memory is VRAM. Shared GPU memory uses system RAM. GPU charts and app values combine all adapters; Settings shows current memory by adapter. Per-process GPU counters may overcount shared allocations and can differ from Task Manager. GPU activity is an estimate: system activity is the busiest GPU engine; app activity sums each process's busiest engine, capped at 100%. The first activity reading needs a second sample to warm up. Driver-provided data may be unavailable or inaccurate on some Windows/driver combinations.

Values marked MB and GB are binary units (MiB/GiB). Average usage is weighted by recorded observations while an app was seen, not elapsed wall time; the graph includes zero when a sampled app is absent. Older summarized buckets have five-minute time resolution. CSV includes observation counts, bucket duration, and ISO 8601 timestamps with local UTC offsets.

## Back up or remove

Quit from the tray before copying the data folder so no writes are in progress. For a live backup use SQLite's backup API; do not copy only `history.db` while logging because recent samples may be in its WAL file.

To remove the app: turn off startup in Settings, close the dashboard, quit from the tray, delete `%LOCALAPPDATA%\Programs\RamTrace` and the `RamTrace` Start menu shortcut. History in `%LOCALAPPDATA%\RamTrace` remains unless you separately delete it.

## Build and checks

Requires the .NET 8 SDK or a newer SDK capable of targeting .NET 8, on Windows x64. No external NuGet packages are needed.

```powershell
dotnet build App/RamTrace.csproj -c Release
dotnet build Checks/RamTrace.Checks.csproj -c Release
& ./Checks/bin/Release/net8.0/RamTrace.Checks.exe "$PWD/check-run"
dotnet publish App/RamTrace.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

Give every check run a fresh folder. The live GPU checks expect GPU performance counters on the test machine. `RamTrace.exe --data-dir <absolute-test-directory>` isolates test history, settings, mutexes and pipes, disables startup changes, and labels the dashboard as a test workspace. `--background`, `--quit`, `--pause`, and `--resume` are supported for local operation and integration checks.

The collector is a WinForms tray process using Windows process counters, GlobalMemoryStatusEx, PDH GPU counters and built-in Windows SQLite. The WPF dashboard is a separate process and exits when its window closes. Graphs are drawn natively with at most 360 aggregate points; rows are virtualized. No browser engine is kept in memory.

API references: [Windows memory counters](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-process_memory_counters_ex), [GPU metrics](https://devblogs.microsoft.com/directx/gpus-in-the-task-manager/), [GPU counter limitations](https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/gpu-process-memory-counters-report-wrong-value), [PDH arrays](https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdh-pdhgetformattedcounterarrayw), [sign-in startup](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys).
