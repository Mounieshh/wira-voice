using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;
using WiraVoice.Core.Audio;
using WiraVoice.Core.Config;

namespace WiraVoice.Core.Speech;

/// <summary>
/// Offline Speech Engine running Whisper locally in-process.
/// Uses Voice Activity Detection (VAD) to trigger StartOfTurn and EndOfTurn events.
/// </summary>
public sealed class WhisperLocalEngine : ISpeechEngine
{
    private readonly AudioCaptureService _audioCapture;
    private WhisperFactory? _whisperFactory;
    private WhisperProcessor? _processor;
    private readonly MemoryStream _audioBuffer = new();
    private readonly object _bufferLock = new();
    private readonly System.Collections.Generic.Queue<byte[]> _preRollChunks = new();
    private int _preRollBytes = 0;
    private const int MaxPreRollBytes = 32000; // 500ms of 16kHz 16-bit mono PCM
    private bool _isListening = false;
    private bool _inSpeech = false;
    private DateTime _lastSpeechTime = DateTime.MinValue;
    private int _turnCounter = 0;
    private string _currentTurnId = string.Empty;
    private CancellationTokenSource? _vadCts;

    public event Action<SpeechTurnEvent>? OnSpeechEvent;
    public event Action<string, bool, string>? OnTranscript;
    public event Action<string>? OnStatusChanged;
    public event Action<double>? OnAudioLevel;

    public bool IsListening => _isListening;
    public SpeechEngineMode Mode => SpeechEngineMode.LocalWhisper;

    public WhisperLocalEngine(AudioCaptureService audioCapture)
    {
        _audioCapture = audioCapture;
        _audioCapture.OnAudioDataAvailable += OnAudioChunk;
        _audioCapture.OnAudioLevel += OnAudioLevelReceived;
        _audioCapture.OnError += err => OnStatusChanged?.Invoke(err);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_isListening) return;

        string modelPath = EnvironmentConfig.WhisperModelPath;
        if (!File.Exists(modelPath))
        {
            modelPath = ResolvePath(modelPath) ?? modelPath;
        }

        if (!File.Exists(modelPath))
        {
            OnStatusChanged?.Invoke($"Whisper model not found at {modelPath}");
            return;
        }

        try
        {
            OnStatusChanged?.Invoke("Loading local Whisper model…");
            _whisperFactory = WhisperFactory.FromPath(modelPath);
            _processor = _whisperFactory.CreateBuilder()
                .WithLanguage("en")
                .WithThreads(Environment.ProcessorCount)
                .Build();

            _isListening = true;
            _vadCts = new CancellationTokenSource();
            _audioCapture.Start();

            // Background task monitoring end-of-speech silence
            _ = Task.Run(() => MonitorSilenceLoopAsync(_vadCts.Token));

            OnStatusChanged?.Invoke("Listening (Local Whisper)…");
        }
        catch (Exception ex)
        {
            _isListening = false;
            var errMsg = $"Whisper initialization failed: {ex.Message}";
            await StopAsync();
            OnStatusChanged?.Invoke(errMsg);
        }
    }

    private void OnAudioLevelReceived(double level)
    {
        OnAudioLevel?.Invoke(level);

        if (!_isListening) return;

        // VAD Speech Start Threshold (0.025 captures natural speech)
        if (level > 0.025)
        {
            _lastSpeechTime = DateTime.UtcNow;

            if (!_inSpeech)
            {
                _inSpeech = true;
                _turnCounter++;
                _currentTurnId = $"local_turn_{_turnCounter}";

                lock (_bufferLock)
                {
                    _audioBuffer.SetLength(0);

                    // Replay pre-roll audio so initial words are completely preserved
                    while (_preRollChunks.Count > 0)
                    {
                        var chunk = _preRollChunks.Dequeue();
                        _audioBuffer.Write(chunk, 0, chunk.Length);
                    }
                    _preRollBytes = 0;
                }

                OnSpeechEvent?.Invoke(new SpeechTurnEvent(string.Empty, SpeechTurnType.StartOfTurn, _currentTurnId));
            }
        }
    }

    private void OnAudioChunk(byte[] pcmChunk)
    {
        if (!_isListening) return;

        lock (_bufferLock)
        {
            if (_inSpeech)
            {
                _audioBuffer.Write(pcmChunk, 0, pcmChunk.Length);
            }
            else
            {
                // Maintain 500ms rolling pre-roll buffer when silent
                _preRollChunks.Enqueue(pcmChunk);
                _preRollBytes += pcmChunk.Length;
                while (_preRollBytes > MaxPreRollBytes && _preRollChunks.Count > 0)
                {
                    var dropped = _preRollChunks.Dequeue();
                    _preRollBytes -= dropped.Length;
                }
            }
        }
    }

    private async Task MonitorSilenceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _isListening)
        {
            await Task.Delay(100, ct);

            // Silence duration threshold: 550ms of silence indicates EndOfTurn
            if (_inSpeech && (DateTime.UtcNow - _lastSpeechTime).TotalMilliseconds > 550)
            {
                _inSpeech = false;
                await TranscribeBufferedAudioAsync();
            }
        }
    }

    private async Task TranscribeBufferedAudioAsync()
    {
        byte[] pcmData;
        lock (_bufferLock)
        {
            pcmData = _audioBuffer.ToArray();
            _audioBuffer.SetLength(0);
        }

        // Need at least ~250ms of audio (16000 * 2 bytes/sec * 0.25 = 8000 bytes)
        if (pcmData.Length < 8000 || _processor == null) return;

        try
        {
            OnStatusChanged?.Invoke("Transcribing locally…");
            var sb = new StringBuilder();

            using var memStream = new MemoryStream(pcmData);
            await foreach (var result in _processor.ProcessAsync(memStream))
            {
                sb.Append(result.Text);
            }

            string transcript = sb.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(transcript))
            {
                OnTranscript?.Invoke(transcript, true, _currentTurnId);
                OnSpeechEvent?.Invoke(new SpeechTurnEvent(transcript, SpeechTurnType.EndOfTurn, _currentTurnId));
            }

            OnStatusChanged?.Invoke("Listening (Local Whisper)…");
        }
        catch (Exception ex)
        {
            OnStatusChanged?.Invoke($"Transcription error: {ex.Message}");
        }
    }

    public Task StopAsync()
    {
        _isListening = false;
        _inSpeech = false;
        _audioCapture.Stop();
        _vadCts?.Cancel();
        _vadCts?.Dispose();
        _vadCts = null;

        _processor?.Dispose();
        _whisperFactory?.Dispose();
        _processor = null;
        _whisperFactory = null;

        lock (_bufferLock)
        {
            _audioBuffer.SetLength(0);
            _preRollChunks.Clear();
            _preRollBytes = 0;
        }

        OnStatusChanged?.Invoke("Ready");
        OnAudioLevel?.Invoke(0.0);
        return Task.CompletedTask;
    }

    public static string? ResolvePath(string relativeOrFileName)
    {
        if (File.Exists(relativeOrFileName)) return Path.GetFullPath(relativeOrFileName);

        var dirsToTry = new[]
        {
            AppDomain.CurrentDomain.BaseDirectory,
            Directory.GetCurrentDirectory()
        };

        foreach (var startDir in dirsToTry)
        {
            var dir = new DirectoryInfo(startDir);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, relativeOrFileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                dir = dir.Parent;
            }
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
