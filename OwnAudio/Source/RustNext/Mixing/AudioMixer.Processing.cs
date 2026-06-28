using System.Numerics;
using System.Runtime.CompilerServices;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.RustNext.Mixing;

public sealed partial class AudioMixer
{
    /// <summary>
    /// Adds interleaved float samples from <paramref name="sourceBuffer"/> into
    /// <paramref name="mixBuffer"/> using additive mixing (summation).
    /// When hardware SIMD acceleration is available, samples are processed in
    /// vector-width batches (typically 4 or 8 floats at a time), providing a
    /// four-to-eight-times throughput improvement on modern processors.
    /// </summary>
    /// <param name="mixBuffer">Destination accumulation buffer; modified in place.</param>
    /// <param name="sourceBuffer">Source samples to add into <paramref name="mixBuffer"/>.</param>
    /// <param name="sampleCount">Number of interleaved samples to mix.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MixIntoBuffer(float[] mixBuffer, float[] sourceBuffer, int sampleCount)
    {
        int i = 0;
        int simdLength = Vector<float>.Count;

        if (Vector.IsHardwareAccelerated && sampleCount >= simdLength)
        {
            int simdLoopEnd = sampleCount - (sampleCount % simdLength);

            for (; i < simdLoopEnd; i += simdLength)
            {
                var mixVec = new Vector<float>(mixBuffer, i);
                var srcVec = new Vector<float>(sourceBuffer, i);

                var result = mixVec + srcVec;
                result.CopyTo(mixBuffer, i);
            }
        }

        for (; i < sampleCount; i++)
        {
            mixBuffer[i] += sourceBuffer[i];
        }
    }

    /// <summary>
    /// Adds samples from <paramref name="sourceBuffer"/> into specific output channels of
    /// <paramref name="mixBuffer"/> as defined by <paramref name="channelMapping"/>.
    /// This allows a mono or stereo source to be routed to arbitrary output channels
    /// in a multi-channel mix bus without allocating intermediate buffers.
    /// If any mapped channel index is out of range the entire mix operation is aborted
    /// to prevent buffer overruns or corruption.
    /// </summary>
    /// <param name="mixBuffer">Destination multi-channel mix buffer; modified in place.</param>
    /// <param name="sourceBuffer">Source audio samples in interleaved format.</param>
    /// <param name="sampleCount">Total number of source samples to mix.</param>
    /// <param name="channelMapping">
    /// Array of zero-based output channel indices; must have one entry per source channel.
    /// </param>
    /// <param name="totalOutputChannels">Total number of channels in <paramref name="mixBuffer"/>.</param>
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

        foreach (int ch in channelMapping)
        {
            if (ch < 0 || ch >= totalOutputChannels)
                return;
        }

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
    /// Multiplies every sample in <paramref name="buffer"/> by the current master volume scalar.
    /// When the volume is within 0.001 of 1.0 the method returns early, avoiding unnecessary work.
    /// When hardware SIMD acceleration is available, samples are processed in vector-width batches
    /// for a four-to-eight-times throughput improvement on modern processors.
    /// </summary>
    /// <param name="buffer">Interleaved float audio buffer to scale in place.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ApplyMasterVolume(Span<float> buffer)
    {
        float volume = _masterVolume;

        if (Math.Abs(volume - 1.0f) < 0.001f)
            return;

        int i = 0;
        int simdLength = Vector<float>.Count;

        if (Vector.IsHardwareAccelerated && buffer.Length >= simdLength)
        {
            var volumeVec = new Vector<float>(volume);
            int simdLoopEnd = buffer.Length - (buffer.Length % simdLength);

            for (; i < simdLoopEnd; i += simdLength)
            {
                var vec = new Vector<float>(buffer.Slice(i, simdLength));
                vec *= volumeVec;

                vec.CopyTo(buffer.Slice(i, simdLength));
            }
        }

        for (; i < buffer.Length; i++)
        {
            buffer[i] *= volume;
        }
    }

    /// <summary>
    /// Applies each registered master effect processor to <paramref name="buffer"/> in insertion order.
    /// Uses a lock-free read of <c>_cachedEffects</c> via <see cref="Volatile.Read"/> so the real-time
    /// audio thread never acquires a lock; the main thread publishes updates atomically through
    /// <c>PublishEffectsCache()</c>. Effects that are not enabled are skipped without calling
    /// <see cref="IEffectProcessor.Process"/>.
    /// </summary>
    /// <param name="buffer">Interleaved float audio buffer to process in place.</param>
    /// <param name="frameCount">Number of audio frames contained in <paramref name="buffer"/>.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ApplyMasterEffects(Span<float> buffer, int frameCount)
    {
        var effects = Volatile.Read(ref _cachedEffects);
        if (effects.Length == 0)
            return;

        foreach (var effect in effects)
        {
            try
            {
                if (effect.Enabled)
                {
                    effect.Process(buffer, frameCount);
                }
            }
            catch {}
        }
    }

    /// <summary>
    /// Scans <paramref name="buffer"/> to find the absolute peak sample value for the left
    /// and right channels and stores the results in <c>_leftPeak</c> and <c>_rightPeak</c>.
    /// Assumes strictly interleaved stereo layout (left sample at even indices, right at odd).
    /// Uses stack-allocated temporary buffers and SIMD vector operations when hardware
    /// acceleration is available to minimise latency on the real-time audio thread.
    /// </summary>
    /// <param name="buffer">Interleaved stereo float audio buffer to analyse.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CalculatePeakLevels(Span<float> buffer)
    {
        float leftPeak = 0.0f;
        float rightPeak = 0.0f;

        int frameCount = buffer.Length / 2;
        int simdLength = Vector<float>.Count;

        if (Vector.IsHardwareAccelerated && frameCount >= simdLength / 2)
        {
            var leftPeakVec = Vector<float>.Zero;
            var rightPeakVec = Vector<float>.Zero;

            int simdFrames = (frameCount / simdLength) * simdLength;
            int i = 0;

            Span<float> leftSamples = stackalloc float[simdLength];
            Span<float> rightSamples = stackalloc float[simdLength];

            for (; i < simdFrames * 2; i += simdLength * 2)
            {
                for (int j = 0; j < simdLength && i + j * 2 < buffer.Length; j++)
                {
                    leftSamples[j] = Math.Abs(buffer[i + j * 2]);
                    rightSamples[j] = Math.Abs(buffer[i + j * 2 + 1]);
                }

                var leftVec = new Vector<float>(leftSamples);
                var rightVec = new Vector<float>(rightSamples);

                leftPeakVec = Vector.Max(leftPeakVec, leftVec);
                rightPeakVec = Vector.Max(rightPeakVec, rightVec);
            }

            for (int j = 0; j < simdLength; j++)
            {
                if (leftPeakVec[j] > leftPeak)
                    leftPeak = leftPeakVec[j];
                if (rightPeakVec[j] > rightPeak)
                    rightPeak = rightPeakVec[j];
            }

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
    /// Applies a hard brickwall limiter to <paramref name="buffer"/>, clamping every sample
    /// to the range [−1.0, +1.0] to prevent digital clipping when multiple summed sources
    /// cause the mix to exceed full scale.
    /// Called once per mix cycle after all effects and volume processing have been applied.
    /// Uses SIMD vector min/max operations when hardware acceleration is available.
    /// </summary>
    /// <param name="buffer">Interleaved float audio buffer to limit in place.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyLimiter(Span<float> buffer)
    {
        int i = 0;
        int simdLength = Vector<float>.Count;

        if (Vector.IsHardwareAccelerated && buffer.Length >= simdLength)
        {
            var maxVec = new Vector<float>(1.0f);
            var minVec = new Vector<float>(-1.0f);
            int simdLoopEnd = buffer.Length - (buffer.Length % simdLength);

            for (; i < simdLoopEnd; i += simdLength)
            {
                var vec = new Vector<float>(buffer.Slice(i, simdLength));
                vec = Vector.Min(vec, maxVec);
                vec = Vector.Max(vec, minVec);
                vec.CopyTo(buffer.Slice(i, simdLength));
            }
        }

        for (; i < buffer.Length; i++)
        {
            if (buffer[i] > 1.0f) buffer[i] = 1.0f;
            else if (buffer[i] < -1.0f) buffer[i] = -1.0f;
        }
    }
}
