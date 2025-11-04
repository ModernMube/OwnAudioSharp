using System;
using System.Runtime.CompilerServices;

namespace Ownaudio.Core.Common;

/// <summary>
/// High-performance audio resampler using linear interpolation.
/// Zero-allocation design for real-time audio processing.
/// </summary>
/// <remarks>
/// Uses linear interpolation which provides good quality at high speed.
/// For better quality (but slower), consider implementing sinc interpolation in the future.
/// </remarks>
public sealed class AudioResampler
{
    private readonly int _sourceRate;
    private readonly int _targetRate;
    private readonly int _channels;
    private readonly double _ratio;
    private double _position;

    // Pre-allocated buffer for resampled output
    private float[] _outputBuffer;

    /// <summary>
    /// Gets whether resampling is needed (source rate != target rate).
    /// </summary>
    public bool IsResamplingNeeded => _sourceRate != _targetRate;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioResampler"/> class.
    /// </summary>
    /// <param name="sourceRate">Source sample rate in Hz.</param>
    /// <param name="targetRate">Target sample rate in Hz.</param>
    /// <param name="channels">Number of audio channels.</param>
    /// <param name="maxFrameSize">Maximum frame size in samples (used for buffer pre-allocation).</param>
    public AudioResampler(int sourceRate, int targetRate, int channels, int maxFrameSize = 8192)
    {
        if (sourceRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceRate), "Source rate must be positive.");

        if (targetRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetRate), "Target rate must be positive.");

        if (channels <= 0 || channels > 32)
            throw new ArgumentOutOfRangeException(nameof(channels), "Channels must be between 1 and 32.");

        _sourceRate = sourceRate;
        _targetRate = targetRate;
        _channels = channels;
        _ratio = (double)sourceRate / targetRate;
        _position = 0.0;

        // Pre-allocate output buffer (worst case: upsampling by 4x)
        int maxOutputSamples = maxFrameSize * channels * 4;
        _outputBuffer = new float[maxOutputSamples];
    }

    /// <summary>
    /// Resamples audio data from source rate to target rate using linear interpolation.
    /// This method is zero-allocation and real-time safe.
    /// </summary>
    /// <param name="input">Input samples (interleaved, Float32).</param>
    /// <param name="output">Output span to write resampled data (must be large enough).</param>
    /// <returns>Number of samples written to output.</returns>
    /// <remarks>
    /// ZERO-ALLOCATION: Uses pre-allocated buffers and Span-based API.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Resample(Span<float> input, Span<float> output)
    {
        if (!IsResamplingNeeded)
        {
            // No resampling needed - direct copy
            input.CopyTo(output);
            return input.Length;
        }

        int inputFrames = input.Length / _channels;
        int outputFrameCapacity = output.Length / _channels;
        int outputFrameCount = 0;

        // Linear interpolation resampling
        while (outputFrameCount < outputFrameCapacity)
        {
            int index0 = (int)_position;
            int index1 = index0 + 1;

            // Check bounds
            if (index1 >= inputFrames)
                break;

            double frac = _position - index0;

            // Interpolate each channel
            for (int ch = 0; ch < _channels; ch++)
            {
                int inputIdx0 = index0 * _channels + ch;
                int inputIdx1 = index1 * _channels + ch;
                int outputIdx = outputFrameCount * _channels + ch;

                float sample0 = input[inputIdx0];
                float sample1 = input[inputIdx1];

                // Linear interpolation: y = y0 + (y1 - y0) * frac
                output[outputIdx] = (float)(sample0 + (sample1 - sample0) * frac);
            }

            outputFrameCount++;
            _position += _ratio;
        }

        // Wrap position for next call (maintain sub-sample accuracy)
        _position -= inputFrames;
        if (_position < 0)
            _position = 0;

        return outputFrameCount * _channels;
    }

    /// <summary>
    /// Resets the resampler state (clears position).
    /// Call this when seeking or starting a new stream.
    /// </summary>
    public void Reset()
    {
        _position = 0.0;
    }

    /// <summary>
    /// Calculates the required output buffer size for a given input size.
    /// </summary>
    /// <param name="inputSamples">Number of input samples (including all channels).</param>
    /// <returns>Required output buffer size in samples.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CalculateOutputSize(int inputSamples)
    {
        if (!IsResamplingNeeded)
            return inputSamples;

        int inputFrames = inputSamples / _channels;
        int outputFrames = (int)Math.Ceiling(inputFrames / _ratio) + 1; // +1 for safety
        return outputFrames * _channels;
    }

    /// <summary>
    /// Gets a pre-allocated buffer for output (for convenience).
    /// </summary>
    /// <returns>Span view of the pre-allocated output buffer.</returns>
    public Span<float> GetOutputBuffer() => _outputBuffer.AsSpan();
}
