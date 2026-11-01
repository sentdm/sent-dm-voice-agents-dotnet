// The binary audio frame of an audio session: a 2-byte big-endian stream number, then 16-bit little-endian
// mono PCM at the session's sample rate, a whole number of samples. The header keeps the samples aligned, so a
// reader can view them as 16-bit integers without copying.
using System;

namespace SentDm.Voice.Agents;

/// <summary>The binary audio frame of an audio session.</summary>
public static class Audio
{
    public const int HeaderBytes = 2;

    /// <summary>The stream a frame belongs to: the session whose stream it is, on this connection.</summary>
    public static int StreamOf(ReadOnlySpan<byte> frame) => (frame[0] << 8) | frame[1];

    /// <summary>The samples of a frame, as 16-bit little-endian mono PCM.</summary>
    public static ReadOnlyMemory<byte> SamplesOf(ReadOnlyMemory<byte> frame) =>
        frame.Slice(HeaderBytes);

    /// <summary>A frame carrying <paramref name="samples"/> for <paramref name="stream"/>.</summary>
    public static byte[] Frame(int stream, ReadOnlySpan<byte> samples)
    {
        if (samples.Length % 2 != 0)
            throw new ArgumentException(
                "audio is a whole number of 16-bit samples",
                nameof(samples)
            );
        var frame = new byte[HeaderBytes + samples.Length];
        frame[0] = (byte)(stream >> 8);
        frame[1] = (byte)stream;
        samples.CopyTo(frame.AsSpan(HeaderBytes));
        return frame;
    }

    /// <summary>How many bytes of samples carry <paramref name="milliseconds"/> of audio at <paramref name="sampleRate"/>: 640 for 20 ms at 16000 Hz.</summary>
    public static int BytesPerFrame(int sampleRate, int milliseconds = 20) =>
        sampleRate * milliseconds / 1000 * 2;
}
