using System;
using System.Runtime.CompilerServices;

namespace OwnaudioNET.Effects.VST
{
    /// <summary>
    /// Interleaved &lt;-&gt; planar shuffling for the VST bridge. Hot path, no allocs.
    /// </summary>
    internal static class VST3BufferConverter
    {
        /// <summary>
        /// L0,R0,L1,R1... -> planar[0]=L, planar[1]=R.
        /// </summary>
        /// <param name="frameCount">Frames to convert, not samples.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void InterleavedToPlanar(ReadOnlySpan<float> interleaved, float[][] planar, int channels, int frameCount)
        {
            if (channels == 2)
            {
                float[] left = planar[0];
                float[] right = planar[1];

                for (int frame = 0; frame < frameCount; frame++)
                {
                    int idx = frame * 2;
                    left[frame] = interleaved[idx];
                    right[frame] = interleaved[idx + 1];
                }
                return;
            }

            if (channels == 1)
            {
                float[] mono = planar[0];
                for (int frame = 0; frame < frameCount; frame++) mono[frame] = interleaved[frame];
                return;
            }

            for (int frame = 0; frame < frameCount; frame++)
            {
                int _base = frame * channels;
                for (int ch = 0; ch < channels; ch++)
                    planar[ch][frame] = interleaved[_base + ch];
            }
        }

        /// <summary>
        /// The way back: planar channels folded into L0,R0,L1,R1...
        /// </summary>
        /// <param name="frameCount">Frames to convert, not samples.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void PlanarToInterleaved(float[][] planar, Span<float> interleaved, int channels, int frameCount)
        {
            if (channels == 2)
            {
                float[] left = planar[0];
                float[] right = planar[1];

                for (int frame = 0; frame < frameCount; frame++)
                {
                    int idx = frame * 2;
                    interleaved[idx] = left[frame];
                    interleaved[idx + 1] = right[frame];
                }
                return;
            }

            if(channels == 1)
            {
                float[] mono = planar[0];
                for (int frame = 0; frame < frameCount; frame++) interleaved[frame] = mono[frame];
                return;
            }

            for (int frame = 0; frame < frameCount; frame++)
            {
                int _base = frame * channels;
                for (int ch = 0; ch < channels; ch++)
                    interleaved[_base + ch] = planar[ch][frame];
            }
        }
    }
}
