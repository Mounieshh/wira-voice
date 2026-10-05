using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace WiraVoice.Core.Automation;

/// <summary>
/// Deep module that captures the active foreground window, recursively walks the Microsoft UI Automation 3
/// accessibility tree, prunes layout noise, and yields interactive control candidates.
/// </summary>
public sealed class UiaReader : IUiaReader
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private readonly UIA3Automation _automation = new();
    private bool _disposed = false;

    public Task<UiSceneSnapshot> CaptureActiveSceneAsync(int maxNodes = 1500, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return new UiSceneSnapshot(IntPtr.Zero, 0, "Unknown", "No Active Window", Array.Empty<UiControlCandidate>());
            }

            GetWindowThreadProcessId(hwnd, out uint pid);
            string processName = "Unknown";
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                processName = proc.ProcessName;
            }
            catch { }

            AutomationElement? rootElement = null;
            try
            {
                rootElement = _automation.FromHandle(hwnd);
            }
            catch
            {
                return new UiSceneSnapshot(hwnd, (int)pid, processName, "Unavailable", Array.Empty<UiControlCandidate>());
            }

            if (rootElement == null)
            {
                return new UiSceneSnapshot(hwnd, (int)pid, processName, "Unavailable", Array.Empty<UiControlCandidate>());
            }

            string windowTitle = rootElement.Name ?? processName;
            bool isSensitiveWindow = windowTitle.Contains(".env", StringComparison.OrdinalIgnoreCase);

            var candidates = new List<UiControlCandidate>();
            var queue = new Queue<AutomationElement>();
            queue.Enqueue(rootElement);

            int visited = 0;
            int candidateIndex = 0;

            while (queue.Count > 0 && visited < maxNodes)
            {
                ct.ThrowIfCancellationRequested();
                var current = queue.Dequeue();
                visited++;

                try
                {
                    // Basic visibility and bounding check
                    var bounds = current.BoundingRectangle;
                    if (bounds.IsEmpty || bounds.Width <= 1 || bounds.Height <= 1)
                    {
                        // Some offscreen elements still contain visible children; continue traversal
                        EnqueueChildren(current, queue);
                        continue;
                    }

                    var controlType = current.ControlType;
                    string name = current.Name ?? string.Empty;
                    string help = current.HelpText ?? string.Empty;
                    bool isEnabled = current.IsEnabled;

                    if (isEnabled && IsInteractive(controlType, name, current))
                    {
                        string op = DetermineOperation(controlType);
                        string id = $"n{candidateIndex}_{op}";

                        if (isSensitiveWindow && (controlType == ControlType.Edit || controlType == ControlType.Document))
                        {
                            name = "[REDACTED SENSITIVE EDITOR]";
                        }

                        var candidate = new UiControlCandidate(
                            Id: id,
                            Name: string.IsNullOrWhiteSpace(name) ? controlType.ToString() : name,
                            Role: controlType.ToString(),
                            Help: help,
                            BoundingBox: new Rectangle((int)bounds.Left, (int)bounds.Top, (int)bounds.Width, (int)bounds.Height),
                            SupportedOperation: op,
                            NativeElement: current
                        );

                        candidates.Add(candidate);
                        candidateIndex++;
                    }

                    EnqueueChildren(current, queue);
                }
                catch
                {
                    // Stale or protected elements are safely skipped
                }
            }

            return new UiSceneSnapshot(hwnd, (int)pid, processName, windowTitle, candidates);
        }, ct);
    }

    private static void EnqueueChildren(AutomationElement parent, Queue<AutomationElement> queue)
    {
        try
        {
            var children = parent.FindAllChildren();
            foreach (var child in children)
            {
                queue.Enqueue(child);
            }
        }
        catch { }
    }

    private static bool IsInteractive(ControlType controlType, string name, AutomationElement element)
    {
        // Explicitly interactive control types
        if (controlType == ControlType.Button ||
            controlType == ControlType.SplitButton ||
            controlType == ControlType.Edit ||
            controlType == ControlType.Document ||
            controlType == ControlType.CheckBox ||
            controlType == ControlType.RadioButton ||
            controlType == ControlType.MenuItem ||
            controlType == ControlType.TabItem ||
            controlType == ControlType.ListItem ||
            controlType == ControlType.TreeItem ||
            controlType == ControlType.ComboBox ||
            controlType == ControlType.Hyperlink ||
            controlType == ControlType.Slider)
        {
            return true;
        }

        // Custom or Group controls with invoke patterns or names
        if ((controlType == ControlType.Custom || controlType == ControlType.Pane) &&
            !string.IsNullOrWhiteSpace(name) &&
            element.Patterns.Invoke.IsSupported)
        {
            return true;
        }

        return false;
    }

    private static string DetermineOperation(ControlType controlType)
    {
        if (controlType == ControlType.Edit || controlType == ControlType.Document)
            return "typeText";

        if (controlType == ControlType.Slider)
            return "setNumber";

        if (controlType == ControlType.ComboBox ||
            controlType == ControlType.TabItem ||
            controlType == ControlType.ListItem ||
            controlType == ControlType.TreeItem)
            return "select";

        return "pressClick";
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _automation.Dispose();
            _disposed = true;
        }
    }
}
