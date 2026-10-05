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
}
