using System;

namespace Ownaudio;

/// <summary>
/// The handful of numbers every codec gets asked about.
/// </summary>
public readonly struct AudioStreamInfo
{
    /// <summary>
    /// Bit depth defaults to 16 here.
    /// </summary>
    public AudioStreamInfo(int channels, int sampleRate, TimeSpan duration)
        : this(channels, sampleRate, duration, 16)
    {
    }

    /// <summary>
    /// bitDepth is the source's bits per sample.
    /// </summary>
    public AudioStreamInfo(int channels, int sampleRate, TimeSpan duration, long bitDepth)
    {
        Channels = channels;
        SampleRate = sampleRate;
        Duration = duration;
        BitDepth = bitDepth;
    }

    /// <summary>
    /// Channel count.
    /// </summary>
    public int Channels { get; }

    /// <summary>
    /// Sample rate in Hz.
    /// </summary>
    public int SampleRate { get; }

    /// <summary>
    /// How long the whole stream runs.
    /// </summary>
    public TimeSpan Duration { get; }

    /// <summary>
    /// Bits per sample of the source.
    /// </summary>
    public long BitDepth { get; }
}
