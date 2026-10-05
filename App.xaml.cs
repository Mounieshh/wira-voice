using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace WiraVoice;

/// <summary>
/// Interaction logic for App.xaml
/// Attaches to the calling console when run from terminal/PowerShell
/// to enable Ctrl+C graceful shutdown and console logging.
/// </summary>
public partial class App : Application
{
    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate? handler, bool add);

    private delegate bool ConsoleCtrlDelegate(int sig);
    private static ConsoleCtrlDelegate? _consoleCtrlHandler;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // If launched from a terminal/console (e.g. dotnet run or powershell/cmd), attach to it
        if (AttachConsole(ATTACH_PARENT_PROCESS))
        {
            _consoleCtrlHandler = sig =>
            {
                // Intercept CTRL_C (0), CTRL_BREAK (1), CTRL_CLOSE (2)
                Console.WriteLine("\n[WiraVoice] Shutdown requested via terminal (Ctrl+C). Exiting...");
                Dispatcher.Invoke(() =>
                {
                    Shutdown(0);
                });
                return true;
            };

            SetConsoleCtrlHandler(_consoleCtrlHandler, true);
            Console.WriteLine("[WiraVoice] Running in foreground. Press Ctrl+C in this terminal or right-click the notch to exit.");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        FreeConsole();
        base.OnExit(e);
    }
}
