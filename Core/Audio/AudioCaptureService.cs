using System;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace WiraVoice.Core.Audio;

/// <summary>
/// Captures audio from the default Windows microphone via WASAPI
/// and converts it to standard 16 kHz 16-bit signed mono PCM.
/// </summary>
public sealed class AudioCaptureService : IDisposable
{
    private WasapiCapture? _capture;
    private BufferedWaveProvider? _bufferedProvider;
    private MediaFoundationResampler? _resampler;
    private byte[] _resampleBuffer = new byte[4096];
    private bool _isCapturing = false;
    private readonly object _lock = new();

    public event Action<byte[]>? OnAudioDataAvailable;
    public event Action<double>? OnAudioLevel;
    public event Action<string>? OnError;

    public bool IsCapturing => _isCapturing;

    public void Start()
    {
        lock (_lock)
        {
            if (_isCapturing) return;

            try
            {
                // Capture from default recording endpoint with low latency
                _capture = new WasapiCapture();
                WaveFormat inputFormat = _capture.WaveFormat;

                // Target format: 16,000 Hz, 16-bit, Mono (PCM)
                var targetFormat = new WaveFormat(16000, 16, 1);

                _bufferedProvider = new BufferedWaveProvider(inputFormat)
                {
                    DiscardOnBufferOverflow = true,
                    BufferDuration = TimeSpan.FromSeconds(5)
                };

                _resampler = new MediaFoundationResampler(_bufferedProvider, targetFormat)
                {
                    ResamplerQuality = 60
                };

                _capture.DataAvailable += OnCaptureDataAvailable;
                _capture.RecordingStopped += OnCaptureRecordingStopped;

                _capture.StartRecording();
                _isCapturing = true;
            }
            catch (Exception ex)
            {
                _isCapturing = false;
                OnError?.Invoke($"Microphone initialization failed: {ex.Message}");
            }
        }
    }

    private void OnCaptureDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_isCapturing || e.BytesRecorded == 0) return;

        try
        {
            // 1. Calculate audio level for UI feedback
            double level = CalculateLevel(e.Buffer, e.BytesRecorded, _capture?.WaveFormat);
            OnAudioLevel?.Invoke(level);

            // 2. Buffer raw audio for resampling
            _bufferedProvider?.AddSamples(e.Buffer, 0, e.BytesRecorded);

            // 3. Read resampled 16kHz 16-bit mono PCM samples
            if (_resampler != null)
            {
                int bytesRead;
                while ((bytesRead = _resampler.Read(_resampleBuffer, 0, _resampleBuffer.Length)) > 0)
                {
                    byte[] pcmChunk = new byte[bytesRead];
                    Buffer.BlockCopy(_resampleBuffer, 0, pcmChunk, 0, bytesRead);
                    OnAudioDataAvailable?.Invoke(pcmChunk);
                }
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Audio processing error: {ex.Message}");
        }
    }

    private static double CalculateLevel(byte[] buffer, int count, WaveFormat? format)
    {
        if (count == 0 || format == null) return 0.0;

        double sum = 0;
        int sampleCount = 0;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            for (int i = 0; i < count - 4; i += 4 * format.Channels)
            {
                float sample = BitConverter.ToSingle(buffer, i);
                sum += Math.Abs(sample);
                sampleCount++;
            }
        }
        else if (format.BitsPerSample == 16)
        {
            for (int i = 0; i < count - 2; i += 2 * format.Channels)
            {
                short sample = BitConverter.ToInt16(buffer, i);
                sum += Math.Abs(sample) / 32768.0;
                sampleCount++;
            }
        }

        if (sampleCount == 0) return 0.0;
        double avg = sum / sampleCount;
        return Math.Min(1.0, avg * 5.0); // Boost scale for smooth UI visualization
    }

    private void OnCaptureRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            OnError?.Invoke($"Microphone recording stopped: {e.Exception.Message}");
        }
        _isCapturing = false;
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_isCapturing) return;

            try
            {
                _capture?.StopRecording();
            }
            catch { }

            _isCapturing = false;
            OnAudioLevel?.Invoke(0.0);
        }
    }

    public void Dispose()
    {
        Stop();
        _capture?.Dispose();
        _resampler?.Dispose();
        _capture = null;
        _resampler = null;
        _bufferedProvider = null;
    }
}
