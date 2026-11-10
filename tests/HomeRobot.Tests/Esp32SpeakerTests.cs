using System.Buffers.Binary;
using System.Net;
using HomeRobot.Server.Speech;
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
            Assert.Equal(expected, await request.Content.ReadAsByteArrayAsync(token));
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });
        using var http = new HttpClient(handler);

        await new Esp32SpeakerClient(http).PlayWavAsync(address, wav, volume: 25);
    }

    private sealed class CheckRequest(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            send(request, token);
    }
}
