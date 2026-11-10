using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

namespace HomeRobot.Server.Speech;

public sealed class Esp32SpeakerClient(HttpClient http)
{
    private const int SampleRate = 16000;
    private const int MaxSeconds = 30;

    public async Task PlayWavAsync(Uri deviceAddress, ReadOnlyMemory<byte> wav,
        int? volume = null, CancellationToken cancellationToken = default)
    {
        if (volume is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(volume));

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(deviceAddress, "/audio/play"))
        {
            Content = new ByteArrayContent(ToPcm(wav.Span))
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (volume is int percent)
            request.Headers.Add("X-Audio-Volume", percent.ToString(CultureInfo.InvariantCulture));

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Accepted)
            throw new HttpRequestException("The ESP32 did not accept the audio clip.", null, response.StatusCode);
    }

    private static byte[] ToPcm(ReadOnlySpan<byte> wav)
    {
        if (!WavAudio.TryReadPcm(wav, out var samples, out var rate, out var channels))
            throw new ArgumentException("Expected a PCM16 WAV of at most 30 seconds.", nameof(wav));
        if (rate == SampleRate && channels == 1)
            return samples.ToArray();

        var sourceFrames = samples.Length / (channels * sizeof(short));
        var outputFrames = Math.Clamp((int)((long)sourceFrames * SampleRate / rate), 1, MaxSeconds * SampleRate);
        var pcm = new byte[outputFrames * sizeof(short)];
        // Average the input over each output frame's time window, including fractional edges.
        for (var i = 0; i < outputFrames; i++)
        {
            var start = (long)i * rate;
            var end = Math.Min(start + rate, (long)sourceFrames * SampleRate);
            long sum = 0;
            for (var pos = start; pos < end;)
            {
                var frame = (int)(pos / SampleRate);
                var next = Math.Min(end, (long)(frame + 1) * SampleRate);
                var offset = frame * channels * sizeof(short);
                var sample = (int)BinaryPrimitives.ReadInt16LittleEndian(samples[offset..]);
                if (channels == 2)
                    sample += BinaryPrimitives.ReadInt16LittleEndian(samples[(offset + sizeof(short))..]);
                sum += sample * (next - pos);
                pos = next;
            }

            var value = (short)(sum / ((end - start) * channels));
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * sizeof(short)), value);
        }

        return pcm;
    }
}
