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
}
