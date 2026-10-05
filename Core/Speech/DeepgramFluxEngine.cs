using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WiraVoice.Core.Audio;
using WiraVoice.Core.Config;

namespace WiraVoice.Core.Speech;

/// <summary>
/// Cloud Speech Engine streaming 16kHz PCM16 audio to Deepgram Flux over WebSockets.
/// Delivers conversational turn lifecycle events: StartOfTurn, EagerEndOfTurn, TurnResumed, EndOfTurn.
/// </summary>
public sealed class DeepgramFluxEngine : ISpeechEngine
{
    private ClientWebSocket? _webSocket;
    private readonly AudioCaptureService _audioCapture;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private bool _isListening = false;
    private string _activeTurnId = string.Empty;
    private readonly object _lock = new();

    public event Action<SpeechTurnEvent>? OnSpeechEvent;
    public event Action<string, bool, string>? OnTranscript;
    public event Action<string>? OnStatusChanged;
    public event Action<double>? OnAudioLevel;

    public bool IsListening => _isListening;
    public SpeechEngineMode Mode => SpeechEngineMode.CloudDeepgram;

    public DeepgramFluxEngine(AudioCaptureService audioCapture)
    {
        _audioCapture = audioCapture;
        _audioCapture.OnAudioDataAvailable += OnAudioChunk;
        _audioCapture.OnAudioLevel += level => OnAudioLevel?.Invoke(level);
        _audioCapture.OnError += err => OnStatusChanged?.Invoke(err);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_isListening) return;

        string apiKey = EnvironmentConfig.DeepgramApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            OnStatusChanged?.Invoke("Deepgram API Key is missing. Set DEEPGRAM_API_KEY in .env");
            return;
        }

        try
        {
            _cts = new CancellationTokenSource();
            _webSocket = new ClientWebSocket();
            _webSocket.Options.SetRequestHeader("Authorization", $"Token {apiKey}");

            var uri = new Uri("wss://api.deepgram.com/v2/listen?model=flux-general-en&encoding=linear16&sample_rate=16000&eager_eot_threshold=0.5&eot_threshold=0.7");
            OnStatusChanged?.Invoke("Connecting to Deepgram Flux…");

            await _webSocket.ConnectAsync(uri, _cts.Token);
            _isListening = true;
            OnStatusChanged?.Invoke("Listening (Cloud Deepgram)…");

            _audioCapture.Start();
            _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
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
        if (!_isListening || _webSocket?.State != WebSocketState.Open) return;

        try
        {
            _webSocket.SendAsync(
                new ArraySegment<byte>(pcmData),
                WebSocketMessageType.Binary,
                true,
                CancellationToken.None
            ).Wait(100);
        }
        catch { }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var ms = new MemoryStream();

        while (!ct.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
        {
            try
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await StopAsync();
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                string json = Encoding.UTF8.GetString(ms.ToArray());
                ProcessMessage(json);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"Speech receive error: {ex.Message}");
                break;
            }
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("type", out var typeProp))
            {
                string msgType = typeProp.GetString() ?? string.Empty;
                if (msgType == "Error")
                {
                    string description = root.TryGetProperty("description", out var desc) ? desc.GetString() ?? "Unknown error" : "Unknown error";
                    OnStatusChanged?.Invoke($"Deepgram error: {description}");
                    return;
                }

                if (msgType == "TurnInfo")
                {
                    string eventType = root.TryGetProperty("event", out var ev) ? ev.GetString() ?? "" : "";
                    string transcript = root.TryGetProperty("transcript", out var tr) ? tr.GetString() ?? "" : "";
                    int turnIndex = root.TryGetProperty("turn_index", out var ti) ? ti.GetInt32() : 0;
                    string turnId = $"turn_{turnIndex}";
                    _activeTurnId = turnId;

                    switch (eventType)
                    {
                        case "StartOfTurn":
                            OnSpeechEvent?.Invoke(new SpeechTurnEvent(string.Empty, SpeechTurnType.StartOfTurn, turnId));
                            break;

                        case "EagerEndOfTurn":
                            if (!string.IsNullOrWhiteSpace(transcript))
                            {
                                OnTranscript?.Invoke(transcript, false, turnId);
                                OnSpeechEvent?.Invoke(new SpeechTurnEvent(transcript, SpeechTurnType.EagerEndOfTurn, turnId));
                            }
                            break;

                        case "TurnResumed":
                            OnSpeechEvent?.Invoke(new SpeechTurnEvent(transcript, SpeechTurnType.TurnResumed, turnId));
                            break;

                        case "EndOfTurn":
                            OnTranscript?.Invoke(transcript, true, turnId);
                            OnSpeechEvent?.Invoke(new SpeechTurnEvent(transcript, SpeechTurnType.EndOfTurn, turnId));
                            break;

                        default:
                            if (!string.IsNullOrWhiteSpace(transcript))
                            {
                                OnTranscript?.Invoke(transcript, false, turnId);
                            }
                            break;
                    }
                }
            }
        }
        catch { }
    }

    public async Task StopAsync()
    {
        _isListening = false;
        _audioCapture.Stop();

        try
        {
            _cts?.Cancel();
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
        }
        catch { }

        _webSocket?.Dispose();
        _webSocket = null;
        _cts?.Dispose();
        _cts = null;

        OnStatusChanged?.Invoke("Ready");
        OnAudioLevel?.Invoke(0.0);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
