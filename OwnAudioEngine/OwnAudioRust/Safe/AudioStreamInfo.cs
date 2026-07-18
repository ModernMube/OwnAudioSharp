using System;

namespace Ownaudio.Safe;

/// <summary>
/// What the decoder actually hands out, after channel and rate conversion.
/// Not necessarily what the source file holds.
/// </summary>
public readonly struct AudioStreamInfo
{
    public int Channels { get; }

    public int SampleRate { get; }

    // TimeSpan.Zero when unknown, check HasKnownDuration to tell the two apart
    public TimeSpan Duration { get; }

    /// <summary>
    /// Source bit depth, 0 for float or compressed formats.
    /// </summary>
    public int BitDepth { get; }

    public bool HasKnownDuration { get; }

    public AudioStreamInfo(int channels, int sampleRate, TimeSpan duration, int bitDepth, bool hasKnownDuration)
    {
        Channels = channels;
        SampleRate = sampleRate;
        Duration = duration;
        BitDepth = bitDepth;
        HasKnownDuration = hasKnownDuration;
    }
}
