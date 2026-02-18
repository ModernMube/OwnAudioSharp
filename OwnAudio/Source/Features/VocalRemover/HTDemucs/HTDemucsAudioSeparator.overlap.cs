namespace OwnaudioNET.Features.Vocalremover
{
    public partial class HTDemucsAudioSeparator
    {
        #region Private Methods - Overlap-Add

        /// <summary>
        /// Apply overlap-add reconstruction with linear crossfade
        /// </summary>
        private void ApplyOverlapAdd(
            Dictionary<HTDemucsStem, float[,]> targetBuffers,
            Dictionary<HTDemucsStem, float[,]> sourceChunk,
            int position,
            int overlap,
            int chunkLength,
            int totalLength)
        {
            foreach (var kvp in sourceChunk)
            {
                var stem = kvp.Key;
                var source = kvp.Value;

                if (!targetBuffers.ContainsKey(stem))
                    continue;

                var target = targetBuffers[stem];

                // Determine regions
                int nonOverlapStart = (position == 0) ? 0 : overlap;
                int copyLength = Math.Min(chunkLength - nonOverlapStart, totalLength - position - nonOverlapStart);

                // Copy non-overlapping part
                if (copyLength > 0)
                {
                    CopyAudioRegion(source, target, nonOverlapStart, position + nonOverlapStart, copyLength);
                }

                // Blend overlapping region with linear crossfade
                if (position > 0 && overlap > 0)
                {
                    int blendLength = Math.Min(overlap, totalLength - position);
                    BlendOverlap(source, target, 0, position, blendLength);
                }
            }
        }

        /// <summary>
        /// Copy audio data from source to target
        /// </summary>
        private void CopyAudioRegion(float[,] source, float[,] target, int srcStart, int dstStart, int length)
        {
            for (int ch = 0; ch < 2; ch++)
            {
                for (int i = 0; i < length; i++)
                {
                    if (dstStart + i < target.GetLength(1))
                    {
                        target[ch, dstStart + i] = source[ch, srcStart + i];
                    }
                }
            }
        }

        /// <summary>
        /// Blend overlapping region with constant-power crossfade.
        /// Uses cosine-based fade to maintain constant energy across transition.
        /// </summary>
        private void BlendOverlap(float[,] source, float[,] target, int srcStart, int dstStart, int length)
        {
            for (int ch = 0; ch < 2; ch++)
            {
                for (int i = 0; i < length; i++)
                {
                    if (dstStart + i < target.GetLength(1))
                    {
                        // Constant-power crossfade using cosine/sine
                        // This maintains perceived loudness better than linear fade
                        float position = (float)i / (float)length;  // 0.0 to 1.0
                        float angle = position * (float)Math.PI / 2.0f;  // 0 to π/2

                        float fadeOut = (float)Math.Cos(angle);  // 1.0 to 0.0 (previous chunk)
                        float fadeIn = (float)Math.Sin(angle);   // 0.0 to 1.0 (current chunk)

                        target[ch, dstStart + i] = target[ch, dstStart + i] * fadeOut +
                                                   source[ch, srcStart + i] * fadeIn;
                    }
                }
            }
        }

        #endregion
    }
}
