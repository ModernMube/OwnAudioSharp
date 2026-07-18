using Ownaudio.Core;

namespace OwnaudioNET.Interfaces;

/// <summary>
/// A real-time effect that chews on audio samples in place.
/// </summary>
public interface IEffectProcessor : IDisposable
{
    /// <summary>
    /// Unique id for this effect.
    /// </summary>
    Guid Id { get; }

    /// <summary>
    /// Effect name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// On/off.
    /// </summary>
    bool Enabled { get; set; }

    /// <summary>
    /// Wet/dry mix, 0 = dry .. 1 = wet.
    /// </summary>
    float Mix { get; set; }

    /// <summary>
    /// Spin up with the given audio config.
    /// </summary>
    /// <param name="config"></param>
    void Initialize(AudioConfig config);

    /// <summary>
    /// Process frameCount frames in the buffer, in-place.
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="frameCount"></param>
    void Process(Span<float> buffer, int frameCount);

    /// <summary>
    /// Drop internal state.
    /// </summary>
    void Reset();

    /// <summary>
    /// Ready to process? Built-ins are always ready; VST3 stays false until
    /// the native plugin is audio-initialized.
    /// </summary>
    bool IsReady => true;

    /// <summary>
    /// Latency this effect adds, in samples. Zero-latency stuff (EQ, comp,
    /// reverb) is 0; lookahead effects report their lookahead. Used by the
    /// mixer's plugin delay compensation to keep tracks aligned.
    /// </summary>
    int LatencySamples => 0;
}
