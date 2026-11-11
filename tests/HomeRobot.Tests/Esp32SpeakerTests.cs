using System.Buffers.Binary;
using System.Net;
using HomeRobot.Server.Speech;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HomeRobot.Tests;

public sealed class Esp32SpeakerTests
{
    [Theory]
    [InlineData(16000)]
    [InlineData(22050)]
    public async Task SendsOneSecondOfRawMonoPcmAt16Khz(int sourceRate)
    {
        const short sample = -1234;
        var wav = VoiceTests.Wav(samples: sourceRate, sample: sample);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), sourceRate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), sourceRate * sizeof(short));
        var expected = VoiceTests.Wav(samples: 16000, sample: sample)[44..];
        var address = new Uri("http://robot.local");

        using var handler = new CheckRequest(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(new Uri(address, "/audio/play"), request.RequestUri);
            Assert.Equal("25", Assert.Single(request.Headers.GetValues("X-Audio-Volume")));
            Assert.Equal("application/octet-stream", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal(expected.Length, request.Content.Headers.ContentLength);
            Assert.Equal(expected, await request.Content.ReadAsByteArrayAsync(token));
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });
        using var http = new HttpClient(handler);

        await new Esp32SpeakerClient(http).PlayWavAsync(address, wav, volume: 25);
    }

    [Theory]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task OneSynthesisDeliversHttpPcmAndPreservesOriginalMacWav(HttpStatusCode deviceStatus)
    {
        var tts = new CountingTts();
        var posts = 0;
        using var handler = new CheckRequest(async (request, token) =>
        {
            posts++;
            Assert.Equal("http://robot.local/audio/play", request.RequestUri!.AbsoluteUri);
            Assert.Equal("50", Assert.Single(request.Headers.GetValues("X-Audio-Volume")));
            Assert.Equal("application/octet-stream", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal(32000, request.Content.Headers.ContentLength);
            Assert.Equal(VoiceTests.Wav(samples: 16000, sample: -1234)[44..], await request.Content.ReadAsByteArrayAsync(token));
            return new HttpResponseMessage(deviceStatus);
        });
        using var deviceHttp = new HttpClient(handler);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Speech:Esp32:Enabled"] = "true",
                ["Speech:Esp32:BaseUrl"] = "http://robot.local",
                ["Speech:Esp32:Volume"] = "50"
            })).ConfigureTestServices(services =>
            {
                services.AddSingleton<ITextToSpeechService>(tts);
                services.AddSingleton(new Esp32SpeakerClient(deviceHttp));
            }));
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/voice/synthesize", new { text = "Ahoj." });
        response.EnsureSuccessStatusCode();
        Assert.Equal(tts.Wave, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("audio/wav", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(deviceStatus == HttpStatusCode.Accepted ? "accepted" : "error",
            Assert.Single(response.Headers.GetValues("X-Esp32-Audio")));
        Assert.Equal(1, tts.Calls);
        Assert.Equal(1, posts);
    }

    private sealed class CountingTts : ITextToSpeechService
    {
        public int Calls;
        public byte[] Wave { get; } = CreateWave();
        private static byte[] CreateWave()
        {
            var wav = VoiceTests.Wav(samples: 22050, sample: -1234);
            BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), 22050);
            BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), 44100);
            return wav;
        }
        public Task<byte[]> SynthesizeAsync(string text, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Wave);
        }
    }

    private sealed class CheckRequest(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            send(request, token);
    }
}
