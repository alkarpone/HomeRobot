using System.Buffers.Binary;

namespace HomeRobot.Server.Speech;

internal static class WavAudio
{
    public const int MaxBytes = 6 * 1024 * 1024;
    public const int MaxSeconds = 30;

    public static bool TryValidate(ReadOnlySpan<byte> wav, out bool silent)
    {
        var valid = TryReadPcm(wav, out var samples, out _, out _);
        silent = valid && samples.IndexOfAnyExcept((byte)0) < 0;
        return valid;
    }

    public static bool TryReadPcm(ReadOnlySpan<byte> wav, out ReadOnlySpan<byte> samples,
        out int sampleRate, out int channels)
    {
        samples = default;
        sampleRate = channels = 0;
        if (wav.Length < 44 || !wav[..4].SequenceEqual("RIFF"u8) ||
            Read32(wav[4..]) != wav.Length - 8 || !wav[8..12].SequenceEqual("WAVE"u8))
            return false;

        uint bytesPerSecond = 0;
        ushort alignment = 0;
        var remaining = wav[12..];
        while (!remaining.IsEmpty)
        {
            if (remaining.Length < 8)
                return false;

            var length = Read32(remaining[4..]);
            var paddedLength = length + (length & 1u);
            if (length > remaining.Length - 8 || paddedLength > remaining.Length - 8)
                return false;

            var chunk = remaining.Slice(8, (int)length);
            if (remaining[..4].SequenceEqual("fmt "u8))
            {
                if (bytesPerSecond != 0 || chunk.Length < 16 || Read16(chunk) != 1)
                    return false;

                channels = Read16(chunk[2..]);
                var rate = Read32(chunk[4..]);
                bytesPerSecond = Read32(chunk[8..]);
                alignment = Read16(chunk[12..]);
                if (channels is < 1 or > 2 || rate is < 8000 or > 48000 ||
                    Read16(chunk[14..]) != 16 || alignment != channels * 2 ||
                    bytesPerSecond != rate * alignment)
                    return false;
                sampleRate = (int)rate;
            }
            else if (remaining[..4].SequenceEqual("data"u8))
            {
                if (!samples.IsEmpty || bytesPerSecond == 0 || chunk.IsEmpty)
                    return false;

                samples = chunk;
            }

            remaining = remaining[(8 + (int)paddedLength)..];
        }

        return !samples.IsEmpty && samples.Length % alignment == 0 && samples.Length <= bytesPerSecond * MaxSeconds;
    }

    private static uint Read32(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    private static ushort Read16(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt16LittleEndian(bytes);
}
