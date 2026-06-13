using Ownaudio.Core;

namespace OwnaudioNET.Interfaces;

/// <summary>
/// Represents an audio effect processor that can modify audio samples in real-time.
/// </summary>
public interface IEffectProcessor : IDisposable
{
    /// <summary>
    /// Gets the unique identifier for this effect.
    /// </summary>
    Guid Id { get; }

    /// <summary>
    /// Gets the name of the effect.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets or sets whether the effect is enabled.
    /// </summary>
    bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the wet/dry mix (0.0 = fully dry, 1.0 = fully wet).
    /// </summary>
    float Mix { get; set; }

    /// <summary>
    /// Initializes the effect with the specified audio configuration.
    /// </summary>
    /// <param name="config">The audio configuration to process.</param>
    void Initialize(AudioConfig config);

    /// <summary>
    /// Processes audio samples in-place.
    /// </summary>
    /// <param name="buffer">The buffer containing audio samples to process.</param>
    /// <param name="frameCount">The number of frames in the buffer.</param>
    void Process(Span<float> buffer, int frameCount);

    /// <summary>
    /// Resets the effect's internal state.
    /// </summary>
    void Reset();

    /// <summary>
    /// Gets whether this effect is ready to process audio.
    /// Always true for built-in effects; VST3 effects return false until
    /// the plugin has been audio-initialized via VST3PluginHost.InitializeAudioAsync().
    /// </summary>
    bool IsReady => true;

    /// <summary>
    /// Gets the processing latency introduced by this effect in samples.
    /// </summary>
    /// <remarks>
    /// Zero-latency effects such as equalizers, compressors, and reverbs return 0.
    /// Lookahead-based effects such as <see cref="AutoGainEffect"/> and
    /// <see cref="LimiterEffect"/> return their actual lookahead buffer size.
    /// VST3 plugins query the value from the native plugin after audio initialization.
    /// This value is used by <see cref="AudioMixer.ApplyPluginDelayCompensation"/>
    /// to align all tracks sample-accurately in the mixed output.
    /// </remarks>
    int LatencySamples => 0;
}
