using System;
using System.Threading.Tasks;
using Deepgram.Models.Listen.v2.WebSocket;
using WiraVoice.Core.Audio;
using WiraVoice.Core.Config;
using WiraVoice.Core.Speech;
using Xunit;

namespace WiraVoice.Tests;

[Collection("Deepgram")]
public class DeepgramFluxEngineTests
{
    [Fact]
    public void DeepgramFluxEngine_InitializesWithCorrectMode()
    {
        using var audio = new AudioCaptureService();
        var engine = new DeepgramFluxEngine(audio);

        Assert.Equal(SpeechEngineMode.CloudDeepgram, engine.Mode);
        Assert.False(engine.IsListening);
    }

    [Fact]
    public async Task DeepgramFluxEngine_WithoutApiKey_FailsGracefully()
    {
        using var audio = new AudioCaptureService();
        var engine = new DeepgramFluxEngine(audio, () => string.Empty);

        string? statusReported = null;
        engine.OnStatusChanged += msg => statusReported = msg;

        await engine.StartAsync();

        Assert.False(engine.IsListening);
        Assert.NotNull(statusReported);
        Assert.Contains("Deepgram API Key is missing", statusReported);
    }

    [Fact]
    public async Task DeepgramFluxEngine_StopAndDispose_IsIdempotent()
    {
        using var audio = new AudioCaptureService();
        var engine = new DeepgramFluxEngine(audio);

        await engine.StopAsync();
        await engine.StopAsync();
        await engine.DisposeAsync();

        Assert.False(engine.IsListening);
    }

    [Fact]
    public void DeepgramLiveSchema_ConfiguresNova3StreamingOptions()
    {
        var schema = new LiveSchema
        {
            Model = "nova-3",
            Language = "en",
            SmartFormat = true,
            Punctuate = true,
            Encoding = "linear16",
            SampleRate = 16000,
            Channels = 1,
            InterimResults = true
        };

        Assert.Equal("nova-3", schema.Model);
        Assert.Equal("en", schema.Language);
        Assert.True(schema.SmartFormat);
        Assert.True(schema.Punctuate);
        Assert.Equal("linear16", schema.Encoding);
        Assert.Equal(16000, schema.SampleRate);
        Assert.Equal(1, schema.Channels);
        Assert.True(schema.InterimResults);
    }
}
