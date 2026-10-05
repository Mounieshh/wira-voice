using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
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

    public FloatingTopBar()
    {
        InitializeComponent();

        // 1. Position top-center of primary screen
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 18;

        // 2. Initialize Core Pipeline
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
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Ensure window never steals focus from the user's active window
        var helper = new WindowInteropHelper(this);
        int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
        SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private async void OnToggleEngineModeClicked(object sender, RoutedEventArgs e)
    {
        await _viewModel.ToggleEngineModeAsync();
    }

    private async void OnToggleListeningClicked(object sender, RoutedEventArgs e)
    {
        await _viewModel.ToggleListeningAsync();
    }

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        await _coordinator.DisposeAsync();
    }
}
