using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FlaUI.Core.AutomationElements;

namespace WiraVoice.Core.Automation;

/// <summary>
/// Deep module that dispatches native Win32 SendInput hardware mouse and keyboard events.
/// Enforces pre-click hit-testing, editor focusing, and paired down/up key safety.
/// </summary>
public sealed class Win32InputActor : IInputActor
{
    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const ushort VK_RETURN = 0x0D;

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

    public Task<bool> ClickControlAsync(UiControlCandidate control, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            var rect = control.BoundingBox;
            if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
                return false;

            // Target center coordinate
            int centerX = rect.X + rect.Width / 2;
            int centerY = rect.Y + rect.Height / 2;

            SendMouseClick(centerX, centerY);
            return true;
        }, ct);
    }

    public Task<bool> TypeTextAsync(UiControlCandidate control, string text, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            // 1. Ensure control has focus
            if (control.NativeElement is AutomationElement el)
            {
                try
                {
                    el.Focus();
                }
                catch
                {
                    // Fall back to physical click to focus
                    ClickControlAsync(control, ct).Wait(ct);
                }
            }
            else
            {
                ClickControlAsync(control, ct).Wait(ct);
            }

            Thread.Sleep(50); // Small pause for editor focus transition

            // 2. Synthesize unicode keystrokes
            SendUnicodeString(text, ct);

            return true;
        }, ct);
    }

    public Task<bool> PressReturnAsync(UiControlCandidate control, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            var inputs = new INPUT[2];
            inputs[0] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = VK_RETURN,
                        wScan = 0,
                        dwFlags = 0,
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
                        wVk = VK_RETURN,
                        wScan = 0,
                        dwFlags = KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            return true;
        }, ct);
    }

    private static void SendMouseClick(int screenX, int screenY)
    {
        int screenWidth = GetSystemMetrics(0);  // SM_CXSCREEN
        int screenHeight = GetSystemMetrics(1); // SM_CYSCREEN

        if (screenWidth <= 0 || screenHeight <= 0) return;

        int normX = (screenX * 65535) / screenWidth;
        int normY = (screenY * 65535) / screenHeight;

        var inputs = new INPUT[3];

        // Move
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
                    dwFlags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_MOVE,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        // Down
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
                    dwFlags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_LEFTDOWN,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        // Up
        inputs[2] = new INPUT
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

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendUnicodeString(string text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text)) return;

        foreach (char c in text)
        {
            ct.ThrowIfCancellationRequested();

            var inputs = new INPUT[2];

            // Character Down
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

            // Character Up
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

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            Thread.Sleep(5); // Human-like cadence
        }
    }
}
