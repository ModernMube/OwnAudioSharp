using System.Numerics;
using System.Runtime.CompilerServices;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Mixing;

public sealed partial class AudioMixer
{
    /// <summary>
    /// Mixes source samples into the mix buffer (additive mixing).
    /// Zero-allocation hot path method with SIMD vectorization.
    /// Performance: 4-8x faster on modern CPUs with hardware acceleration.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MixIntoBuffer(float[] mixBuffer, float[] sourceBuffer, int sampleCount)
    {
        int i = 0;
        int simdLength = Vector<float>.Count;

        // SIMD vectorized processing (processes 4-8 floats at once depending on CPU)
        if (Vector.IsHardwareAccelerated && sampleCount >= simdLength)
        {
            // Process in SIMD chunks for optimal performance
            int simdLoopEnd = sampleCount - (sampleCount % simdLength);

            for (; i < simdLoopEnd; i += simdLength)
            {
                // Load vectors from both buffers
                var mixVec = new Vector<float>(mixBuffer, i);
                var srcVec = new Vector<float>(sourceBuffer, i);

                // Add vectors (SIMD operation - single CPU instruction)
                var result = mixVec + srcVec;

                // Store result back to mix buffer
                result.CopyTo(mixBuffer, i);
            }
        }

        // Scalar fallback for remaining samples
        for (; i < sampleCount; i++)
        {
            mixBuffer[i] += sourceBuffer[i];
        }
    }

    /// <summary>
    /// Mixes source samples into specific output channels based on channel mapping.
    /// Zero-allocation hot path method for selective channel routing.
    /// </summary>
    /// <param name="mixBuffer">The output mix buffer</param>
    /// <param name="sourceBuffer">The source audio buffer</param>
    /// <param name="sampleCount">Number of samples to mix</param>
    /// <param name="channelMapping">Target output channel indices (must match source channel count)</param>
    /// <param name="totalOutputChannels">Total number of output channels in mix buffer</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MixIntoBufferSelective(
        float[] mixBuffer,
        float[] sourceBuffer,
        int sampleCount,
        int[] channelMapping,
        int totalOutputChannels)
    {
        int sourceChannels = channelMapping.Length;
        int frameCount = sampleCount / sourceChannels;

        // Validate channel mapping
        foreach (int ch in channelMapping)
        {
            if (ch < 0 || ch >= totalOutputChannels)
                return; // Invalid channel index - skip mixing to prevent crashes
        }

        // Mix frame by frame with channel mapping
        for (int frame = 0; frame < frameCount; frame++)
        {
            for (int ch = 0; ch < sourceChannels; ch++)
            {
                int sourceIndex = frame * sourceChannels + ch;
                int outputIndex = frame * totalOutputChannels + channelMapping[ch];

                mixBuffer[outputIndex] += sourceBuffer[sourceIndex];
            }
        }
    }

    /// <summary>
    /// Applies master volume to the mixed buffer.
    /// Zero-allocation hot path method with SIMD vectorization.
    /// Performance: 4-8x faster on modern CPUs with hardware acceleration.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ApplyMasterVolume(Span<float> buffer)
    {
        float volume = _masterVolume;

        if (Math.Abs(volume - 1.0f) < 0.001f)
            return; // Skip if volume is ~1.0

        int i = 0;
        int simdLength = Vector<float>.Count;

        // SIMD vectorized processing
        if (Vector.IsHardwareAccelerated && buffer.Length >= simdLength)
        {
            var volumeVec = new Vector<float>(volume);
            int simdLoopEnd = buffer.Length - (buffer.Length % simdLength);

            for (; i < simdLoopEnd; i += simdLength)
            {
                // Load vector from buffer
                var vec = new Vector<float>(buffer.Slice(i, simdLength));

                // Multiply by volume vector (SIMD operation - single CPU instruction)
                vec *= volumeVec;

                // Store result back to buffer
                vec.CopyTo(buffer.Slice(i, simdLength));
            }
        }

        // Scalar fallback for remaining samples
        for (; i < buffer.Length; i++)
        {
            buffer[i] *= volume;
        }
    }

    /// <summary>
    /// Applies master effects to the mixed buffer.
    /// Effects are processed in the order they were added.
    /// Zero-allocation hot path method using cached effect array.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ApplyMasterEffects(Span<float> buffer, int frameCount)
    {
        if (_effectsChanged)
        {
            lock (_effectsLock)
            {
                if (_effectsChanged) // Double-check inside lock
                {
                    _cachedEffects = _masterEffects.ToArray();
                    _effectsChanged = false;
                }
            }
        }

        // Use cached array (zero allocation in steady state)
        var effects = _cachedEffects;
        if (effects.Length == 0)
            return; // No effects to apply

        // Process each effect in sequence
        foreach (var effect in effects)
        {
            try
            {
                if (effect.Enabled)
                {
                    effect.Process(buffer, frameCount);
                }
            }
            catch
            {
                // Effect processing error - skip this effect and continue
                // In production, log via ILogger
            }
        }
    }

    /// <summary>
    /// Calculates peak levels for stereo output.
    /// Updates LeftPeak and RightPeak fields.
    /// Uses SIMD vectorization for optimal performance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CalculatePeakLevels(Span<float> buffer)
    {
        float leftPeak = 0.0f;
        float rightPeak = 0.0f;

        int frameCount = buffer.Length / 2; // Stereo: 2 samples per frame
        int simdLength = Vector<float>.Count;

        // SIMD vectorized processing (processes multiple samples at once)
        if (Vector.IsHardwareAccelerated && frameCount >= simdLength / 2)
        {
            var leftPeakVec = Vector<float>.Zero;
            var rightPeakVec = Vector<float>.Zero;

            int simdFrames = (frameCount / simdLength) * simdLength;
            int i = 0;

            // Pre-allocate buffers outside the loop to avoid potential stack overflow (CA2014)
            Span<float> leftSamples = stackalloc float[simdLength];
            Span<float> rightSamples = stackalloc float[simdLength];

            for (; i < simdFrames * 2; i += simdLength * 2)
            {
                // Load left channel samples (every other pair)
                for (int j = 0; j < simdLength && i + j * 2 < buffer.Length; j++)
                {
                    leftSamples[j] = Math.Abs(buffer[i + j * 2]);
                    rightSamples[j] = Math.Abs(buffer[i + j * 2 + 1]);
                }

                var leftVec = new Vector<float>(leftSamples);
                var rightVec = new Vector<float>(rightSamples);

                // Track maximum values using Vector.Max
                leftPeakVec = Vector.Max(leftPeakVec, leftVec);
                rightPeakVec = Vector.Max(rightPeakVec, rightVec);
            }

            // Extract maximum from vectors
            for (int j = 0; j < simdLength; j++)
            {
                if (leftPeakVec[j] > leftPeak)
                    leftPeak = leftPeakVec[j];
                if (rightPeakVec[j] > rightPeak)
                    rightPeak = rightPeakVec[j];
            }

            // Process remaining samples with scalar code
            for (; i < buffer.Length; i += 2)
            {
                float leftSample = Math.Abs(buffer[i]);
                float rightSample = Math.Abs(buffer[i + 1]);

                if (leftSample > leftPeak)
                    leftPeak = leftSample;
                if (rightSample > rightPeak)
                    rightPeak = rightSample;
            }
        }
        else
        {
            // Scalar fallback (original implementation)
            for (int i = 0; i < buffer.Length; i += 2)
            {
                float leftSample = Math.Abs(buffer[i]);
                float rightSample = Math.Abs(buffer[i + 1]);

                if (leftSample > leftPeak)
                    leftPeak = leftSample;

                if (rightSample > rightPeak)
                    rightPeak = rightSample;
            }
        }

        _leftPeak = leftPeak;
        _rightPeak = rightPeak;
    }

    /// <summary>
    /// Writes mixed audio to the recorder.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteToRecorder(Span<float> buffer)
    {
        lock (_recorderLock)
        {
            if (_isRecording && _recorder != null)
            {
                try
                {
                    _recorder.WriteSamples(buffer);
                }
                catch
                {
                    // Recording error - stop recording
                    _isRecording = false;
                }
            }
        }
    }
}
