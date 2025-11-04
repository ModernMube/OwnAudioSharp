using System;

namespace Ownaudio;

/// <summary>
/// Contains audio stream information commonly requested by audio codecs.
/// This class cannot be inherited.
/// </summary>
public readonly struct AudioStreamInfo
{
    /// <summary>
    /// Initializes <see cref="AudioStreamInfo"/> structure.
    /// </summary>
    /// <param name="channels">Number of audio channels.</param>
    /// <param name="sampleRate">Audio sample rate.</param>
    /// <param name="duration">Audio stream duration.</param>
    public AudioStreamInfo(int channels, int sampleRate, TimeSpan duration)
    {
        Channels = channels;
        SampleRate = sampleRate;
        Duration = duration;
        BitDepth = 16;
    }

    /// <summary>
    /// Initializes <see cref="AudioStreamInfo"/> structure.
    /// </summary>
    /// <param name="channels">Number of audio channels.</param>
    /// <param name="sampleRate">Audio sample rate.</param>
    /// <param name="duration">Audio stream duration.</param>
    /// <param name="bitDepth">Audio stream bit depth</param>
    public AudioStreamInfo(int channels, int sampleRate, TimeSpan duration, long bitDepth)
    {
        Channels = channels;
        SampleRate = sampleRate;
        Duration = duration;
        BitDepth = bitDepth;
    }

    /// <summary>
    /// Gets number of audio channels.
    /// </summary>
    public int Channels { get; }

    /// <summary>
    /// Gets audio sample rate.
    /// </summary>
    public int SampleRate { get; }

    /// <summary>
    /// Gets audio stream duration.
    /// </summary>
    public TimeSpan Duration { get; }

    /// <summary>
    /// Gets audio stream Bit depth
    /// </summary>
    public long BitDepth { get; }
}
