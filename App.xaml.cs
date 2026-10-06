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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Ensure console output is attached
        AttachConsole(ATTACH_PARENT_PROCESS);

        Console.CancelKeyPress += (s, args) =>
        {
            args.Cancel = true;
            Console.WriteLine("\n[Server] Shutdown requested via terminal (Ctrl+C). Exiting cleanly...");
            Dispatcher.Invoke(() => Shutdown(0));
        };

        Console.WriteLine();
        Console.WriteLine("======================================================================");
        Console.WriteLine("  Wira Voice — Voice Automation & Decision Pipeline");
        Console.WriteLine("======================================================================");
        Console.WriteLine("  Press Ctrl+C in this terminal or right-click the top bar to exit.");
        Console.WriteLine("======================================================================");
        Console.WriteLine();
    }
}
