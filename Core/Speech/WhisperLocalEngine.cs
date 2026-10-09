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
    private const int MaxPreRollBytes = 12800; // 400ms of 16kHz 16-bit mono PCM (16000 * 2 * 0.40)
    public const double SpeechStartThreshold = 0.085; // Audio level to trigger speech start from silence (above 0.04-0.08 room noise)
    public const double SpeechContinueThreshold = 0.020; // Audio level to keep recording while speaking (above room noise floor)
    public const int SilenceTimeoutMs = 900; // 900ms natural conversational pause window before finalizing utterance
    private bool _isListening = false;
    private bool _inSpeech = false;
    private DateTime _speechStartTime = DateTime.MinValue;
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

        // Automatic discovery: if dedicated English model (ggml-base.en.bin) is present, prefer it for higher accuracy
        var enCandidate = ResolvePath("models/ggml-base.en.bin");
        if (File.Exists(enCandidate))
        {
            modelPath = enCandidate;
        }

        if (!File.Exists(modelPath))
        {
            OnStatusChanged?.Invoke($"Whisper model not found at {modelPath}");
            return;
        }

        try
        {
            Console.WriteLine($"[SpeechEngine:Whisper] Loading local Whisper model from: {modelPath}...");
            OnStatusChanged?.Invoke("Loading local Whisper model…");
            _whisperFactory = WhisperFactory.FromPath(modelPath);
            _processor = _whisperFactory.CreateBuilder()
                .WithLanguage("en")
                .WithThreads(Environment.ProcessorCount)
                .WithPrompt("Select, click, type, open, submit, scroll, close, cancel, account, window, tab, browser.")
                .Build();

            _isListening = true;
            _vadCts = new CancellationTokenSource();
            _audioCapture.Start();

            // Background task monitoring end-of-speech silence
            _ = Task.Run(() => MonitorSilenceLoopAsync(_vadCts.Token));

            Console.WriteLine($"[SpeechEngine:Whisper] Ready. Listening for microphone input...");
            OnStatusChanged?.Invoke("Listening (Local Whisper)…");
        }
        catch (Exception ex)
        {
            _isListening = false;
            var errMsg = $"Whisper initialization failed: {ex.Message}";
            Console.WriteLine($"[SpeechEngine:Whisper] Error: {errMsg}");
            await StopAsync();
            OnStatusChanged?.Invoke(errMsg);
        }
    }

    private void OnAudioLevelReceived(double level)
    {
        OnAudioLevel?.Invoke(level);

        if (!_isListening) return;

        if (!_inSpeech)
        {
            // VAD Speech Start: requires clear vocal energy to begin recording
            if (level >= SpeechStartThreshold)
            {
                _inSpeech = true;
                _speechStartTime = DateTime.UtcNow;
                _lastSpeechTime = DateTime.UtcNow;
                _turnCounter++;
                _currentTurnId = $"local_turn_{_turnCounter}";
                Console.WriteLine($"[Voice] Speech detected (level: {level:F2}) -> Recording voice audio...");
                OnStatusChanged?.Invoke("Listening to voice…");
                OnSpeechEvent?.Invoke(new SpeechTurnEvent(string.Empty, SpeechTurnType.StartOfTurn, _currentTurnId));

                lock (_bufferLock)
                {
                    _audioBuffer.SetLength(0);

                    // Replay 400ms pre-roll audio so initial consonant attack is preserved
                    while (_preRollChunks.Count > 0)
                    {
                        var chunk = _preRollChunks.Dequeue();
                        _audioBuffer.Write(chunk, 0, chunk.Length);
                    }
                    _preRollBytes = 0;
                }
            }
        }
        else
        {
            // VAD Speech Continue: much lower threshold to maintain turn through soft consonants and inter-word pacing
            if (level >= SpeechContinueThreshold)
            {
                _lastSpeechTime = DateTime.UtcNow;
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
                // Maintain 250ms rolling pre-roll buffer when silent
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
            await Task.Delay(80, ct).ConfigureAwait(false);

            // Silence duration threshold: 1.1s allows natural speech cadence without cutting phrases mid-sentence
            if (_inSpeech && (DateTime.UtcNow - _lastSpeechTime).TotalMilliseconds > SilenceTimeoutMs)
            {
                _inSpeech = false;
                await TranscribeBufferedAudioAsync().ConfigureAwait(false);
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

        // Trim trailing room silence before Whisper processing to stop hallucinations
        pcmData = TrimTrailingSilence(pcmData);

        var speechDuration = (DateTime.UtcNow - _speechStartTime).TotalSeconds;
        // Require at least 250ms of audio and 150ms of active speech duration to discard transient clicks
        if (pcmData.Length < 8000 || speechDuration < 0.15 || _processor == null)
        {
            OnStatusChanged?.Invoke("Listening (Local Whisper)…");
            return;
        }

        double durationSec = pcmData.Length / (16000.0 * 2.0);
        Console.WriteLine($"[Voice] End of speech ({durationSec:F1}s audio). Transcribing with Local Whisper...");

        try
        {
            OnStatusChanged?.Invoke("Transcribing speech locally…");
            var sb = new StringBuilder();

            int sampleCount = pcmData.Length / 2;
            float[] samples = new float[sampleCount];

            // 1. Calculate peak amplitude for adaptive gain normalization
            float maxAbs = 0f;
            for (int i = 0; i < sampleCount; i++)
            {
                short sample = BitConverter.ToInt16(pcmData, i * 2);
                float val = Math.Abs(sample / 32768.0f);
                if (val > maxAbs) maxAbs = val;
            }

            // 2. Adaptive auto-gain: boost quiet speech up to 12x targeting peak ~0.65
            float gain = 1.0f;
            if (maxAbs > 0.003f && maxAbs < 0.35f)
            {
                gain = Math.Clamp(0.65f / maxAbs, 1.0f, 12.0f);
            }

            for (int i = 0; i < sampleCount; i++)
            {
                short sample = BitConverter.ToInt16(pcmData, i * 2);
                samples[i] = Math.Clamp((sample / 32768.0f) * gain, -1.0f, 1.0f);
            }

            await foreach (var result in _processor.ProcessAsync(samples))
            {
                if (!string.IsNullOrWhiteSpace(result.Text))
                {
                    sb.Append(result.Text);
                }
            }

            string rawTranscript = sb.ToString().Trim();
            string cleanTranscript = CleanTranscript(rawTranscript);

            if (!string.IsNullOrWhiteSpace(cleanTranscript))
            {
                Console.WriteLine($"[SpeechEngine:Whisper] Transcribed: \"{cleanTranscript}\" (turn: {_currentTurnId})");
                OnTranscript?.Invoke(cleanTranscript, true, _currentTurnId);
                OnSpeechEvent?.Invoke(new SpeechTurnEvent(cleanTranscript, SpeechTurnType.EndOfTurn, _currentTurnId));
            }
            else
            {
                Console.WriteLine($"[SpeechEngine:Whisper] Non-speech audio detected (raw: \"{rawTranscript}\"). Discarding noise.");
            }

            OnStatusChanged?.Invoke("Listening (Local Whisper)…");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SpeechEngine:Whisper] Transcription error: {ex.Message}");
            OnStatusChanged?.Invoke($"Transcription error: {ex.Message}");
        }
    }

    public static string CleanTranscript(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        string trimmed = text.Trim();

        // Discard Whisper special tokens (e.g. [BLANK_AUDIO], [silence], etc.)
        if (trimmed.Equals("[BLANK_AUDIO]", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("[BLANK AUDIO]", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("[SILENCE]", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("[MUSIC]", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("[APPLAUSE]", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("[LAUGHTER]", StringComparison.OrdinalIgnoreCase) ||
            (trimmed.StartsWith("[") && trimmed.EndsWith("]")) ||
            (trimmed.StartsWith("(") && trimmed.EndsWith(")")))
        {
            return string.Empty;
        }

        // Strip bracket tokens embedded in text (e.g. "[BLANK_AUDIO] Click Submit" -> "Click Submit")
        var cleaned = System.Text.RegularExpressions.Regex.Replace(trimmed, @"\[[A-Za-z0-9_\s]+\]|\([A-Za-z0-9_\s]+\)", "").Trim();

        if (!cleaned.Any(char.IsLetterOrDigit))
        {
            return string.Empty;
        }

        return cleaned;
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

    /// <summary>
    /// Trims trailing low-energy silence from 16kHz 16-bit mono PCM, retaining a short padding
    /// window to preserve terminal consonants while eliminating dead air hallucinations in Whisper.
    /// </summary>
    public static byte[] TrimTrailingSilence(byte[] pcmData, int sampleRate = 16000, double minThreshold = 0.015, int keepPaddingMs = 150)
    {
        if (pcmData == null || pcmData.Length < 4) return pcmData ?? [];
        int totalSamples = pcmData.Length / 2;
        int paddingSamples = (sampleRate * keepPaddingMs) / 1000;
        int lastActiveSample = totalSamples - 1;

        // Scan backward in 10ms windows (160 samples)
        int window = sampleRate / 100;
        for (int i = totalSamples - window; i >= 0; i -= window)
        {
            double sumSquares = 0;
            for (int j = 0; j < window; j++)
            {
                short s = BitConverter.ToInt16(pcmData, (i + j) * 2);
                double norm = s / 32768.0;
                sumSquares += norm * norm;
            }
            double rms = Math.Sqrt(sumSquares / window);
            if (rms >= minThreshold)
            {
                lastActiveSample = Math.Min(totalSamples, i + window + paddingSamples);
                break;
            }
        }

        int trimmedBytes = Math.Min(pcmData.Length, Math.Max(lastActiveSample * 2, sampleRate / 2 * 2));
        if (trimmedBytes >= pcmData.Length) return pcmData;

        var result = new byte[trimmedBytes];
        Buffer.BlockCopy(pcmData, 0, result, 0, trimmedBytes);
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
