using System;
using System.Threading;
using System.Threading.Tasks;
using Deepgram;
using Deepgram.Models.Listen.v2.WebSocket;
using WiraVoice.Core.Audio;
using WiraVoice.Core.Config;

namespace WiraVoice.Core.Speech;

/// <summary>
/// Cloud Speech Engine streaming 16kHz PCM16 audio to Deepgram Nova-3 using the official Deepgram .NET SDK.
/// Delivers real-time neural transcription, live VAD endpointing, and turn lifecycle events.
/// </summary>
public sealed class DeepgramFluxEngine : ISpeechEngine
{
    private ListenWebSocketClient? _liveClient;
    private readonly AudioCaptureService _audioCapture;
    private readonly Func<string>? _apiKeyProvider;
    private bool _isListening = false;
    private int _turnCounter = 0;
    private string _activeTurnId = "turn_1";
    private string _accumulatedTranscript = string.Empty;
    private readonly object _lock = new();

    public event Action<SpeechTurnEvent>? OnSpeechEvent;
    public event Action<string, bool, string>? OnTranscript;
    public event Action<string>? OnStatusChanged;
    public event Action<double>? OnAudioLevel;

    public bool IsListening => _isListening;
    public SpeechEngineMode Mode => SpeechEngineMode.CloudDeepgram;

    public DeepgramFluxEngine(AudioCaptureService audioCapture, Func<string>? apiKeyProvider = null)
    {
        _audioCapture = audioCapture;
        _apiKeyProvider = apiKeyProvider;
        _audioCapture.OnAudioDataAvailable += OnAudioChunk;
        _audioCapture.OnAudioLevel += level => OnAudioLevel?.Invoke(level);
        _audioCapture.OnError += err => OnStatusChanged?.Invoke(err);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_isListening) return;

        string apiKey = _apiKeyProvider != null ? _apiKeyProvider() : EnvironmentConfig.DeepgramApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            OnStatusChanged?.Invoke("Deepgram API Key is missing. Set DEEPGRAM_API_KEY in .env");
            return;
        }

        try
        {
            Library.Initialize();
            _liveClient = new ListenWebSocketClient(apiKey);

            await _liveClient.Subscribe(new EventHandler<OpenResponse>((sender, e) =>
            {
                OnStatusChanged?.Invoke("Listening (Cloud Deepgram Nova-3)…");
                Console.WriteLine("[SpeechEngine:Deepgram] Connected to Deepgram Nova-3 WebSocket.");
            }));

            await _liveClient.Subscribe(new EventHandler<SpeechStartedResponse>((sender, e) =>
            {
                string turnId;
                lock (_lock)
                {
                    turnId = _activeTurnId;
                }
                Console.WriteLine($"[SpeechEngine:Deepgram] Speech started (turn: {turnId})");
                OnStatusChanged?.Invoke("Listening (speaking)…");
                OnSpeechEvent?.Invoke(new SpeechTurnEvent(string.Empty, SpeechTurnType.StartOfTurn, turnId));
            }));

            await _liveClient.Subscribe(new EventHandler<ResultResponse>((sender, e) =>
            {
                OnResultResponse(e);
            }));

            await _liveClient.Subscribe(new EventHandler<UtteranceEndResponse>((sender, e) =>
            {
                FinalizeTurn();
            }));

            await _liveClient.Subscribe(new EventHandler<CloseResponse>((sender, e) =>
            {
                Console.WriteLine("[SpeechEngine:Deepgram] Connection closed.");
                OnStatusChanged?.Invoke("Deepgram disconnected");
            }));

            await _liveClient.Subscribe(new EventHandler<ErrorResponse>((sender, e) =>
            {
                Console.WriteLine($"[SpeechEngine:Deepgram] Error: {e.Message}");
                OnStatusChanged?.Invoke($"Deepgram error: {e.Message}");
            }));

            var liveSchema = new LiveSchema()
            {
                Model = "nova-3",
                Language = "en",
                SmartFormat = true,
                Punctuate = true,
                Encoding = "linear16",
                SampleRate = 16000,
                Channels = 1,
                InterimResults = true,
                VadEvents = true,
                EndPointing = "350",
                UtteranceEnd = "1000"
            };

            OnStatusChanged?.Invoke("Connecting to Deepgram Nova-3…");
            bool connected = await _liveClient.Connect(liveSchema);
            if (!connected)
            {
                OnStatusChanged?.Invoke("Failed to connect to Deepgram Nova-3.");
                await StopAsync();
                return;
            }

            _isListening = true;
            _turnCounter = 1;
            _activeTurnId = $"deepgram_turn_{_turnCounter}";
            _accumulatedTranscript = string.Empty;

            _audioCapture.Start();
        }
        catch (Exception ex)
        {
            _isListening = false;
            OnStatusChanged?.Invoke($"Deepgram connection failed: {ex.Message}");
            await StopAsync();
        }
    }

    private void OnAudioChunk(byte[] pcmData)
    {
        if (!_isListening || _liveClient == null || !_liveClient.IsConnected()) return;

        try
        {
            _liveClient.Send(pcmData, pcmData.Length);
        }
        catch { }
    }

    private void OnResultResponse(ResultResponse e)
    {
        if (e.Channel?.Alternatives == null || e.Channel.Alternatives.Count == 0) return;

        var alt = e.Channel.Alternatives[0];
        string segmentText = alt.Transcript?.Trim() ?? string.Empty;

        bool shouldFinalize = false;
        string? interimHypothesis = null;
        string? eagerTranscript = null;
        string currentTurnId;

        lock (_lock)
        {
            currentTurnId = _activeTurnId;
            bool isFinal = e.IsFinal == true;
            bool speechFinal = e.SpeechFinal == true;

            if (isFinal)
            {
                if (!string.IsNullOrWhiteSpace(segmentText))
                {
                    _accumulatedTranscript = string.IsNullOrWhiteSpace(_accumulatedTranscript)
                        ? segmentText
                        : $"{_accumulatedTranscript} {segmentText}";
                }

                if (speechFinal)
                {
                    shouldFinalize = true;
                }
                else if (!string.IsNullOrWhiteSpace(_accumulatedTranscript))
                {
                    eagerTranscript = _accumulatedTranscript;
                }
            }
            else
            {
                interimHypothesis = string.IsNullOrWhiteSpace(_accumulatedTranscript)
                    ? segmentText
                    : $"{_accumulatedTranscript} {segmentText}".Trim();
            }
        }

        if (shouldFinalize)
        {
            FinalizeTurn();
        }
        else if (!string.IsNullOrWhiteSpace(eagerTranscript))
        {
            OnTranscript?.Invoke(eagerTranscript, false, currentTurnId);
            OnSpeechEvent?.Invoke(new SpeechTurnEvent(eagerTranscript, SpeechTurnType.EagerEndOfTurn, currentTurnId));
        }
        else if (!string.IsNullOrWhiteSpace(interimHypothesis))
        {
            OnTranscript?.Invoke(interimHypothesis, false, currentTurnId);
        }
    }

    private void FinalizeTurn()
    {
        string rawTranscript;
        string currentTurnId;

        lock (_lock)
        {
            rawTranscript = _accumulatedTranscript.Trim();
            _accumulatedTranscript = string.Empty;

            if (string.IsNullOrWhiteSpace(rawTranscript)) return;

            currentTurnId = _activeTurnId;
            _turnCounter++;
            _activeTurnId = $"deepgram_turn_{_turnCounter}";
        }

        string cleanTranscript = WhisperLocalEngine.CleanTranscript(rawTranscript);

        if (!string.IsNullOrWhiteSpace(cleanTranscript))
        {
            Console.WriteLine($"[SpeechEngine:Deepgram] Transcribed: \"{cleanTranscript}\" (turn: {currentTurnId})");
            OnTranscript?.Invoke(cleanTranscript, true, currentTurnId);
            OnSpeechEvent?.Invoke(new SpeechTurnEvent(cleanTranscript, SpeechTurnType.EndOfTurn, currentTurnId));
        }
        else
        {
            Console.WriteLine($"[SpeechEngine:Deepgram] Non-speech audio detected (raw: \"{rawTranscript}\"). Discarding noise.");
        }

        OnStatusChanged?.Invoke("Listening (Cloud Deepgram Nova-3)…");
    }

    public async Task StopAsync()
    {
        _isListening = false;
        _audioCapture.Stop();

        try
        {
            if (_liveClient != null)
            {
                await _liveClient.Stop();
                _liveClient = null;
            }
            Library.Terminate();
        }
        catch { }

        lock (_lock)
        {
            _accumulatedTranscript = string.Empty;
        }

        OnStatusChanged?.Invoke("Ready");
        OnAudioLevel?.Invoke(0.0);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
