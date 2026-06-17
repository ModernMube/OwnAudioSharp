using System;

namespace Ownaudio.Audio.Diagnostics;

/// <summary>
/// Provides VU-meter level data for the <see cref="Capture.AudioRecorder.LevelChanged"/> event.
/// </summary>
/// <remarks>
/// This event is throttled to approximately 30 Hz so it is safe to use directly
/// for UI updates without additional rate limiting.
/// </remarks>
public sealed class AudioLevelEventArgs : EventArgs
{
    #region Properties

    /// <summary>
    /// Root-mean-square level in the range [0, 1].
    /// Represents the perceived loudness of the captured audio.
    /// </summary>
    public float Rms { get; }

    /// <summary>
    /// Peak absolute sample value in the range [0, 1].
    /// Useful for clipping detection.
    /// </summary>
    public float Peak { get; }

    /// <summary>
    /// RMS level in decibels full-scale (dBFS), where 0 dBFS is the maximum.
    /// Returns <see cref="float.NegativeInfinity"/> when <see cref="Rms"/> is 0.
    /// </summary>
    public float RmsDb => Rms > 0f ? 20f * MathF.Log10(Rms) : float.NegativeInfinity;

    /// <summary>
    /// Peak level in dBFS.
    /// Returns <see cref="float.NegativeInfinity"/> when <see cref="Peak"/> is 0.
    /// </summary>
    public float PeakDb => Peak > 0f ? 20f * MathF.Log10(Peak) : float.NegativeInfinity;

    #endregion

    #region Construction

    /// <summary>
    /// Initializes a new <see cref="AudioLevelEventArgs"/>.
    /// </summary>
    /// <param name="rms">RMS level in [0, 1].</param>
    /// <param name="peak">Peak level in [0, 1].</param>
    public AudioLevelEventArgs(float rms, float peak)
    {
        Rms  = rms;
        Peak = peak;
    }

    #endregion
}
