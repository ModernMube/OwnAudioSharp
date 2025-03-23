using System;

namespace Ownaudio.Processors;

/// <summary>
/// An interface that is intended to manipulate specified audio sample in <c>Float32</c> format
/// before its gets send out to the output device.
/// </summary>
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
}
