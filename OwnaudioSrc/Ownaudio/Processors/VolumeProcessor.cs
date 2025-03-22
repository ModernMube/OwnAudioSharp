using System;

namespace Ownaudio.Processors;

/// <summary>
/// A sample processor that simply multiply given audio sample to a desired volume.
/// This class cannot be inherited.
/// <para>Implements: <see cref="SampleProcessorBase"/>.</para>
/// </summary>
public sealed class VolumeProcessor : SampleProcessorBase
{
    /// <summary>
    /// Initializes <see cref="VolumeProcessor"/>. The volume range should between 0f to 1f.
    /// </summary>
    /// <param name="initialVolume">Inital desired audio volume.</param>
    public VolumeProcessor(float initialVolume = 1.0f)
    {
        Volume = initialVolume;
    }

    /// <summary>
    /// Gets or sets desired volume.
    /// </summary>
    public float Volume { get; set; }

    /// <summary>
    /// Adjusts the samples to the correct volume.
    /// </summary>
    /// <param name="samples"></param>
    public override void Process(Span<float> samples)
    {
        for (int i = 0; i < samples.Length; i++)
            samples[i] = samples[i] * Volume;
    }
}
