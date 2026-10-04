using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using RamTrace.Core;

namespace RamTrace;

internal static class Installer
{
    public static void Install()
    {
        var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "RamTrace");
        Directory.CreateDirectory(target);
        var exe = Path.Combine(target, "RamTrace.exe");
        if (!string.Equals(Environment.ProcessPath, exe, StringComparison.OrdinalIgnoreCase))
        {
            // An installed collector is asked to quit so a repeat install can replace the executable.
            Program.Send("EXIT");
            foreach (var name in new[] { "RamTrace.exe", "RamTrace.dll", "RamTrace.Core.dll", "RamTrace.runtimeconfig.json", "RamTrace.deps.json" })
            {
                var source = Path.Combine(AppContext.BaseDirectory, name);
                if (File.Exists(source)) File.Copy(source, Path.Combine(target, name), true);
            }
        }
        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) key.SetValue("RamTrace", "\"" + exe + "\" --background");
        if (!File.Exists(Path.Combine(Program.Folder, "settings.json"))) new Settings().Save(Program.Folder);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(Path.Combine(programs, "RamTrace.lnk"));
            shortcut.TargetPath = exe; shortcut.WorkingDirectory = target; shortcut.Description = "RAM, GPU and VRAM history"; shortcut.IconLocation = exe + ",0"; shortcut.Save();
            Marshal.FinalReleaseComObject(shortcut);
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }
}
