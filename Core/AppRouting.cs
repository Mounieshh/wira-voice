using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;
using WiraVoice.Core.Automation;
using WiraVoice.Core.Commands;

namespace WiraVoice.Core;

public sealed class InstalledApplication : IEquatable<InstalledApplication>
{
    public string Name { get; }
    public string BundleID { get; }
    public string Url { get; }
    public List<string> Aliases { get; }

    public InstalledApplication(string name, string bundleID, string url, List<string>? aliases = null)
    {
        Name = name;
        BundleID = bundleID;
        Url = url;
        var list = new List<string> { name, Path.GetFileNameWithoutExtension(url) };
        if (aliases != null) list.AddRange(aliases);
        Aliases = list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool Equals(InstalledApplication? other)
    {
        if (other is null) return false;
        return string.Equals(BundleID, other.BundleID, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals(object? obj) => obj is InstalledApplication other && Equals(other);
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(BundleID);
}

/// <summary>
/// Discovers installed applications, default browser, and handles activation on Windows.
/// Exact port of Computah's AppRouting.swift adapted for Windows.
/// </summary>
public static class AppRouting
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public static List<InstalledApplication> Installed()
    {
        var result = new Dictionary<string, InstalledApplication>(StringComparer.OrdinalIgnoreCase);

        // 1. Scan Start Menu Programs (System & User)
        var startMenuFolders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs)
        };

        foreach (var folder in startMenuFolders)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
            try
            {
                var files = Directory.GetFiles(folder, "*.lnk", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var id = name.ToLowerInvariant();
                    if (!result.ContainsKey(id))
                    {
                        result[id] = new InstalledApplication(name, id, file);
                    }
                }
            }
            catch { }
        }

        // 2. Scan Registry App Paths
        var appPathsKeys = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths"
        };

        foreach (var subKey in appPathsKeys)
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey(subKey);
            if (baseKey == null) continue;
            foreach (var appKeyName in baseKey.GetSubKeyNames())
            {
                try
                {
                    using var appKey = baseKey.OpenSubKey(appKeyName);
                    var exePath = appKey?.GetValue(null) as string;
                    if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                    {
                        var name = Path.GetFileNameWithoutExtension(appKeyName);
                        var id = name.ToLowerInvariant();
                        if (!result.ContainsKey(id))
                        {
                            result[id] = new InstalledApplication(name, id, exePath, [appKeyName]);
                        }
                    }
                }
                catch { }
            }
        }

        // 3. Running processes
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (proc.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(proc.MainWindowTitle))
                    {
                        var name = proc.ProcessName;
                        var id = name.ToLowerInvariant();
                        if (!result.ContainsKey(id))
                        {
                            string exePath = "";
                            try { exePath = proc.MainModule?.FileName ?? ""; } catch { }
                            result[id] = new InstalledApplication(name, id, exePath);
                        }
                    }
                }
                catch { }
            }
        }
        catch { }

        return result.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static InstalledApplication? DefaultBrowser()
    {
        try
        {
            using var userChoiceKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            var progId = userChoiceKey?.GetValue("ProgId") as string;
            if (string.IsNullOrEmpty(progId))
            {
                progId = "MSEdgeHTM"; // Default to Edge if not set
            }

            using var cmdKey = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
            var cmd = cmdKey?.GetValue(null) as string;
            string exePath = "";
            if (!string.IsNullOrEmpty(cmd))
            {
                var trimmed = cmd.Trim();
                if (trimmed.StartsWith('"'))
                {
                    var endQuote = trimmed.IndexOf('"', 1);
                    if (endQuote > 1) exePath = trimmed.Substring(1, endQuote - 1);
                }
                else
                {
                    var space = trimmed.IndexOf(' ');
                    exePath = space > 0 ? trimmed.Substring(0, space) : trimmed;
                }
            }

            var name = "Browser";
            if (progId.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) name = "Google Chrome";
            else if (progId.Contains("MSEdge", StringComparison.OrdinalIgnoreCase)) name = "Microsoft Edge";
            else if (progId.Contains("Firefox", StringComparison.OrdinalIgnoreCase)) name = "Mozilla Firefox";
            else if (progId.Contains("Brave", StringComparison.OrdinalIgnoreCase)) name = "Brave Browser";
            else if (!string.IsNullOrEmpty(exePath)) name = Path.GetFileNameWithoutExtension(exePath);

            return new InstalledApplication(name, progId.ToLowerInvariant(), exePath, ["Registered default browser", name]);
        }
        catch
        {
            return new InstalledApplication("Microsoft Edge", "msedge", "msedge.exe", ["Registered default browser"]);
        }
    }

    public static async Task<bool> ActivateAsync(InstalledApplication app, InputPermit permit = default)
    {
        try
        {
            permit.Check();
            // Check if already running
            Process? runningProc = null;
            var targetName = Path.GetFileNameWithoutExtension(app.Url);

            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (string.Equals(proc.ProcessName, targetName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(proc.ProcessName, app.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        if (proc.MainWindowHandle != IntPtr.Zero)
                        {
                            runningProc = proc;
                            break;
                        }
                    }
                }
                catch { }
            }

            if (runningProc != null)
            {
                SetForegroundWindow(runningProc.MainWindowHandle);
            }
            else
            {
                using var lease = permit.Begin(effect: true);
                var psi = new ProcessStartInfo
                {
                    FileName = app.Url,
                    UseShellExecute = true
                };
                Process.Start(psi);
            }

            for (int i = 0; i < 20; i++)
            {
                permit.Check();
                var fgHwnd = GetForegroundWindow();
                if (fgHwnd != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(fgHwnd, out var fgPid);
                    try
                    {
                        var fgProc = Process.GetProcessById((int)fgPid);
                        if (string.Equals(fgProc.ProcessName, targetName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(fgProc.ProcessName, app.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                    catch { }
                }
                await Task.Delay(100).ConfigureAwait(false);
            }
            return false;
        }
        catch
        {
            return false;
        }
    }
}
