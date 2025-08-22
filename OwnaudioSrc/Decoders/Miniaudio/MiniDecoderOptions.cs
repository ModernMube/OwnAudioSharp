
namespace Ownaudio.Decoders.MiniAudio;

/// <summary>
/// Options for decoding (and, or) resampling specified audio source that can be passed
/// </summary>
public sealed class MiniaudioDecoderOptions
{
    /// <summary>
    /// Initializes <see cref="MiniaudioDecoderOptions"/> object.
    /// </summary>
    /// <param name="channels">Desired audio channel count.</param>
    /// <param name="sampleRate">Desired audio sample rate.</param>
    public MiniaudioDecoderOptions(int channels, int sampleRate)
    {
        Channels = channels;
        SampleRate = sampleRate;
    }

    /// <summary>
    /// Gets destination audio channel count.
    /// </summary>
    public int Channels { get; }

    /// <summary>
    /// Gets destination audio sample rate.
    /// </summary>
    public int SampleRate { get; }
}
