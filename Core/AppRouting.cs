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
    public string AppUserModelID { get; }
    public string BundleID => AppUserModelID;
    public string Url { get; }
    public List<string> Aliases { get; }

    public InstalledApplication(string name, string appUserModelID, string url, List<string>? aliases = null)
    {
        Name = name;
        AppUserModelID = appUserModelID;
        Url = url;
        var list = new List<string> { name, Path.GetFileNameWithoutExtension(url), appUserModelID };
        if (aliases != null) list.AddRange(aliases);
        Aliases = list.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool MatchesName(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (string.Equals(candidate, Name, StringComparison.OrdinalIgnoreCase)) return true;
        return Aliases.Any(a => string.Equals(a, candidate, StringComparison.OrdinalIgnoreCase));
    }

    public bool MatchesWindowTitle(string windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle)) return false;
        if (windowTitle.Contains(Name, StringComparison.OrdinalIgnoreCase)) return true;
        return Aliases.Any(a => a.Length >= 4 && windowTitle.Contains(a, StringComparison.OrdinalIgnoreCase));
    }

    public bool Equals(InstalledApplication? other)
    {
        if (other is null) return false;
        return string.Equals(AppUserModelID, other.AppUserModelID, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals(object? obj) => obj is InstalledApplication other && Equals(other);
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(AppUserModelID);
}

/// <summary>
/// Discovers installed applications, default browser, and handles window activation on Windows.
/// Native Windows implementation using AppUserModelID (AUMID) and PE FileDescription.
/// </summary>
public static class AppRouting
{

    public static List<InstalledApplication> Installed()
    {
        var result = new Dictionary<string, InstalledApplication>(StringComparer.OrdinalIgnoreCase);

        // 1. Shell AppsFolder (Win32, UWP, and MSIX applications with authentic AUMIDs)
        try
        {
            foreach (var kvp in WindowsAppIdentity.ShellAppsCatalog)
            {
                var name = kvp.Key;
                var aumid = kvp.Value;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(aumid)) continue;

                var id = name.ToLowerInvariant();
                if (!result.ContainsKey(id))
                {
                    string target = aumid;
                    if (File.Exists(aumid))
                    {
                        var peDesc = WindowsAppIdentity.GetFriendlyAppNameFromPath(aumid);
                        if (peDesc != "Unknown") name = peDesc;
                    }
                    else
                    {
                        target = $"shell:AppsFolder\\{aumid}";
                    }
                    result[id] = new InstalledApplication(name, aumid, target, [name, aumid]);
                }
            }
        }
        catch { }

        // 2. Scan Start Menu Programs (System & User shortcuts)
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

        // 3. Scan Registry App Paths
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
                        var peName = WindowsAppIdentity.GetFriendlyAppNameFromPath(exePath);
                        var name = peName != "Unknown" ? peName : Path.GetFileNameWithoutExtension(appKeyName);
                        var id = Path.GetFileNameWithoutExtension(appKeyName).ToLowerInvariant();
                        if (!result.ContainsKey(id) && !result.ContainsKey(name.ToLowerInvariant()))
                        {
                            result[id] = new InstalledApplication(name, id, exePath, [appKeyName, name]);
                        }
                    }
                }
                catch { }
            }
        }

        // 4. Running processes with active top-level windows
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (proc.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(proc.MainWindowTitle))
                    {
                        var friendly = WindowsAppIdentity.GetFriendlyAppName(proc);
                        var aumid = WindowsAppIdentity.GetAppUserModelId(proc.MainWindowHandle, proc.Id);
                        var id = friendly.ToLowerInvariant();
                        if (!result.ContainsKey(id))
                        {
                            string exePath = "";
                            try { exePath = proc.MainModule?.FileName ?? ""; } catch { }
                            result[id] = new InstalledApplication(friendly, aumid, exePath, [proc.ProcessName, friendly]);
                        }
                    }
                }
                catch { }
            }
        }
        catch { }

        // 5. Ensure File Explorer is canonically pinned to explorer.exe with full domain aliases
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var explorerExe = Path.Combine(winDir, "explorer.exe");
        if (File.Exists(explorerExe))
        {
            var explorerApp = new InstalledApplication("File Explorer", "explorer", explorerExe,
                ["Windows Explorer", "explorer", "files", "folder", "folders", "volume", "volumes", "directory", "Microsoft.Windows.Explorer"]);
            result["file explorer"] = explorerApp;
            result["explorer"] = explorerApp;
        }

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

            var name = !string.IsNullOrEmpty(exePath) ? WindowsAppIdentity.GetFriendlyAppNameFromPath(exePath) : "Browser";
            if (name == "Unknown") name = "Browser";
            var aumid = !string.IsNullOrEmpty(exePath) ? Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant() : progId.ToLowerInvariant();

            return new InstalledApplication(name, aumid, exePath, ["Registered default browser", name, progId]);
        }
        catch
        {
            return new InstalledApplication("Microsoft Edge", "msedge", "msedge.exe", ["Registered default browser"]);
        }
    }

    public static bool IsSameApp(string? appOrProcessName, string? windowTitle, InstalledApplication? app)
    {
        if (app == null) return false;
        return WindowsAppIdentity.IsSameApplication(appOrProcessName, appOrProcessName, windowTitle, app);
    }

    public static bool IsSameApp(string? friendlyName, string? processName, string? windowTitle, InstalledApplication? app, string? aumid = null)
    {
        if (app == null) return false;
        return WindowsAppIdentity.IsSameApplication(friendlyName, processName, windowTitle, app, aumid);
    }

    public static bool IsSameApp(AXSnapshot? snapshot, InstalledApplication? app)
    {
        if (snapshot == null || app == null) return false;
        if (!string.IsNullOrEmpty(snapshot.AppUserModelID) &&
            (string.Equals(snapshot.AppUserModelID, app.AppUserModelID, StringComparison.OrdinalIgnoreCase) ||
             app.MatchesName(snapshot.AppUserModelID)))
        {
            return true;
        }
        return WindowsAppIdentity.IsSameApplication(snapshot.AppName, snapshot.AppName, snapshot.WindowTitle, app, snapshot.AppUserModelID);
    }

    public static async Task<bool> ActivateAsync(InstalledApplication app, InputPermit permit = default)
    {
        try
        {
            permit.Check();

            // Locate existing top-level window without launching a duplicate process
            IntPtr existingHwnd = WindowsAppIdentity.FindExistingTopLevelWindow(app);

            if (existingHwnd != IntPtr.Zero)
            {
                using var lease = permit.Begin(effect: true);
                if (WindowsAppIdentity.IsIconic(existingHwnd))
                {
                    WindowsAppIdentity.ShowWindow(existingHwnd, WindowsAppIdentity.SW_RESTORE);
                }
                WindowsAppIdentity.SetForegroundWindow(existingHwnd);
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

            for (int i = 0; i < 35; i++)
            {
                permit.Check();
                var fgHwnd = WindowsAppIdentity.GetForegroundWindow();
                if (fgHwnd != IntPtr.Zero)
                {
                    WindowsAppIdentity.GetWindowThreadProcessId(fgHwnd, out var fgPid);
                    try
                    {
                        var fgFriendly = WindowsAppIdentity.GetFriendlyAppName((int)fgPid);
                        var fgProcName = Process.GetProcessById((int)fgPid).ProcessName;
                        string fgTitle = WindowsAppIdentity.GetWindowTitle(fgHwnd);
                        var fgAumid = WindowsAppIdentity.GetAppUserModelId(fgHwnd, (int)fgPid);

                        if (WindowsAppIdentity.IsSameApplication(fgFriendly, fgProcName, fgTitle, app, fgAumid))
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
