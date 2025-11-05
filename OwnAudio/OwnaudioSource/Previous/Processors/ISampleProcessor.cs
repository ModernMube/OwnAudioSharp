using System;

namespace OwnaudioLegacy.Processors;

/// <summary>
/// An interface that is intended to manipulate specified audio sample in <c>Float32</c> format
/// before its gets send out to the output device.
/// </summary>
[Obsolete("This is legacy code, available only for compatibility!")]
public interface ISampleProcessor
{
    /// <summary>
    /// Gets or sets whether or not the sample processor is currently enabled.
    /// </summary>
    bool IsEnabled { get; set; }

    /// <summary>
    /// Process or manipulate given audio sample in <c>float[]</c> format.
    /// </summary>
    /// <param name="samples">Audio sample to be processed.</param>
    /// <returns>Processed sample in <c>Float32</c> format.</returns>
    void Process(Span<float> samples);

    /// <summary>
    /// It clears temporary storage but does not change parameters.
    /// </summary>
    void Reset();
}
