using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Deepgram;
using Deepgram.Models.Listen.v2.WebSocket;
using WiraVoice.Core.Config;
using Xunit;
using Xunit.Abstractions;

namespace WiraVoice.Tests;

[Collection("Deepgram")]
public class DeepgramLiveIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public DeepgramLiveIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task StreamSpeechWav_TranscribesSuccessfully()
    {
        string wavPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\temp_speech.wav"));
        if (!File.Exists(wavPath))
        {
            wavPath = @"D:\Vibe_\wira-voice\temp_speech.wav";
        }
        if (!File.Exists(wavPath))
        {
            _output.WriteLine("temp_speech.wav not present; skipping live audio test.");
            return;
        }

        EnvironmentConfig.Initialize();
        var apiKey = EnvironmentConfig.DeepgramApiKey;
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "ApiKey must be present");

        Library.Initialize();
        var client = new ListenWebSocketClient(apiKey);

        string? recognized = null;
        bool speechFinalReceived = false;

        await client.Subscribe(new EventHandler<OpenResponse>((s, e) =>
        {
            _output.WriteLine("[Event] OpenResponse received!");
        }));

        await client.Subscribe(new EventHandler<ResultResponse>((s, e) =>
        {
            var text = e.Channel?.Alternatives?[0]?.Transcript;
            _output.WriteLine($"[ResultResponse] isFinal={e.IsFinal}, speechFinal={e.SpeechFinal}: \"{text}\"");
            if (!string.IsNullOrWhiteSpace(text))
            {
                recognized = text;
            }
            if (e.SpeechFinal == true)
            {
                speechFinalReceived = true;
            }
        }));

        await client.Subscribe(new EventHandler<ErrorResponse>((s, e) =>
        {
            _output.WriteLine($"[Event] ErrorResponse: {e.Message}");
        }));

        await client.Subscribe(new EventHandler<CloseResponse>((s, e) =>
        {
            _output.WriteLine("[Event] CloseResponse received!");
        }));

        var schema = new LiveSchema()
        {
            Model = "nova-3",
            Language = "en",
            SmartFormat = true,
            InterimResults = true,
            Punctuate = true,
            Encoding = "linear16",
            SampleRate = 16000,
            Channels = 1
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _output.WriteLine("Connecting to Deepgram Nova-3...");
        bool connected = await client.Connect(schema, cts);
        Assert.True(connected, "Should connect");

        byte[] wavBytes = await File.ReadAllBytesAsync(wavPath);
        byte[] pcmData = new byte[wavBytes.Length - 44];
        Buffer.BlockCopy(wavBytes, 44, pcmData, 0, pcmData.Length);

        _output.WriteLine($"Streaming {pcmData.Length} bytes of PCM speech...");

        // Stream in 100ms chunks (3200 bytes per chunk at 16kHz 16-bit mono)
        int chunkSize = 3200;
        for (int offset = 0; offset < pcmData.Length; offset += chunkSize)
        {
            int count = Math.Min(chunkSize, pcmData.Length - offset);
            byte[] chunk = new byte[count];
            Buffer.BlockCopy(pcmData, offset, chunk, 0, count);

            client.Send(chunk, count);
            await Task.Delay(100);
        }

        // Send 1.5 seconds of trailing silence so endpointing triggers
        byte[] silence = new byte[3200];
        for (int i = 0; i < 15; i++)
        {
            client.Send(silence, silence.Length);
            await Task.Delay(100);
        }

        // Wait up to 5 seconds for transcription
        for (int i = 0; i < 50 && string.IsNullOrWhiteSpace(recognized); i++)
        {
            await Task.Delay(100);
        }

        await client.Stop();

        _output.WriteLine($"Final recognized text: \"{recognized}\", SpeechFinal: {speechFinalReceived}");
        Assert.NotNull(recognized);
        Assert.Contains("Chrome", recognized, StringComparison.OrdinalIgnoreCase);
    }
}
