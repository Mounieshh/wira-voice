using System;
using System.Threading;
using System.Threading.Tasks;

namespace WiraVoice.Core.Speech;

public enum SpeechTurnType
{
    StartOfTurn,      // User begins speaking -> cancel active command permits
    EagerEndOfTurn,   // User about to finish speaking -> speculative execution trigger
    TurnResumed,      // User resumed speaking after brief pause -> cancel speculative plan
    EndOfTurn         // Speech finalized -> execute verified action plan
}

public record SpeechTurnEvent(string Utterance, SpeechTurnType Type, string TurnId);

public enum SpeechEngineMode
{
    CloudDeepgram,
    LocalWhisper
}

public interface ISpeechEngine : IAsyncDisposable
{
    event Action<SpeechTurnEvent>? OnSpeechEvent;
    event Action<string, bool, string>? OnTranscript; // transcript, isFinal, turnId
    event Action<string>? OnStatusChanged;
    event Action<double>? OnAudioLevel;

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();
    bool IsListening { get; }
    SpeechEngineMode Mode { get; }
}
