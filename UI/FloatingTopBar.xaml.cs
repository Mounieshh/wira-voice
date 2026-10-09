using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using WiraVoice.Core.Audio;
using WiraVoice.Core.Commands;
using WiraVoice.Core.Config;
using WiraVoice.Core.Language;
using WiraVoice.Core.Speech;

namespace WiraVoice.UI;

public partial class FloatingTopBar : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private const int MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private readonly TopBarViewModel _viewModel;
    private readonly CommandCoordinator _coordinator;
    private readonly DispatcherTimer _animationTimer;
    private bool _userMoved = false;

    public FloatingTopBar()
    {
        InitializeComponent();

        // 1. Initialize Core Pipeline
        EnvironmentConfig.Initialize();

        var defaultMode = !string.IsNullOrWhiteSpace(EnvironmentConfig.DeepgramApiKey)
            ? SpeechEngineMode.CloudDeepgram
            : SpeechEngineMode.LocalWhisper;

        var audio = new AudioCaptureService();
        var cloud = new DeepgramFluxEngine(audio);
        var local = new WhisperLocalEngine(audio);
        var router = new SpeechEngineRouter(cloud, local, defaultMode);

        var apiKey = EnvironmentConfig.TypeSafeApiKey;
        if (string.IsNullOrEmpty(apiKey)) apiKey = EnvironmentConfig.OpenRouterApiKey;

        bool hasTypeSafeKey = !string.IsNullOrEmpty(EnvironmentConfig.TypeSafeApiKey);
        var endpoint = hasTypeSafeKey
            ? new Uri("https://api.typesafe.ai/v1/systemone")
            : new Uri("https://openrouter.ai/api/alpha/decisions");

        var jevModel = hasTypeSafeKey ? EnvironmentConfig.JevModel : "typesafe/jev-1.13";

        var jevSelector = new JevSelector(apiKey, jevModel, endpoint: endpoint);
        var engine = new CommandEngine(jevSelector);

        _coordinator = new CommandCoordinator(engine, speechEngine: router);
        _viewModel = new TopBarViewModel(_coordinator, router)
        {
            EngineMode = defaultMode
        };
        DataContext = _viewModel;

        // 2. Set up smooth 30 FPS waveform animation timer
        _animationTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _animationTimer.Tick += (s, e) => _viewModel.UpdateWaveformTick(0.033);
        _animationTimer.Start();

        // 3. Center notch at top of screen and automatically start listening
        Loaded += (s, e) =>
        {
            CenterOnScreen();
            SafeAsync(async () => await _viewModel.ToggleListeningAsync());
        };
        SizeChanged += (s, e) => CenterOnScreen();
        KeyDown += Window_KeyDown;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            SafeAsync(async () => await _viewModel.ToggleListeningAsync());
        }
        else if (e.Key == Key.Escape)
        {
            if (MoreButton.ContextMenu != null && MoreButton.ContextMenu.IsOpen)
            {
                MoreButton.ContextMenu.IsOpen = false;
                e.Handled = true;
            }
        }
    }

    private void CenterOnScreen()
    {
        if (_userMoved) return;

        var helper = new WindowInteropHelper(this);
        var hMonitor = MonitorFromWindow(helper.Handle, MONITOR_DEFAULTTONEAREST);

        if (hMonitor != IntPtr.Zero)
        {
            var mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf<MONITORINFO>();
            if (GetMonitorInfo(hMonitor, ref mi))
            {
                // Convert work area from physical pixels to WPF device-independent units
                var source = PresentationSource.FromVisual(this);
                double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

                double workLeft = mi.rcWork.Left / dpiX;
                double workWidth = (mi.rcWork.Right - mi.rcWork.Left) / dpiX;
                double workTop = mi.rcWork.Top / dpiY;

                Left = workLeft + (workWidth - ActualWidth) / 2;
                Top = workTop + 14;
                return;
            }
        }

        var screenWidth = SystemParameters.PrimaryScreenWidth;
        Left = (screenWidth - ActualWidth) / 2;
        Top = 14;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Ensure window never steals focus from active applications
        var helper = new WindowInteropHelper(this);
        int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
        SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _userMoved = true;
            DragMove();
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (MoreButton.ContextMenu != null)
        {
            MoreButton.ContextMenu.PlacementTarget = MoreButton;
            MoreButton.ContextMenu.IsOpen = true;
        }
    }

    private void OnToggleListeningClicked(object sender, RoutedEventArgs e)
    {
        SafeAsync(async () => await _viewModel.ToggleListeningAsync());
    }

    private void OnMoreOptionsClicked(object sender, RoutedEventArgs e)
    {
        if (MoreButton.ContextMenu != null)
        {
            MoreButton.ContextMenu.PlacementTarget = MoreButton;
            MoreButton.ContextMenu.IsOpen = true;
        }
    }

    private void OnSelectCloudEngineClicked(object sender, RoutedEventArgs e)
    {
        SafeAsync(async () => await _viewModel.SetEngineModeAsync(SpeechEngineMode.CloudDeepgram));
    }

    private void OnSelectLocalEngineClicked(object sender, RoutedEventArgs e)
    {
        SafeAsync(async () => await _viewModel.SetEngineModeAsync(SpeechEngineMode.LocalWhisper));
    }

    private void OnExitClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SafeAsync(Func<Task> action)
    {
        Task.Run(async () =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    _viewModel.Status = $"Error: {ex.Message}";
                });
            }
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _animationTimer.Stop();

        try
        {
            if (_coordinator != null)
            {
                _coordinator.Shutdown();
                _coordinator.DisposeAsync().AsTask().Wait(1000);
            }
        }
        catch { }

        try { Deepgram.Library.Terminate(); } catch { }
        Application.Current?.Shutdown(0);
    }
}
