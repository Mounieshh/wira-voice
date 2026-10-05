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

    private readonly TopBarViewModel _viewModel;
    private readonly CommandCoordinator _coordinator;
    private readonly DispatcherTimer _animationTimer;
    private bool _userMoved = false;

    public FloatingTopBar()
    {
        InitializeComponent();

        // 1. Initialize Core Pipeline
        EnvironmentConfig.Initialize();

        var audio = new AudioCaptureService();
        var cloud = new DeepgramFluxEngine(audio);
        var local = new WhisperLocalEngine(audio);
        var router = new SpeechEngineRouter(cloud, local);

        var apiKey = EnvironmentConfig.TypeSafeApiKey;
        if (string.IsNullOrEmpty(apiKey)) apiKey = EnvironmentConfig.OpenRouterApiKey;

        var endpoint = !string.IsNullOrEmpty(EnvironmentConfig.TypeSafeApiKey)
            ? new Uri("https://api.typesafe.ai/v1/systemone")
            : new Uri(EnvironmentConfig.OpenRouterBaseUrl.TrimEnd('/') + "/systemone");

        var jevSelector = new JevSelector(apiKey, EnvironmentConfig.JevModel, endpoint: endpoint);
        var engine = new CommandEngine(jevSelector);

        _coordinator = new CommandCoordinator(engine, speechEngine: router);
        _viewModel = new TopBarViewModel(_coordinator, router);
        DataContext = _viewModel;

        // 2. Set up smooth 30 FPS waveform animation timer
        _animationTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _animationTimer.Tick += (s, e) => _viewModel.UpdateWaveformTick(0.033);
        _animationTimer.Start();

        // 3. Center notch at top of screen
        Loaded += (s, e) => CenterOnScreen();
        SizeChanged += (s, e) => CenterOnScreen();
    }

    private void CenterOnScreen()
    {
        if (_userMoved) return;

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
            MoreButton.ContextMenu.PlacementTarget = this;
            MoreButton.ContextMenu.IsOpen = true;
        }
    }

    private async void OnToggleListeningClicked(object sender, RoutedEventArgs e)
    {
        await _viewModel.ToggleListeningAsync();
    }

    private void OnMoreOptionsClicked(object sender, RoutedEventArgs e)
    {
        if (MoreButton.ContextMenu != null)
        {
            MoreButton.ContextMenu.PlacementTarget = MoreButton;
            MoreButton.ContextMenu.IsOpen = true;
        }
    }

    private async void OnSelectCloudEngineClicked(object sender, RoutedEventArgs e)
    {
        await _viewModel.SetEngineModeAsync(SpeechEngineMode.CloudDeepgram);
    }

    private async void OnSelectLocalEngineClicked(object sender, RoutedEventArgs e)
    {
        await _viewModel.SetEngineModeAsync(SpeechEngineMode.LocalWhisper);
    }

    private void OnClearTranscriptClicked(object sender, RoutedEventArgs e)
    {
        _viewModel.ClearTranscript();
    }

    private void OnOpenDiagnosticsClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var tempHtml = Path.Combine(Path.GetTempPath(), "architecture-review-20261005.html");
            if (File.Exists(tempHtml))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = tempHtml,
                    UseShellExecute = true
                });
            }
        }
        catch { }
    }

    private void OnExitClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _animationTimer.Stop();
        await _coordinator.DisposeAsync();
    }
}
