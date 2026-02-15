using System;
using System.Runtime.CompilerServices;

namespace OwnaudioNET.Effects.VST
{
    /// <summary>
    /// Efficient buffer format converter between interleaved and planar audio formats.
    /// Zero-allocation design for hot-path usage.
    /// </summary>
    internal static class VST3BufferConverter
    {
        /// <summary>
        /// Converts interleaved stereo buffer to planar format.
        /// Input:  L0, R0, L1, R1, L2, R2, ...
        /// Output: planar[0] = [L0, L1, L2, ...], planar[1] = [R0, R1, R2, ...]
        /// </summary>
        /// <param name="interleaved">Source interleaved audio buffer.</param>
        /// <param name="planar">Destination planar buffer array [channel][samples].</param>
        /// <param name="channels">Number of audio channels.</param>
        /// <param name="frameCount">Number of audio frames to convert.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void InterleavedToPlanar(
            ReadOnlySpan<float> interleaved,
            float[][] planar,
            int channels,
            int frameCount)
        {
            if (channels == 2)
            {
                float[] left = planar[0];
                float[] right = planar[1];

                for (int frame = 0; frame < frameCount; frame++)
                {
                    int interleavedIdx = frame * 2;
                    left[frame] = interleaved[interleavedIdx];
                    right[frame] = interleaved[interleavedIdx + 1];
                }
            }
            else if (channels == 1)
            {
                float[] mono = planar[0];
                for (int frame = 0; frame < frameCount; frame++)
                {
                    mono[frame] = interleaved[frame];
                }
            }
            else
            {
                for (int frame = 0; frame < frameCount; frame++)
                {
                    int interleavedBase = frame * channels;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        planar[ch][frame] = interleaved[interleavedBase + ch];
                    }
                }
            }
        }

        /// <summary>
        /// Converts planar format to interleaved stereo buffer.
        /// Input:  planar[0] = [L0, L1, L2, ...], planar[1] = [R0, R1, R2, ...]
        /// Output: L0, R0, L1, R1, L2, R2, ...
        /// </summary>
        /// <param name="planar">Source planar buffer array [channel][samples].</param>
        /// <param name="interleaved">Destination interleaved audio buffer.</param>
        /// <param name="channels">Number of audio channels.</param>
        /// <param name="frameCount">Number of audio frames to convert.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void PlanarToInterleaved(
            float[][] planar,
            Span<float> interleaved,
            int channels,
            int frameCount)
        {
            if (channels == 2)
            {
                float[] left = planar[0];
                float[] right = planar[1];

                for (int frame = 0; frame < frameCount; frame++)
                {
                    int interleavedIdx = frame * 2;
                    interleaved[interleavedIdx] = left[frame];
                    interleaved[interleavedIdx + 1] = right[frame];
                }
            }
            else if (channels == 1)
            {
                float[] mono = planar[0];
                for (int frame = 0; frame < frameCount; frame++)
                {
                    interleaved[frame] = mono[frame];
                }
            }
            else
            {
                for (int frame = 0; frame < frameCount; frame++)
                {
                    int interleavedBase = frame * channels;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        interleaved[interleavedBase + ch] = planar[ch][frame];
                    }
                }
            }
        }
    }
}
