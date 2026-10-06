using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using WiraVoice.Core.Audio;
using Xunit;

namespace WiraVoice.Tests;

public class AudioCaptureTests
{
    [Fact]
    public void CalculateLevel_WithWaveFormatExtensible_ReturnsAccurateLevel()
    {
        // Simulate WASAPI default mix format: 48000Hz, 2 channels, 32-bit float extensible
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        // Also simulate extensible format
        var extensibleFormat = new WaveFormatExtensible(48000, 32, 2);

        // Generate 100ms of simulated speech audio (amplitude 0.25)
        int sampleCount = 4800 * 2;
        byte[] buffer = new byte[sampleCount * 4];
        for (int i = 0; i < sampleCount; i++)
        {
            float sample = 0.25f * (float)Math.Sin(2 * Math.PI * 440 * i / 48000.0);
            byte[] bytes = BitConverter.GetBytes(sample);
            Buffer.BlockCopy(bytes, 0, buffer, i * 4, 4);
        }

        // Test with standard IEEE float
        double levelFloat = AudioCaptureService.CalculateLevel(buffer, buffer.Length, format);
        Assert.True(levelFloat > 0.1, $"Expected level > 0.1 for float format, got {levelFloat}");

        // Test with WaveFormatExtensible (was failing and returning 0.0)
        double levelExtensible = AudioCaptureService.CalculateLevel(buffer, buffer.Length, extensibleFormat);
        Assert.True(levelExtensible > 0.1, $"Expected level > 0.1 for extensible format, got {levelExtensible}");
    }

    [Fact]
    public void CalculateLevel_With16BitPcm_ReturnsAccurateLevel()
    {
        var format = new WaveFormat(16000, 16, 1);
        int sampleCount = 1600;
        byte[] buffer = new byte[sampleCount * 2];
        for (int i = 0; i < sampleCount; i++)
        {
            short sample = (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / 16000.0));
            byte[] bytes = BitConverter.GetBytes(sample);
            Buffer.BlockCopy(bytes, 0, buffer, i * 2, 2);
        }

        double level = AudioCaptureService.CalculateLevel(buffer, buffer.Length, format);
        Assert.True(level > 0.1, $"Expected level > 0.1 for 16-bit format, got {level}");
    }

    [Fact]
    public void LiveMicrophone_CapturesAudioAndLevels()
    {
        using var audio = new AudioCaptureService();
        int chunksReceived = 0;
        double maxLevel = 0.0;
        string? error = null;

        audio.OnError += err => error = err;
        audio.OnAudioDataAvailable += chunk => chunksReceived++;
        audio.OnAudioLevel += lvl => { if (lvl > maxLevel) maxLevel = lvl; };

        audio.Start();
        System.Threading.Thread.Sleep(600);
        audio.Stop();

        Assert.Null(error);
        Assert.True(chunksReceived > 0, $"No audio chunks received from microphone! Error: {error}");
    }

    [Fact]
    public void Resampler_WithBufferedWaveProvider_DrainsWithoutHanging()
    {
        var inputFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var targetFormat = new WaveFormat(16000, 16, 1);
        var buffered = new BufferedWaveProvider(inputFormat)
        {
            DiscardOnBufferOverflow = true,
            ReadFully = false,
            BufferDuration = TimeSpan.FromSeconds(5)
        };
        using var resampler = new MediaFoundationResampler(buffered, targetFormat);

        // Add 20ms of audio
        byte[] dummy = new byte[960 * 2 * 4];
        buffered.AddSamples(dummy, 0, dummy.Length);

        byte[] output = new byte[4096];
        int totalRead = 0;
        int bytesRead;
        while ((bytesRead = resampler.Read(output, 0, output.Length)) > 0)
        {
            totalRead += bytesRead;
        }

        Assert.True(totalRead > 0, $"Expected resampled bytes, got {totalRead}");
    }

    [Fact]
    public void Resampler_WithWaveFormatExtensible_ConvertsSineWaveAccurately()
    {
        // 48kHz stereo 32-bit float extensible
        var inputFormat = new WaveFormatExtensible(48000, 32, 2);
        var targetFormat = new WaveFormat(16000, 16, 1);
        var buffered = new BufferedWaveProvider(inputFormat)
        {
            DiscardOnBufferOverflow = true,
            ReadFully = false,
            BufferDuration = TimeSpan.FromSeconds(5)
        };
        using var resampler = new MediaFoundationResampler(buffered, targetFormat);

        // Generate 100ms of 440Hz sine wave
        int sampleCount = 4800 * 2;
        byte[] buffer = new byte[sampleCount * 4];
        for (int i = 0; i < sampleCount; i++)
        {
            float sample = 0.5f * (float)Math.Sin(2 * Math.PI * 440 * i / 48000.0);
            byte[] bytes = BitConverter.GetBytes(sample);
            Buffer.BlockCopy(bytes, 0, buffer, i * 4, 4);
        }
        buffered.AddSamples(buffer, 0, buffer.Length);

        byte[] output = new byte[4096];
        int totalRead = 0;
        int bytesRead;
        while ((bytesRead = resampler.Read(output, 0, output.Length)) > 0)
        {
            totalRead += bytesRead;
        }

        Assert.True(totalRead > 0, $"Expected resampled bytes, got {totalRead}");

        // Verify non-zero PCM samples
        short maxSample = 0;
        for (int i = 0; i < totalRead; i += 2)
        {
            short s = Math.Abs(BitConverter.ToInt16(output, i));
            if (s > maxSample) maxSample = s;
        }

        Assert.True(maxSample > 1000, $"Expected non-zero PCM samples, but max was {maxSample}!");
    }
}
