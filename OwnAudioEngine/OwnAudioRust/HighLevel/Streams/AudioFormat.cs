namespace Ownaudio.Audio.Streams;

/// <summary>
/// Immutable description of an audio stream format.
/// </summary>
/// <param name="SampleRate">Sample rate in Hz (e.g. 44 100 or 48 000).</param>
/// <param name="Channels">Number of interleaved channels (e.g. 1 = mono, 2 = stereo).</param>
/// <param name="SampleType">
/// Sample data format; <see cref="SampleFormat.Float32"/> is recommended for DSP work.
/// </param>
public readonly record struct AudioFormat(
    int SampleRate,
    int Channels,
    SampleFormat SampleType = SampleFormat.Float32)
{
    #region Derived properties

    /// <summary>
    /// Number of bytes per sample for the given <see cref="SampleType"/>.
    /// </summary>
    public int BytesPerSample => SampleType switch
    {
        SampleFormat.Float32 => 4,
        SampleFormat.Int16   => 2,
        SampleFormat.UInt16  => 2,
        _                    => 4,
    };

    /// <summary>
    /// Computes the total number of samples in the given duration.
    /// </summary>
    /// <param name="duration">The time span to convert.</param>
    /// <returns>Total samples across all channels.</returns>
    public long SamplesForDuration(System.TimeSpan duration)
    {
        return (long)(duration.TotalSeconds * SampleRate * Channels);
    }

    /// <summary>
    /// Computes the duration represented by the given sample count.
    /// </summary>
    /// <param name="totalSamples">Total interleaved sample count.</param>
    /// <returns>The corresponding <see cref="System.TimeSpan"/>.</returns>
    public System.TimeSpan DurationForSamples(long totalSamples)
    {
        if (SampleRate <= 0 || Channels <= 0)
        {
            return System.TimeSpan.Zero;
        }

        double seconds = (double)totalSamples / Channels / SampleRate;
        return System.TimeSpan.FromSeconds(seconds);
    }

    #endregion
}
