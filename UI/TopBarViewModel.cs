using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using WiraVoice.Core.Commands;
using WiraVoice.Core.Config;
using WiraVoice.Core.Speech;

namespace WiraVoice.UI;

public sealed class TopBarViewModel : INotifyPropertyChanged
{
    private readonly CommandCoordinator _coordinator;
    private readonly SpeechEngineRouter _router;

    private string _status = "Ready";
    private string _transcript = "Say a command (e.g., 'Click Submit' or 'Type hello')...";
    private double _audioLevel = 0.0;
    private bool _isListening = false;
    private SpeechEngineMode _engineMode = SpeechEngineMode.CloudDeepgram;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    public string Transcript
    {
        get => _transcript;
        set { _transcript = value; OnPropertyChanged(); }
    }

    public double AudioLevel
    {
        get => _audioLevel;
        set { _audioLevel = value; OnPropertyChanged(); }
    }

    public bool IsListening
    {
        get => _isListening;
        set { _isListening = value; OnPropertyChanged(); }
    }

    public SpeechEngineMode EngineMode
    {
        get => _engineMode;
        set
        {
            _engineMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EngineModeDisplay));
            OnPropertyChanged(nameof(IsCloudMode));
        }
    }

    public string EngineModeDisplay => EngineMode == SpeechEngineMode.CloudDeepgram ? "Cloud (Deepgram)" : "Local (Whisper)";
    public bool IsCloudMode => EngineMode == SpeechEngineMode.CloudDeepgram;

    public TopBarViewModel(CommandCoordinator coordinator, SpeechEngineRouter router)
    {
        _coordinator = coordinator;
        _router = router;

        _coordinator.OnStatus = (status, isBusy) =>
        {
            App.Current?.Dispatcher?.Invoke(() => Status = status);
        };

        _coordinator.OnTurnFinished = result =>
        {
            App.Current?.Dispatcher?.Invoke(() => Status = result.Status);
        };

        _router.OnTranscript += (text, isFinal, turnId) =>
        {
            App.Current?.Dispatcher?.Invoke(() =>
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    Transcript = text;
                }
            });
        };

        _router.OnAudioLevel += level =>
        {
            App.Current?.Dispatcher?.Invoke(() => AudioLevel = level);
        };
    }

    public async Task ToggleListeningAsync()
    {
        if (IsListening)
        {
            await _coordinator.StopSpeechAsync();
            IsListening = false;
            Status = "Stopped";
            AudioLevel = 0.0;
        }
        else
        {
            Status = "Starting...";
            await _coordinator.StartSpeechAsync();
            IsListening = true;
            Status = "Listening…";
        }
    }

    public async Task ToggleEngineModeAsync()
    {
        var newMode = EngineMode == SpeechEngineMode.CloudDeepgram
            ? SpeechEngineMode.LocalWhisper
            : SpeechEngineMode.CloudDeepgram;

        await _router.SwitchModeAsync(newMode);
        EngineMode = newMode;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
