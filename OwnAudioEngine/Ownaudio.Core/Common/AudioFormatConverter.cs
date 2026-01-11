using System;
using System.Runtime.CompilerServices;

namespace Ownaudio.Core.Common;

/// <summary>
/// High-performance audio format converter that combines resampling and channel conversion.
/// Zero-allocation design for real-time audio processing.
/// </summary>
/// <remarks>
/// This converter operates conditionally:
/// - Only resamples if source rate != target rate
/// - Only converts channels if source channels != target channels
/// - If no conversion needed, performs zero-copy passthrough
///
/// Processing order:
/// 1. Channel conversion (if needed)
/// 2. Resampling (if needed)
///
/// This order minimizes the amount of data to resample.
/// </remarks>
public sealed class AudioFormatConverter
{
    private readonly AudioChannelConverter? _channelConverter;
    private readonly AudioResampler? _resampler;
    private readonly bool _needsConversion;

    private readonly int _sourceChannels;
    private readonly int _targetChannels;
    private readonly int _sourceRate;
    private readonly int _targetRate;

    // Pre-allocated intermediate buffer (for channel conversion before resampling)
    private float[] _intermediateBuffer;

    /// <summary>
    /// Gets whether format conversion is needed.
    /// </summary>
    public bool IsConversionNeeded => _needsConversion;

    /// <summary>
    /// Gets the source sample rate.
    /// </summary>
    public int SourceRate => _sourceRate;

    /// <summary>
    /// Gets the target sample rate.
    /// </summary>
    public int TargetRate => _targetRate;

    /// <summary>
    /// Gets the source channel count.
    /// </summary>
    public int SourceChannels => _sourceChannels;

    /// <summary>
    /// Gets the target channel count.
    /// </summary>
    public int TargetChannels => _targetChannels;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioFormatConverter"/> class.
    /// </summary>
    /// <param name="sourceRate">Source sample rate in Hz.</param>
    /// <param name="sourceChannels">Number of source audio channels.</param>
    /// <param name="targetRate">Target sample rate in Hz.</param>
    /// <param name="targetChannels">Number of target audio channels.</param>
    /// <param name="maxFrameSize">Maximum frame size in samples (used for buffer pre-allocation).</param>
    public AudioFormatConverter(
        int sourceRate,
        int sourceChannels,
        int targetRate,
        int targetChannels,
        int maxFrameSize = 8192)
    {
        _sourceRate = sourceRate;
        _sourceChannels = sourceChannels;
        _targetRate = targetRate;
        _targetChannels = targetChannels;

        bool needsResampling = sourceRate != targetRate;
        bool needsChannelConversion = sourceChannels != targetChannels;

        _needsConversion = needsResampling || needsChannelConversion;

        if (!_needsConversion)
        {
            // No conversion needed - passthrough mode
            _channelConverter = null;
            _resampler = null;
            _intermediateBuffer = Array.Empty<float>();
            return;
        }

        // Initialize channel converter if needed
        if (needsChannelConversion)
        {
            _channelConverter = new AudioChannelConverter(sourceChannels, targetChannels);
        }

        // Initialize resampler if needed
        if (needsResampling)
        {
            // Resampler works on the target channel count (after channel conversion)
            int resamplerChannels = needsChannelConversion ? targetChannels : sourceChannels;
            _resampler = new AudioResampler(sourceRate, targetRate, resamplerChannels, maxFrameSize);
        }

        // Pre-allocate intermediate buffer (worst case: channel upmix + resampling upmix)
        int maxIntermediateSamples = maxFrameSize * Math.Max(sourceChannels, targetChannels) * 4;
        _intermediateBuffer = new float[maxIntermediateSamples];
    }

    /// <summary>
    /// Converts audio format from source to target.
    /// This method is zero-allocation and real-time safe (after initialization).
    /// </summary>
    /// <param name="input">Input samples (interleaved, Float32, source format).</param>
    /// <param name="output">Output span to write converted data (must be large enough).</param>
    /// <returns>Number of samples written to output.</returns>
    /// <remarks>
    /// ZERO-ALLOCATION: Uses pre-allocated buffers and Span-based API.
    /// Processing order: Channel conversion → Resampling
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Convert(Span<float> input, Span<float> output)
    {
        if (!_needsConversion)
        {
            // No conversion needed - zero-copy passthrough
            input.CopyTo(output);
            return input.Length;
        }

        Span<float> currentData = input;
        int currentSamples = input.Length;

        // Step 1: Channel conversion (if needed)
        if (_channelConverter != null)
        {
            Span<float> intermediateSpan = _intermediateBuffer.AsSpan();
            currentSamples = _channelConverter.Convert(currentData, intermediateSpan);
            currentData = intermediateSpan.Slice(0, currentSamples);
        }

        // Step 2: Resampling (if needed)
        if (_resampler != null)
        {
            currentSamples = _resampler.Resample(currentData, output);
        }
        else
        {
            // No resampling, copy to output
            currentData.CopyTo(output);
        }

        return currentSamples;
    }

    /// <summary>
    /// Resets the converter state (clears resampler position).
    /// Call this when seeking or starting a new stream.
    /// </summary>
    public void Reset()
    {
        _resampler?.Reset();
    }

    /// <summary>
    /// Calculates the required output buffer size for a given input size.
    /// </summary>
    /// <param name="inputSamples">Number of input samples (including all channels).</param>
    /// <returns>Required output buffer size in samples.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CalculateOutputSize(int inputSamples)
    {
        if (!_needsConversion)
            return inputSamples;

        int samples = inputSamples;

        // Calculate size after channel conversion
        if (_channelConverter != null)
        {
            samples = _channelConverter.CalculateOutputSize(samples);
        }

        // Calculate size after resampling
        if (_resampler != null)
        {
            samples = _resampler.CalculateOutputSize(samples);
        }

        return samples;
    }

    /// <summary>
    /// Gets a string description of the conversion being performed.
    /// </summary>
    public string GetConversionDescription()
    {
        if (!_needsConversion)
            return "No conversion (passthrough)";

        var parts = new System.Collections.Generic.List<string>();

        if (_channelConverter != null)
        {
            parts.Add($"{_sourceChannels}ch → {_targetChannels}ch");
        }

        if (_resampler != null)
        {
            parts.Add($"{_sourceRate}Hz → {_targetRate}Hz");
        }

        return string.Join(", ", parts);
    }
}
