using System;

namespace Ownaudio.Safe;

/// <summary>
/// Immutable metadata describing the decoded output of a <see cref="StreamingAudioDecoder"/>.
/// All values describe the decoder output after any requested channel and
/// sample-rate conversion, not necessarily the raw source file.
/// </summary>
public readonly struct AudioStreamInfo
{
    #region Properties

    /// <summary>Number of interleaved channels in the decoded output.</summary>
    public int Channels { get; }

    /// <summary>Output sample rate in Hz.</summary>
    public int SampleRate { get; }

    /// <summary>
    /// Total stream duration. Equals <see cref="TimeSpan.Zero"/> when the duration
    /// is unknown; check <see cref="HasKnownDuration"/> to distinguish.
    /// </summary>
    public TimeSpan Duration { get; }

    /// <summary>Source bit depth in bits, or 0 for float/compressed formats.</summary>
    public int BitDepth { get; }

    /// <summary>
    /// <see langword="true"/> when the total duration could be determined from
    /// the container metadata.
    /// </summary>
    public bool HasKnownDuration { get; }

    #endregion

    #region Construction

    /// <summary>
    /// Initializes a new <see cref="AudioStreamInfo"/>.
    /// </summary>
    /// <param name="channels">Number of interleaved channels.</param>
    /// <param name="sampleRate">Output sample rate in Hz.</param>
    /// <param name="duration">Total stream duration, or <see cref="TimeSpan.Zero"/> if unknown.</param>
    /// <param name="bitDepth">Source bit depth, or 0 for float/compressed formats.</param>
    /// <param name="hasKnownDuration">Whether <paramref name="duration"/> is meaningful.</param>
    public AudioStreamInfo(int channels, int sampleRate, TimeSpan duration, int bitDepth, bool hasKnownDuration)
    {
        Channels = channels;
        SampleRate = sampleRate;
        Duration = duration;
        BitDepth = bitDepth;
        HasKnownDuration = hasKnownDuration;
    }

    #endregion
}
