using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace WiraVoice.Core.Automation;

/// <summary>
/// Deep module that captures the active foreground window, walks Microsoft UI Automation 3,
/// and produces AXSnapshot with native node hierarchies and roles.
/// Equivalent to Computah's AXReader.swift.
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
        return Task.Run(async () =>
        {
            var snapshot = await CaptureAsync(maxNodes, 0.45, null, ct).ConfigureAwait(false);
            var candidates = AXGrouping.Candidates(snapshot).Select(c =>
            {
                var node = snapshot.Nodes[c.NodeID];
                return new UiControlCandidate(
                    Id: c.Id,
                    Name: node.Label,
                    Role: node.Role,
                    Help: node.Help,
                    BoundingBox: node.Frame ?? Rectangle.Empty,
                    SupportedOperation: c.Operation.ToWireString(),
                    NativeElement: node.NativeElement
                );
            }).ToList();

            return new UiSceneSnapshot(
                snapshot.WindowHandle ?? IntPtr.Zero,
                snapshot.Pid,
                snapshot.AppName,
                snapshot.WindowTitle,
                candidates
            );
        }, ct);
    }

    public Task<AXSnapshot> CaptureAsync(int maxNodes = 900, double seconds = 0.35, int? targetPid = null, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            IntPtr hwnd = GetForegroundWindow();
            int pid = targetPid ?? 0;
            string processName = "Unknown";

            if (hwnd != IntPtr.Zero)
            {
                GetWindowThreadProcessId(hwnd, out uint fgPid);
                if (!targetPid.HasValue) pid = (int)fgPid;
            }

            try
            {
                if (pid > 0)
                {
                    using var proc = Process.GetProcessById(pid);
                    processName = proc.ProcessName;
                    if (targetPid.HasValue && proc.MainWindowHandle != IntPtr.Zero)
                    {
                        hwnd = proc.MainWindowHandle;
                    }
                }
            }
            catch { }

            if (hwnd == IntPtr.Zero)
            {
                return new AXSnapshot(pid, processName, "No Active Window", [], false, readSeconds: sw.Elapsed.TotalSeconds);
            }

            AutomationElement? rootElement = null;
            try
            {
                rootElement = _automation.FromHandle(hwnd);
            }
            catch
            {
                return new AXSnapshot(pid, processName, "Unavailable", [], true, readSeconds: sw.Elapsed.TotalSeconds, windowHandle: hwnd);
            }

            if (rootElement == null)
            {
                return new AXSnapshot(pid, processName, "Unavailable", [], true, readSeconds: sw.Elapsed.TotalSeconds, windowHandle: hwnd);
            }

            string windowTitle = "";
            try { windowTitle = rootElement.Name ?? processName; } catch { windowTitle = processName; }

            var nodes = new List<AXNode>();
            var handles = new List<object?>();
            var queue = new Queue<(AutomationElement Element, int? ParentIndex, int Depth)>();
            queue.Enqueue((rootElement, null, 0));

            var coverageGaps = new List<AXCoverageGap>();
            var maxTimeMs = seconds * 1000;

            while (queue.Count > 0 && nodes.Count < maxNodes)
            {
                ct.ThrowIfCancellationRequested();
                if (sw.ElapsedMilliseconds > maxTimeMs)
                {
                    coverageGaps.Add(new AXCoverageGap(nodes.Count, AXCoverageReason.Budget));
                    break;
                }

                var (element, parentIndex, depth) = queue.Dequeue();

                try
                {
                    var rect = Rectangle.Empty;
                    try
                    {
                        var b = element.BoundingRectangle;
                        if (!b.IsEmpty) rect = new Rectangle((int)b.Left, (int)b.Top, (int)b.Width, (int)b.Height);
                    }
                    catch { }

                    var controlType = ControlType.Custom;
                    try { controlType = element.ControlType; } catch { }

                    string role = MapRole(controlType);
                    string label = "";
                    try { label = element.Name ?? ""; } catch { }

                    string value = "";
                    try
                    {
                        if (element.Patterns.Value.IsSupported)
                        {
                            value = element.Patterns.Value.Pattern.Value.Value ?? "";
                        }
                        else if (element.Patterns.RangeValue.IsSupported)
                        {
                            value = element.Patterns.RangeValue.Pattern.Value.Value.ToString();
                        }
                    }
                    catch { }

                    var actions = new List<string>();
                    try
                    {
                        if (element.Patterns.Invoke.IsSupported) actions.Add("AXPress");
                        if (element.Patterns.ExpandCollapse.IsSupported) actions.Add("AXShowMenu");
                        if (element.Patterns.RangeValue.IsSupported)
                        {
                            actions.Add("AXIncrement");
                            actions.Add("AXDecrement");
                        }
                    }
                    catch { }

                    bool isEnabled = true;
                    try { isEnabled = element.IsEnabled; } catch { }

                    bool isFocused = false;
                    try { isFocused = element.Properties.HasKeyboardFocus.ValueOrDefault; } catch { }

                    bool isSelected = false;
                    try
                    {
                        if (element.Patterns.SelectionItem.IsSupported)
                        {
                            isSelected = element.Patterns.SelectionItem.Pattern.IsSelected.Value;
                        }
                    }
                    catch { }

                    double? min = null, max = null;
                    try
                    {
                        if (element.Patterns.RangeValue.IsSupported)
                        {
                            min = element.Patterns.RangeValue.Pattern.Minimum.Value;
                            max = element.Patterns.RangeValue.Pattern.Maximum.Value;
                        }
                    }
                    catch { }

                    string help = "";
                    try { help = element.HelpText ?? ""; } catch { }

                    var nodeIndex = nodes.Count;
                    var node = new AXNode
                    {
                        Id = nodeIndex,
                        Parent = parentIndex,
                        Depth = depth,
                        Role = role,
                        Subrole = "",
                        Label = label,
                        Value = value,
                        Actions = actions,
                        Enabled = isEnabled,
                        Frame = rect,
                        Help = help,
                        Focused = isFocused,
                        Selected = isSelected,
                        Minimum = min,
                        Maximum = max,
                        Visible = rect.Width > 0 && rect.Height > 0,
                        NativeElement = element
                    };

                    nodes.Add(node);
                    handles.Add(element);

                    // Enqueue children
                    try
                    {
                        var children = element.FindAllChildren();
                        foreach (var child in children)
                        {
                            queue.Enqueue((child, nodeIndex, depth + 1));
                        }
                    }
                    catch { }
                }
                catch { }
            }

            bool partial = queue.Count > 0 || nodes.Count >= maxNodes;
            return new AXSnapshot(
                pid,
                processName,
                windowTitle,
                nodes,
                partial,
                handles,
                readSeconds: sw.Elapsed.TotalSeconds,
                windowHandle: hwnd,
                bundleID: processName,
                coverage: coverageGaps
            );
        }, ct);
    }

    private static string MapRole(ControlType type)
    {
        if (type == ControlType.Button || type == ControlType.SplitButton) return "AXButton";
        if (type == ControlType.Edit) return "AXTextField";
        if (type == ControlType.Document) return "AXWebArea";
        if (type == ControlType.CheckBox) return "AXCheckBox";
        if (type == ControlType.RadioButton) return "AXRadioButton";
        if (type == ControlType.MenuItem) return "AXMenuItem";
        if (type == ControlType.Menu || type == ControlType.MenuBar) return "AXMenuBar";
        if (type == ControlType.TabItem) return "AXTabItem";
        if (type == ControlType.Tab) return "AXTabGroup";
        if (type == ControlType.List) return "AXList";
        if (type == ControlType.ListItem) return "AXRow";
        if (type == ControlType.Table || type == ControlType.DataGrid) return "AXTable";
        if (type == ControlType.Tree) return "AXOutline";
        if (type == ControlType.TreeItem) return "AXRow";
        if (type == ControlType.ComboBox) return "AXComboBox";
        if (type == ControlType.Hyperlink) return "AXLink";
        if (type == ControlType.Slider) return "AXSlider";
        if (type == ControlType.Spinner) return "AXIncrementor";
        if (type == ControlType.ToolBar) return "AXToolbar";
        if (type == ControlType.Window) return "AXWindow";
        if (type == ControlType.Text) return "AXStaticText";
        if (type == ControlType.Header || type == ControlType.TitleBar) return "AXHeading";
        if (type == ControlType.ScrollBar) return "AXScrollArea";
        return "AXGroup";
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
