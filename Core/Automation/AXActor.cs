using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using FlaUI.Core.AutomationElements;
using WiraVoice.Core.Commands;

namespace WiraVoice.Core.Automation;

/// <summary>
/// Executes native automation operations on AXCandidate controls with InputPermit lease enforcement.
/// Port of Computah's AXActor.swift for Windows.
/// </summary>
public static class AXActor
{
    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_BACK = 0x08;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_A = 0x41;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [MarshalAs(UnmanagedType.LPArray), In] INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    public static string Perform(AXCandidate candidate, AXSnapshot snapshot, string? text, InputPermit permit, bool physicalActivation = true)
    {
        permit.Check();

        if (!snapshot.Nodes.IndicesContains(candidate.NodeID))
        {
            throw AXFailureException.Unavailable("The selected candidate is missing from the observation snapshot.");
        }

        var node = snapshot.Nodes[candidate.NodeID];
        var rect = node.Frame ?? new Rectangle(0, 0, 100, 30);
        int centerX = rect.X + rect.Width / 2;
        int centerY = rect.Y + rect.Height / 2;

        switch (candidate.Operation)
        {
            case AXOperation.PressClick:
                permit.Perform(() =>
                {
                    SendMouseMove(centerX, centerY);
                    SendMouseClick(centerX, centerY);
                }, effect: true);
                return $"Dispatched pressClick to {candidate.Description} at ({centerX},{centerY})";

            case AXOperation.TypeText:
                if (!IsBrowserOmnibox(candidate, node) && node.NativeElement is AutomationElement typeEl && typeEl.Patterns.Value.IsSupported && !typeEl.Patterns.Value.Pattern.IsReadOnly)
                {
                    permit.Perform(() =>
                    {
                        typeEl.Patterns.Value.Pattern.SetValue(text ?? "");
                        if (IsAutoSubmitControl(candidate, node))
                        {
                            try { typeEl.Focus(); } catch { }
                            SendKeyDown(VK_RETURN);
                            SendKeyUp(VK_RETURN);
                        }
                    }, effect: true);
                    return $"Dispatched typeText \"{text}\" via ValuePattern to {candidate.Description}";
                }
                else
                {
                    permit.Perform(() =>
                    {
                        SendMouseMove(centerX, centerY);
                        SendMouseClick(centerX, centerY);
                        Thread.Sleep(50);
                        if (!string.IsNullOrEmpty(text))
                        {
                            SendUnicodeString(text, permit);
                        }
                        if (IsAutoSubmitControl(candidate, node))
                        {
                            Thread.Sleep(60);
                            SendKeyDown(VK_RETURN);
                            SendKeyUp(VK_RETURN);
                        }
                    }, effect: true);
                    return $"Dispatched typeText \"{text}\" to {candidate.Description}";
                }

            case AXOperation.ReplaceText:
                if (!IsBrowserOmnibox(candidate, node) && node.NativeElement is AutomationElement repEl && repEl.Patterns.Value.IsSupported && !repEl.Patterns.Value.Pattern.IsReadOnly)
                {
                    permit.Perform(() =>
                    {
                        repEl.Patterns.Value.Pattern.SetValue(text ?? "");
                        if (IsAutoSubmitControl(candidate, node))
                        {
                            try { repEl.Focus(); } catch { }
                            SendKeyDown(VK_RETURN);
                            SendKeyUp(VK_RETURN);
                        }
                    }, effect: true);
                    return $"Dispatched replaceText \"{text}\" via ValuePattern to {candidate.Description}";
                }
                else
                {
                    permit.Perform(() =>
                    {
                        SendMouseMove(centerX, centerY);
                        SendMouseClick(centerX, centerY);
                        Thread.Sleep(50);
                        // Select all (Ctrl+A) and replace
                        SendKeyDown(VK_CONTROL);
                        SendKeyDown(VK_A);
                        SendKeyUp(VK_A);
                        SendKeyUp(VK_CONTROL);
                        Thread.Sleep(15);
                        SendKeyDown(VK_BACK);
                        SendKeyUp(VK_BACK);
                        Thread.Sleep(15);
                        if (!string.IsNullOrEmpty(text))
                        {
                            SendUnicodeString(text, permit);
                        }
                        if (IsAutoSubmitControl(candidate, node))
                        {
                            Thread.Sleep(60);
                            SendKeyDown(VK_RETURN);
                            SendKeyUp(VK_RETURN);
                        }
                    }, effect: true);
                    return $"Dispatched replaceText \"{text}\" to {candidate.Description}";
                }

            case AXOperation.PressReturn:
                permit.Perform(() =>
                {
                    SendKeyDown(VK_RETURN);
                    SendKeyUp(VK_RETURN);
                }, effect: true);
                return $"Dispatched pressReturn to {candidate.Description}";

            case AXOperation.ScrollUp:
                permit.Perform(() =>
                {
                    SendMouseMove(centerX, centerY);
                    SendMouseWheel(120);
                }, effect: true);
                return $"Dispatched scrollUp to {candidate.Description}";

            case AXOperation.ScrollDown:
                permit.Perform(() =>
                {
                    SendMouseMove(centerX, centerY);
                    SendMouseWheel(-120);
                }, effect: true);
                return $"Dispatched scrollDown to {candidate.Description}";

            case AXOperation.ShowMenu:
                permit.Perform(() =>
                {
                    SendMouseMove(centerX, centerY);
                    SendMouseRightClick(centerX, centerY);
                }, effect: true);
                return $"Dispatched showMenu to {candidate.Description}";

            case AXOperation.SetNumber:
                if (node.NativeElement is AutomationElement el && el.Patterns.RangeValue.IsSupported && double.TryParse(text?.Replace("%", ""), out var val))
                {
                    permit.Perform(() =>
                    {
                        el.Patterns.RangeValue.Pattern.SetValue(val);
                    }, effect: true);
                    return $"Dispatched setNumber {val} via RangeValue to {candidate.Description}";
                }
                else
                {
                    permit.Perform(() =>
                    {
                        SendMouseMove(centerX, centerY);
                        SendMouseClick(centerX, centerY);
                    }, effect: true);
                    return $"Dispatched setNumber click to {candidate.Description}";
                }

            case AXOperation.Select:
            case AXOperation.Increment:
            case AXOperation.Decrement:
            default:
                permit.Perform(() =>
                {
                    SendMouseMove(centerX, centerY);
                    SendMouseClick(centerX, centerY);
                }, effect: true);
                return $"Dispatched {candidate.Operation.ToWireString()} to {candidate.Description}";
        }
    }

    private static void SendMouseMove(int screenX, int screenY)
    {
        int sw = GetSystemMetrics(0);
        int sh = GetSystemMetrics(1);
        if (sw <= 0 || sh <= 0) return;

        int normX = (screenX * 65535) / sw;
        int normY = (screenY * 65535) / sh;

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dx = normX,
                    dy = normY,
                    mouseData = 0,
                    dwFlags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_MOVE,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private static void SendMouseClick(int screenX, int screenY)
    {
        int sw = GetSystemMetrics(0);
        int sh = GetSystemMetrics(1);
        if (sw <= 0 || sh <= 0) return;

        int normX = (screenX * 65535) / sw;
        int normY = (screenY * 65535) / sh;

        var inputs = new INPUT[2];
        inputs[0] = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dx = normX,
                    dy = normY,
                    mouseData = 0,
                    dwFlags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_LEFTDOWN,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        inputs[1] = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dx = normX,
                    dy = normY,
                    mouseData = 0,
                    dwFlags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_LEFTUP,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendMouseRightClick(int screenX, int screenY)
    {
        int sw = GetSystemMetrics(0);
        int sh = GetSystemMetrics(1);
        if (sw <= 0 || sh <= 0) return;

        int normX = (screenX * 65535) / sw;
        int normY = (screenY * 65535) / sh;

        var inputs = new INPUT[2];
        inputs[0] = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dx = normX,
                    dy = normY,
                    mouseData = 0,
                    dwFlags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_RIGHTDOWN,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        inputs[1] = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dx = normX,
                    dy = normY,
                    mouseData = 0,
                    dwFlags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_RIGHTUP,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendMouseWheel(int delta)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = (uint)delta,
                    dwFlags = MOUSEEVENTF_WHEEL,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private static void SendKeyDown(ushort vk)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private static void SendKeyUp(ushort vk)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private static void SendUnicodeString(string text, InputPermit permit)
    {
        foreach (char c in text)
        {
            permit.Check();
            var inputs = new INPUT[2];
            inputs[0] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
            inputs[1] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
            SendInput(2, inputs, Marshal.SizeOf<INPUT>());
            Thread.Sleep(3);
        }
    }

    public static bool IsBrowserOmnibox(AXCandidate candidate, AXNode node)
    {
        string desc = candidate.Description ?? "";
        string label = node.Label ?? "";

        return desc.Contains("Address and search bar", StringComparison.OrdinalIgnoreCase) ||
               desc.Contains("Address bar", StringComparison.OrdinalIgnoreCase) ||
               desc.Contains("Omnibox", StringComparison.OrdinalIgnoreCase) ||
               label.Contains("Address and search bar", StringComparison.OrdinalIgnoreCase) ||
               label.Contains("Address bar", StringComparison.OrdinalIgnoreCase) ||
               label.Contains("Omnibox", StringComparison.OrdinalIgnoreCase) ||
               label.Contains("Search or enter web address", StringComparison.OrdinalIgnoreCase) ||
               label.Contains("Search or enter address", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAutoSubmitControl(AXCandidate candidate, AXNode node)
    {
        if (IsBrowserOmnibox(candidate, node)) return true;

        string desc = candidate.Description ?? "";
        string label = node.Label ?? "";

        return desc.Contains("Search Box", StringComparison.OrdinalIgnoreCase) ||
               desc.Contains("Search Field", StringComparison.OrdinalIgnoreCase) ||
               desc.Contains("Search bar", StringComparison.OrdinalIgnoreCase) ||
               node.Role == "AXSearchField" ||
               (node.Role == "AXTextField" && (label.Contains("Search", StringComparison.OrdinalIgnoreCase) || label.Contains("Address", StringComparison.OrdinalIgnoreCase)));
    }
}
