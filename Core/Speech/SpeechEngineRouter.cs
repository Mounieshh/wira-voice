using System;
using System.Threading;
using System.Threading.Tasks;

namespace WiraVoice.Core.Speech;

/// <summary>
/// Routes speech events to the currently active engine (Cloud Deepgram or Local Whisper)
/// and allows seamless hot-swapping at runtime.
/// </summary>
public sealed class SpeechEngineRouter : ISpeechEngine
{
    private readonly DeepgramFluxEngine _cloudEngine;
    private readonly WhisperLocalEngine _localEngine;
    private ISpeechEngine _activeEngine;
    private readonly object _lock = new();

    public event Action<SpeechTurnEvent>? OnSpeechEvent;
    public event Action<string, bool, string>? OnTranscript;
    public event Action<string>? OnStatusChanged;
    public event Action<double>? OnAudioLevel;

    public bool IsListening => _activeEngine.IsListening;
    public SpeechEngineMode Mode => _activeEngine.Mode;

    public SpeechEngineRouter(DeepgramFluxEngine cloudEngine, WhisperLocalEngine localEngine, SpeechEngineMode defaultMode = SpeechEngineMode.CloudDeepgram)
    {
        _cloudEngine = cloudEngine;
        _localEngine = localEngine;
        _activeEngine = defaultMode == SpeechEngineMode.CloudDeepgram ? _cloudEngine : _localEngine;

        WireEvents(_cloudEngine);
        WireEvents(_localEngine);
    }

    private void WireEvents(ISpeechEngine engine)
    {
        engine.OnSpeechEvent += ev => { if (_activeEngine == engine) OnSpeechEvent?.Invoke(ev); };
        engine.OnTranscript += (tr, fin, tid) => { if (_activeEngine == engine) OnTranscript?.Invoke(tr, fin, tid); };
        engine.OnStatusChanged += status => { if (_activeEngine == engine) OnStatusChanged?.Invoke(status); };
        engine.OnAudioLevel += level => { if (_activeEngine == engine) OnAudioLevel?.Invoke(level); };
    }

    public async Task SwitchModeAsync(SpeechEngineMode newMode)
    {
        if (_activeEngine.Mode == newMode) return;

        bool wasListening = _activeEngine.IsListening;
        await _activeEngine.StopAsync();

        lock (_lock)
        {
            _activeEngine = newMode == SpeechEngineMode.CloudDeepgram ? _cloudEngine : _localEngine;
        }

        OnStatusChanged?.Invoke($"Switched to {newMode}");

        if (wasListening)
        {
            await _activeEngine.StartAsync();
        }
    }

    public Task StartAsync(CancellationToken ct = default) => _activeEngine.StartAsync(ct);

    public Task StopAsync() => _activeEngine.StopAsync();

    public async ValueTask DisposeAsync()
    {
        await _cloudEngine.DisposeAsync();
        await _localEngine.DisposeAsync();
    }
}
