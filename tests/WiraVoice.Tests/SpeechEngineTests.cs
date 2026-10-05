using System;
using System.Threading;
using System.Threading.Tasks;
using WiraVoice.Core.Config;
using WiraVoice.Core.Speech;
using Xunit;

namespace WiraVoice.Tests;

public class FakeSpeechEngine : ISpeechEngine
{
    public event Action<SpeechTurnEvent>? OnSpeechEvent;
    public event Action<string, bool, string>? OnTranscript;
    public event Action<string>? OnStatusChanged;
    public event Action<double>? OnAudioLevel;

    public bool IsListening { get; private set; } = false;
    public SpeechEngineMode Mode { get; }

    public FakeSpeechEngine(SpeechEngineMode mode)
    {
        Mode = mode;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        IsListening = true;
        OnStatusChanged?.Invoke("Listening");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        IsListening = false;
        OnStatusChanged?.Invoke("Ready");
        return Task.CompletedTask;
    }

    public void SimulateSpeechTurn(string utterance, SpeechTurnType type, string turnId)
    {
        OnSpeechEvent?.Invoke(new SpeechTurnEvent(utterance, type, turnId));
    }

    public void SimulateTranscript(string text, bool isFinal, string turnId)
    {
        OnTranscript?.Invoke(text, isFinal, turnId);
    }

    public ValueTask DisposeAsync()
    {
        IsListening = false;
        return ValueTask.CompletedTask;
    }
}

public class SpeechEngineTests
{
    [Fact]
    public void EnvironmentConfig_InitializesWithDefaults()
    {
        EnvironmentConfig.Initialize();
        Assert.NotNull(EnvironmentConfig.WhisperModelPath);
        Assert.NotNull(EnvironmentConfig.JevProvider);
    }

    [Fact]
    public void SpeechTurnEvent_PreservesProperties()
    {
        var ev = new SpeechTurnEvent("click submit", SpeechTurnType.EagerEndOfTurn, "turn_1");
        Assert.Equal("click submit", ev.Utterance);
        Assert.Equal(SpeechTurnType.EagerEndOfTurn, ev.Type);
        Assert.Equal("turn_1", ev.TurnId);
    }

    [Fact]
    public void FakeSpeechEngine_EmitsTurnEvents()
    {
        var engine = new FakeSpeechEngine(SpeechEngineMode.CloudDeepgram);
        SpeechTurnEvent? received = null;

        engine.OnSpeechEvent += ev => received = ev;
        engine.SimulateSpeechTurn("type hello", SpeechTurnType.StartOfTurn, "turn_42");

        Assert.NotNull(received);
        Assert.Equal("type hello", received.Utterance);
        Assert.Equal(SpeechTurnType.StartOfTurn, received.Type);
        Assert.Equal("turn_42", received.TurnId);
    }
}
