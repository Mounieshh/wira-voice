using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace WiraVoice.Core.Automation;

/// <summary>
/// Native Windows application identity resolver and window management module.
/// Extracts human-readable application names directly from PE binary headers
/// (FileVersionInfo.FileDescription), demuxes UWP ApplicationFrameHost containers,
/// filters out Windows desktop shell infrastructure windows (Taskbar, Desktop),
/// and maps Windows AppUserModelIDs (AUMID) without hardcoded application dictionaries.
/// </summary>
public static class WindowsAppIdentity
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    internal const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetApplicationUserModelId(IntPtr hProcess, ref uint length, StringBuilder sb);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>
    /// Retrieves the window text / caption for a given HWND.
    /// </summary>
    public static string GetWindowTitle(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return "";
        int len = GetWindowTextLength(hWnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hWnd, sb, len + 1);
        return sb.ToString();
    }

    private static readonly Lazy<Dictionary<string, string>> FriendlyToAumidCache = new(BuildShellAppsCatalog);
    private static readonly Lazy<Dictionary<string, string>> AumidToFriendlyCache = new(() =>
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in FriendlyToAumidCache.Value)
        {
            if (!dict.ContainsKey(kvp.Value))
            {
                dict[kvp.Value] = kvp.Key;
            }
        }
        return dict;
    });

    private static Dictionary<string, string> BuildShellAppsCatalog()
    {
        var catalog = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType != null)
            {
                dynamic? shell = Activator.CreateInstance(shellType);
                if (shell != null)
                {
                    var folder = shell.NameSpace("shell:AppsFolder");
                    if (folder != null)
                    {
                        foreach (var item in folder.Items())
                        {
                            try
                            {
                                string name = (string)item.Name;
                                string path = (string)item.Path;
                                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(path))
                                {
                                    if (!catalog.ContainsKey(name))
                                    {
                                        catalog[name] = path;
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
        }
        catch { }

        // Standard Windows system defaults if not enumerated
        if (!catalog.ContainsKey("File Explorer")) catalog["File Explorer"] = "explorer";
        if (!catalog.ContainsKey("Windows Explorer")) catalog["Windows Explorer"] = "explorer";

        return catalog;
    }

    /// <summary>
    /// Returns the cached map of friendly application names to AUMIDs.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ShellAppsCatalog => FriendlyToAumidCache.Value;

    /// <summary>
    /// Retrieves the window class name for a given HWND.
    /// </summary>
    public static string GetWindowClassName(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return "";
        var sb = new StringBuilder(256);
        GetClassName(hWnd, sb, 256);
        return sb.ToString();
    }

    /// <summary>
    /// Checks whether the specified window belongs to Windows desktop shell infrastructure
    /// (Taskbar, Desktop icon manager, Wallpaper worker, or Shell flyouts).
    /// </summary>
    public static bool IsShellInfrastructureWindow(IntPtr hWnd, string? className = null)
    {
        var cls = className ?? GetWindowClassName(hWnd);
        if (string.IsNullOrEmpty(cls)) return false;

        if (cls.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
            cls.Equals("Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase) ||
            cls.Equals("Progman", StringComparison.OrdinalIgnoreCase) ||
            cls.Equals("WorkerW", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (cls.Equals("Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase))
        {
            if (hWnd != IntPtr.Zero)
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid != 0)
                {
                    try
                    {
                        using var p = Process.GetProcessById((int)pid);
                        var pName = p.ProcessName;
                        if (pName.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
                            pName.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                            pName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                    catch { }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Checks whether the window is an actual File Explorer folder navigation window.
    /// </summary>
    public static bool IsFileExplorerFolderWindow(IntPtr hWnd, string? className = null)
    {
        var cls = className ?? GetWindowClassName(hWnd);
        bool isFolderClass = cls.Equals("CabinetWClass", StringComparison.OrdinalIgnoreCase) ||
                             cls.Equals("ExploreWClass", StringComparison.OrdinalIgnoreCase);
        if (!isFolderClass) return false;

        if (hWnd != IntPtr.Zero)
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid != 0)
            {
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    if (!p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
                catch { }
            }
        }

        return true;
    }

    /// <summary>
    /// Retrieves the friendly, human-readable application name for a process dynamically
    /// via PE FileVersionInfo.FileDescription, demuxing UWP ApplicationFrameHost containers.
    /// </summary>
    public static string GetFriendlyAppName(Process? proc)
    {
        if (proc == null) return "Unknown";

        // 1. Demux UWP ApplicationFrameHost windows to their caption
        try
        {
            if (proc.ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                if (proc.MainWindowHandle != IntPtr.Zero)
                {
                    var title = GetWindowTitle(proc.MainWindowHandle);
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        return title.Trim();
                    }
                }

                // If MainWindowHandle is 0 (frequent in UWP), resolve via active foreground window if PID matches
                var fgHwnd = GetForegroundWindow();
                if (fgHwnd != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(fgHwnd, out var fgPid);
                    if (fgPid == proc.Id)
                    {
                        var fgTitle = GetWindowTitle(fgHwnd);
                        if (!string.IsNullOrWhiteSpace(fgTitle))
                        {
                            return fgTitle.Trim();
                        }
                    }
                }
            }
        }
        catch { }

        // 2. Direct FileDescription from MainModule
        try
        {
            var desc = proc.MainModule?.FileVersionInfo?.FileDescription;
            if (!string.IsNullOrWhiteSpace(desc))
            {
                return desc.Trim();
            }
        }
        catch { }

        // 3. Query full image path (works across bitness boundaries)
        try
        {
            var imagePath = GetProcessImagePath(proc.Id);
            if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
            {
                var desc = FileVersionInfo.GetVersionInfo(imagePath)?.FileDescription;
                if (!string.IsNullOrWhiteSpace(desc))
                {
                    return desc.Trim();
                }
            }
        }
        catch { }

        // 4. Check AUMID from packaged app
        try
        {
            var aumid = GetPackagedAumid(proc.Id);
            if (!string.IsNullOrWhiteSpace(aumid) && AumidToFriendlyCache.Value.TryGetValue(aumid, out var friendly))
            {
                return friendly;
            }
        }
        catch { }

        // 5. Fallback to process name
        try
        {
            return proc.ProcessName;
        }
        catch
        {
            return "Unknown";
        }
    }

    /// <summary>
    /// Retrieves the friendly, human-readable application name for a PID dynamically.
    /// </summary>
    public static string GetFriendlyAppName(int pid)
    {
        if (pid <= 0) return "Unknown";
        try
        {
            using var proc = Process.GetProcessById(pid);
            return GetFriendlyAppName(proc);
        }
        catch
        {
            return "Unknown";
        }
    }

    /// <summary>
    /// Retrieves the friendly application name from an executable path or shortcut.
    /// </summary>
    public static string GetFriendlyAppNameFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Unknown";

        try
        {
            if (File.Exists(path))
            {
                var desc = FileVersionInfo.GetVersionInfo(path)?.FileDescription;
                if (!string.IsNullOrWhiteSpace(desc))
                {
                    return desc.Trim();
                }
            }
        }
        catch { }

        return Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>
    /// Retrieves the canonical AppUserModelID (AUMID) for a running window and process.
    /// </summary>
    public static string GetAppUserModelId(IntPtr hWnd, int pid)
    {
        if (pid <= 0) return "";

        // 1. Check packaged AUMID via kernel32
        try
        {
            var packaged = GetPackagedAumid(pid);
            if (!string.IsNullOrWhiteSpace(packaged))
            {
                return packaged;
            }
        }
        catch { }

        // 2. Check friendly name mapping from shell:AppsFolder catalog
        try
        {
            var friendly = GetFriendlyAppName(pid);
            if (!string.IsNullOrWhiteSpace(friendly) && FriendlyToAumidCache.Value.TryGetValue(friendly, out var aumid))
            {
                return aumid;
            }
        }
        catch { }

        // 3. Fallback to process name
        try
        {
            using var proc = Process.GetProcessById(pid);
            return proc.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Locates an existing top-level window (HWND) for the specified application.
    /// Excludes Windows Shell infrastructure elements (Taskbar, Desktop).
    /// Restores minimized windows without launching duplicate instances.
    /// </summary>
    public static IntPtr FindExistingTopLevelWindow(InstalledApplication app)
    {
        IntPtr foundHwnd = IntPtr.Zero;

        bool isExplorerTarget = app.Name.Equals("File Explorer", StringComparison.OrdinalIgnoreCase) ||
                                app.AppUserModelID.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
                                app.AppUserModelID.Equals("Microsoft.Windows.Explorer", StringComparison.OrdinalIgnoreCase) ||
                                app.Aliases.Any(a => a.Equals("explorer", StringComparison.OrdinalIgnoreCase) || a.Equals("files", StringComparison.OrdinalIgnoreCase));

        EnumWindows((hWnd, lParam) =>
        {
            // Only consider windows that are visible or minimized
            if (!IsWindowVisible(hWnd) && !IsIconic(hWnd)) return true;

            var className = GetWindowClassName(hWnd);

            // Filter out shell infrastructure windows (Taskbar, Desktop icons, etc.)
            if (IsShellInfrastructureWindow(hWnd, className)) return true;

            string title = GetWindowTitle(hWnd);

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0) return true;

            // For File Explorer: must be a genuine folder navigation window (CabinetWClass or ExploreWClass)
            if (isExplorerTarget)
            {
                if (IsFileExplorerFolderWindow(hWnd, className))
                {
                    foundHwnd = hWnd;
                    return false; // Found genuine File Explorer folder window!
                }
                return true;
            }

            try
            {
                using var proc = Process.GetProcessById((int)pid);
                string procName = proc.ProcessName;
                string friendly = GetFriendlyAppName(proc);

                // If hosted under ApplicationFrameHost (UWP), caption is the friendly app name
                if (procName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(title))
                {
                    friendly = title;
                }

                if (IsSameApplication(friendly, procName, title, app))
                {
                    foundHwnd = hWnd;
                    return false; // Found matching window, stop enumeration
                }
            }
            catch { }

            return true;
        }, IntPtr.Zero);

        return foundHwnd;
    }

    /// <summary>
    /// Compares an observed application name, process name, and window title against an InstalledApplication.
    /// Supports UWP ApplicationFrameHost demuxing and dynamic identity resolution without hardcoded lookup tables.
    /// </summary>
    public static bool IsSameApplication(string? candidateFriendlyName, string? candidateProcName, string? windowTitle, InstalledApplication? app)
    {
        return IsSameApplication(candidateFriendlyName, candidateProcName, windowTitle, app, null);
    }

    /// <summary>
    /// Compares an observed application name, process name, window title, and optional AUMID against an InstalledApplication.
    /// Supports UWP ApplicationFrameHost demuxing and dynamic identity resolution without hardcoded lookup tables.
    /// </summary>
    public static bool IsSameApplication(string? candidateFriendlyName, string? candidateProcName, string? windowTitle, InstalledApplication? app, string? candidateAumid)
    {
        if (app == null) return false;

        // 0. Direct AUMID match
        if (!string.IsNullOrEmpty(candidateAumid))
        {
            if (string.Equals(candidateAumid, app.AppUserModelID, StringComparison.OrdinalIgnoreCase) ||
                app.MatchesName(candidateAumid))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(app.AppUserModelID) &&
                (app.AppUserModelID.StartsWith(candidateAumid, StringComparison.OrdinalIgnoreCase) ||
                 candidateAumid.StartsWith(app.AppUserModelID, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        // 1. Support for UWP applications hosted under ApplicationFrameHost
        if (!string.IsNullOrEmpty(candidateProcName) &&
            candidateProcName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(windowTitle))
        {
            if (app.MatchesName(windowTitle) || app.MatchesWindowTitle(windowTitle))
            {
                return true;
            }
        }

        // 2. Direct match on friendly application name (e.g. "Google Chrome", "Windows Explorer", "Microsoft Edge", "Camera")
        if (!string.IsNullOrEmpty(candidateFriendlyName) && app.MatchesName(candidateFriendlyName))
        {
            return true;
        }

        // 3. Direct match on process name or AUMID
        if (!string.IsNullOrEmpty(candidateProcName))
        {
            if (string.Equals(candidateProcName, app.AppUserModelID, StringComparison.OrdinalIgnoreCase) ||
                app.MatchesName(candidateProcName))
            {
                return true;
            }

            var urlTarget = Path.GetFileNameWithoutExtension(app.Url);
            if (!string.IsNullOrEmpty(urlTarget) && string.Equals(candidateProcName, urlTarget, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrEmpty(app.AppUserModelID) &&
                (app.AppUserModelID.StartsWith(candidateProcName, StringComparison.OrdinalIgnoreCase) ||
                 candidateProcName.StartsWith(app.AppUserModelID, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        // 4. Substring & space-normalized token matching
        if (!string.IsNullOrEmpty(candidateFriendlyName) && !string.IsNullOrEmpty(app.Name))
        {
            var normCand = candidateFriendlyName.Replace(" ", "").ToLowerInvariant();
            var normApp = app.Name.Replace(" ", "").ToLowerInvariant();
            if (normCand.Contains(normApp) || normApp.Contains(normCand))
                return true;
        }

        if (!string.IsNullOrEmpty(candidateProcName) && !string.IsNullOrEmpty(app.Name))
        {
            var normProc = candidateProcName.Replace(" ", "").ToLowerInvariant();
            var normApp = app.Name.Replace(" ", "").ToLowerInvariant();
            if (normProc.Length >= 3 && (normApp.Contains(normProc) || normProc.Contains(normApp)))
                return true;
        }

        // 5. Window title match
        if (!string.IsNullOrEmpty(windowTitle) && app.MatchesWindowTitle(windowTitle))
        {
            return true;
        }

        return false;
    }

    private static string? GetProcessImagePath(int pid)
    {
        IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProc == IntPtr.Zero) return null;

        try
        {
            var sb = new StringBuilder(1024);
            int capacity = sb.Capacity;
            if (QueryFullProcessImageName(hProc, 0, sb, ref capacity))
            {
                return sb.ToString();
            }
        }
        finally
        {
            CloseHandle(hProc);
        }

        return null;
    }

    private static string? GetPackagedAumid(int pid)
    {
        IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProc == IntPtr.Zero) return null;

        try
        {
            uint length = 256;
            var sb = new StringBuilder(256);
            int hr = GetApplicationUserModelId(hProc, ref length, sb);
            if (hr == 122) // ERROR_INSUFFICIENT_BUFFER
            {
                sb.EnsureCapacity((int)length);
                hr = GetApplicationUserModelId(hProc, ref length, sb);
            }
            if (hr == 0 && length > 0)
            {
                return sb.ToString();
            }
        }
        catch { }
        finally
        {
            CloseHandle(hProc);
        }

        return null;
    }
}
