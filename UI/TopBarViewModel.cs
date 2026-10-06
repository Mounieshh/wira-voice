using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using WiraVoice.Core.Commands;
using WiraVoice.Core.Config;
using WiraVoice.Core.Speech;

namespace WiraVoice.UI;

/// <summary>
/// Presentation model for the compact floating top bar notch UI.
/// Encapsulates transcript formatting, audio waveform math matching Computah,
/// and speech engine switching behind a clean, testable seam.
/// </summary>
public sealed class TopBarViewModel : INotifyPropertyChanged
{
    private readonly CommandCoordinator? _coordinator;
    private readonly SpeechEngineRouter? _router;

    private string _status = "Ready";
    private string _transcript = string.Empty;
    private double _audioLevel = 0.0;
    private double _displayedLevel = 0.0;
    private bool _isListening = false;
    private SpeechEngineMode _engineMode = SpeechEngineMode.CloudDeepgram;

    // Resting baseline heights matching Computah: [7, 15, 20, 10]
    private double _bar0Height = 6.0;
    private double _bar1Height = 13.0;
    private double _bar2Height = 17.0;
    private double _bar3Height = 9.0;

    private readonly DateTime _startTime = DateTime.UtcNow;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Status
    {
        get => _status;
        set
        {
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayText));
            OnPropertyChanged(nameof(IsPlaceholder));
        }
    }

    public string Transcript
    {
        get => _transcript;
        set
        {
            _transcript = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayText));
            OnPropertyChanged(nameof(IsPlaceholder));
        }
    }

    public string DisplayText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_transcript))
            {
                return _transcript;
            }

            return _isListening ? "Go ahead, I'm listening…" : "Say a command or click to listen…";
        }
    }

    public bool IsPlaceholder => string.IsNullOrWhiteSpace(_transcript);

    public double AudioLevel
    {
        get => _audioLevel;
        set { _audioLevel = Math.Clamp(value, 0.0, 1.0); OnPropertyChanged(); }
    }

    public bool IsListening
    {
        get => _isListening;
        set
        {
            _isListening = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayText));
            OnPropertyChanged(nameof(ListenButtonTooltip));
        }
    }

    public string ListenButtonTooltip => _isListening ? "Stop listening" : "Start listening";

    public SpeechEngineMode EngineMode
    {
        get => _engineMode;
        set
        {
            _engineMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EngineModeDisplay));
            OnPropertyChanged(nameof(IsCloudMode));
            OnPropertyChanged(nameof(IsLocalMode));
        }
    }

    public string EngineModeDisplay => EngineMode == SpeechEngineMode.CloudDeepgram ? "Cloud (Deepgram)" : "Local (Whisper)";
    public bool IsCloudMode => EngineMode == SpeechEngineMode.CloudDeepgram;
    public bool IsLocalMode => EngineMode == SpeechEngineMode.LocalWhisper;

    public double Bar0Height
    {
        get => _bar0Height;
        private set { _bar0Height = value; OnPropertyChanged(); }
    }

    public double Bar1Height
    {
        get => _bar1Height;
        private set { _bar1Height = value; OnPropertyChanged(); }
    }

    public double Bar2Height
    {
        get => _bar2Height;
        private set { _bar2Height = value; OnPropertyChanged(); }
    }

    public double Bar3Height
    {
        get => _bar3Height;
        private set { _bar3Height = value; OnPropertyChanged(); }
    }

    public TopBarViewModel(CommandCoordinator? coordinator = null, SpeechEngineRouter? router = null)
    {
        _coordinator = coordinator;
        _router = router;

        if (_coordinator != null)
        {
            _coordinator.OnStatus = (status, isBusy) =>
            {
                SafeDispatch(() => Status = status);
            };

            _coordinator.OnTurnFinished = result =>
            {
                SafeDispatch(() => Status = result.Status);
            };
        }

        if (_router != null)
        {
            _router.OnStatusChanged += status =>
            {
                SafeDispatch(() =>
                {
                    Status = status;
                    if (status.StartsWith("Listening to voice", StringComparison.OrdinalIgnoreCase))
                    {
                        // Fresh speech started: clear old transcript so top bar visibly resets
                        _transcript = string.Empty;
                        OnPropertyChanged(nameof(Transcript));
                        OnPropertyChanged(nameof(DisplayText));
                        OnPropertyChanged(nameof(IsPlaceholder));
                    }
                });
            };

            _router.OnTranscript += (text, isFinal, turnId) =>
            {
                SafeDispatch(() =>
                {
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        Console.WriteLine($"[TopBar] Updating display transcript: \"{text}\"");
                        Transcript = text;
                        Status = "Transcribed";
                    }
                });
            };

            _router.OnAudioLevel += level =>
            {
                AudioLevel = level;
            };
        }
    }

    /// <summary>
    /// Smoothly updates the 4 audio visualizer waveform bars matching Computah's exact formula.
    /// When the user is silent, the dots remain completely still at baseline height 4.0.
    /// When the user speaks, they dynamically scale into lively equalizer bars.
    /// </summary>
    public void UpdateWaveformTick(double elapsedSeconds = 0.033)
    {
        double rate = _audioLevel > _displayedLevel ? 20.0 : 7.0;
        _displayedLevel += (_audioLevel - _displayedLevel) * Math.Min(1.0, elapsedSeconds * rate);

        if (_isListening)
        {
            if (_displayedLevel <= 0.005)
            {
                // Silent: dots remain completely motionless
                Bar0Height = 4.0;
                Bar1Height = 4.0;
                Bar2Height = 4.0;
                Bar3Height = 4.0;
            }
            else
            {
                // Active speech: scale bars using Computah's exact formula clamped to bar height
                double phase = (DateTime.UtcNow - _startTime).TotalSeconds * 4.0;
                Bar0Height = Math.Clamp(4.0 + _displayedLevel * (10.0 + (Math.Sin(phase + 0 * 1.2) + 1.0) * 4.0), 4.0, 22.0);
                Bar1Height = Math.Clamp(4.0 + _displayedLevel * (10.0 + (Math.Sin(phase + 1 * 1.2) + 1.0) * 4.0), 4.0, 22.0);
                Bar2Height = Math.Clamp(4.0 + _displayedLevel * (10.0 + (Math.Sin(phase + 2 * 1.2) + 1.0) * 4.0), 4.0, 22.0);
                Bar3Height = Math.Clamp(4.0 + _displayedLevel * (10.0 + (Math.Sin(phase + 3 * 1.2) + 1.0) * 4.0), 4.0, 22.0);
            }
        }
        else
        {
            // Resting state heights when stopped (matching upstream Computah [7, 15, 20, 10])
            Bar0Height = 7.0;
            Bar1Height = 15.0;
            Bar2Height = 20.0;
            Bar3Height = 10.0;
        }
    }

    public async Task ToggleListeningAsync()
    {
        if (IsListening)
        {
            if (_coordinator != null)
                await _coordinator.StopSpeechAsync();

            IsListening = false;
            Status = "Stopped";
            AudioLevel = 0.0;
            _displayedLevel = 0.0;
        }
        else
        {
            Status = "Starting...";
            try
            {
                if (_coordinator != null)
                {
                    await _coordinator.StartSpeechAsync();
                }

                // Check if active engine actually started listening
                if (_router != null && !_router.IsListening)
                {
                    // Automatic fallback: If cloud Deepgram key is missing, switch to local Whisper
                    if (EngineMode == SpeechEngineMode.CloudDeepgram && string.IsNullOrWhiteSpace(EnvironmentConfig.DeepgramApiKey))
                    {
                        Status = "Cloud key missing; falling back to Local Whisper…";
                        await SetEngineModeAsync(SpeechEngineMode.LocalWhisper);
                        if (_coordinator != null)
                        {
                            await _coordinator.StartSpeechAsync();
                        }
                    }

                    if (!_router.IsListening)
                    {
                        IsListening = false;
                        return;
                    }
                }

                IsListening = true;
                Status = "Listening…";
            }
            catch (Exception ex)
            {
                IsListening = false;
                Status = $"Error: {ex.Message}";
            }
        }
    }

    public async Task ToggleEngineModeAsync()
    {
        var newMode = EngineMode == SpeechEngineMode.CloudDeepgram
            ? SpeechEngineMode.LocalWhisper
            : SpeechEngineMode.CloudDeepgram;

        await SetEngineModeAsync(newMode);
    }

    public async Task SetEngineModeAsync(SpeechEngineMode mode)
    {
        if (EngineMode == mode) return;

        if (_router != null)
        {
            await _router.SwitchModeAsync(mode);
        }

        EngineMode = mode;
    }

    public void ClearTranscript()
    {
        Transcript = string.Empty;
        Status = "Ready";
    }

    private static void SafeDispatch(Action action)
    {
        var app = App.Current;
        if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(action);
        }
        else
        {
            action();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
