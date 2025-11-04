using System;
using System.Runtime.CompilerServices;

namespace Ownaudio.Core.Common;

/// <summary>
/// High-performance audio channel converter.
/// Zero-allocation design for real-time audio processing.
/// </summary>
/// <remarks>
/// Supports common conversions:
/// - Mono to Stereo (duplicate channel)
/// - Stereo to Mono (average channels)
/// - Multi-channel to Stereo (downmix)
/// - Multi-channel to Mono (average all channels)
/// </remarks>
public sealed class AudioChannelConverter
{
    private readonly int _sourceChannels;
    private readonly int _targetChannels;

    /// <summary>
    /// Gets whether channel conversion is needed (source channels != target channels).
    /// </summary>
    public bool IsConversionNeeded => _sourceChannels != _targetChannels;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioChannelConverter"/> class.
    /// </summary>
    /// <param name="sourceChannels">Number of source audio channels.</param>
    /// <param name="targetChannels">Number of target audio channels.</param>
    public AudioChannelConverter(int sourceChannels, int targetChannels)
    {
        if (sourceChannels <= 0 || sourceChannels > 32)
            throw new ArgumentOutOfRangeException(nameof(sourceChannels), "Source channels must be between 1 and 32.");

        if (targetChannels <= 0 || targetChannels > 32)
            throw new ArgumentOutOfRangeException(nameof(targetChannels), "Target channels must be between 1 and 32.");

        _sourceChannels = sourceChannels;
        _targetChannels = targetChannels;
    }

    /// <summary>
    /// Converts audio channel count from source to target.
    /// This method is zero-allocation and real-time safe.
    /// </summary>
    /// <param name="input">Input samples (interleaved, Float32).</param>
    /// <param name="output">Output span to write converted data (must be large enough).</param>
    /// <returns>Number of samples written to output.</returns>
    /// <remarks>
    /// ZERO-ALLOCATION: Uses Span-based API with no heap allocations.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Convert(Span<float> input, Span<float> output)
    {
        if (!IsConversionNeeded)
        {
            // No conversion needed - direct copy
            input.CopyTo(output);
            return input.Length;
        }

        int inputFrames = input.Length / _sourceChannels;
        int outputSamples = inputFrames * _targetChannels;

        if (output.Length < outputSamples)
            throw new ArgumentException($"Output buffer too small. Required: {outputSamples}, Available: {output.Length}");

        // Route to specific conversion method based on channel configuration
        if (_sourceChannels == 1 && _targetChannels == 2)
        {
            // Mono to Stereo (duplicate)
            ConvertMonoToStereo(input, output, inputFrames);
        }
        else if (_sourceChannels == 2 && _targetChannels == 1)
        {
            // Stereo to Mono (average)
            ConvertStereoToMono(input, output, inputFrames);
        }
        else if (_sourceChannels == 1 && _targetChannels > 2)
        {
            // Mono to Multi-channel (duplicate to all channels)
            ConvertMonoToMulti(input, output, inputFrames);
        }
        else if (_sourceChannels > 1 && _targetChannels == 1)
        {
            // Multi-channel to Mono (average all)
            ConvertMultiToMono(input, output, inputFrames);
        }
        else if (_sourceChannels > 2 && _targetChannels == 2)
        {
            // Multi-channel to Stereo (use first 2 channels)
            ConvertMultiToStereo(input, output, inputFrames);
        }
        else
        {
            // Generic conversion (not optimized)
            ConvertGeneric(input, output, inputFrames);
        }

        return outputSamples;
    }

    /// <summary>
    /// Converts mono to stereo by duplicating the channel.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ConvertMonoToStereo(Span<float> input, Span<float> output, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            float sample = input[i];
            output[i * 2] = sample;     // Left
            output[i * 2 + 1] = sample; // Right
        }
    }

    /// <summary>
    /// Converts stereo to mono by averaging the channels.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ConvertStereoToMono(Span<float> input, Span<float> output, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            float left = input[i * 2];
            float right = input[i * 2 + 1];
            output[i] = (left + right) * 0.5f;
        }
    }

    /// <summary>
    /// Converts mono to multi-channel by duplicating to all channels.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ConvertMonoToMulti(Span<float> input, Span<float> output, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            float sample = input[i];
            for (int ch = 0; ch < _targetChannels; ch++)
            {
                output[i * _targetChannels + ch] = sample;
            }
        }
    }

    /// <summary>
    /// Converts multi-channel to mono by averaging all channels.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ConvertMultiToMono(Span<float> input, Span<float> output, int frames)
    {
        float scale = 1.0f / _sourceChannels;

        for (int i = 0; i < frames; i++)
        {
            float sum = 0.0f;
            for (int ch = 0; ch < _sourceChannels; ch++)
            {
                sum += input[i * _sourceChannels + ch];
            }
            output[i] = sum * scale;
        }
    }

    /// <summary>
    /// Converts multi-channel to stereo using first 2 channels.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ConvertMultiToStereo(Span<float> input, Span<float> output, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            output[i * 2] = input[i * _sourceChannels];         // Left
            output[i * 2 + 1] = input[i * _sourceChannels + 1]; // Right
        }
    }

    /// <summary>
    /// Generic channel conversion (fallback, not optimized).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ConvertGeneric(Span<float> input, Span<float> output, int frames)
    {
        if (_sourceChannels < _targetChannels)
        {
            // Upmix: duplicate available channels
            for (int i = 0; i < frames; i++)
            {
                for (int ch = 0; ch < _targetChannels; ch++)
                {
                    int sourceChannel = ch % _sourceChannels;
                    output[i * _targetChannels + ch] = input[i * _sourceChannels + sourceChannel];
                }
            }
        }
        else
        {
            // Downmix: average extra channels
            for (int i = 0; i < frames; i++)
            {
                for (int ch = 0; ch < _targetChannels; ch++)
                {
                    float sum = 0.0f;
                    int count = 0;

                    // Average all source channels that map to this target channel
                    for (int sch = ch; sch < _sourceChannels; sch += _targetChannels)
                    {
                        sum += input[i * _sourceChannels + sch];
                        count++;
                    }

                    output[i * _targetChannels + ch] = sum / count;
                }
            }
        }
    }

    /// <summary>
    /// Calculates the required output buffer size for a given input size.
    /// </summary>
    /// <param name="inputSamples">Number of input samples (including all channels).</param>
    /// <returns>Required output buffer size in samples.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CalculateOutputSize(int inputSamples)
    {
        if (!IsConversionNeeded)
            return inputSamples;

        int inputFrames = inputSamples / _sourceChannels;
        return inputFrames * _targetChannels;
    }
}
