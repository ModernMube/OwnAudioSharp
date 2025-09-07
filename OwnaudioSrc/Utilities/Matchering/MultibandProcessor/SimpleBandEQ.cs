using System;

namespace Ownaudio.Utilities.Matchering
{
    #region Simple Band EQ

    /// <summary>
    /// Simple 3-band equalizer for individual frequency band processing
    /// </summary>
    public class SimpleBandEQ
    {
        /// <summary>
        /// Array of biquad filters for EQ processing
        /// </summary>
        private readonly MultiBandBiquadFilter[] filters;

        /// <summary>
        /// Initializes a new instance of the SimpleBandEQ class
        /// </summary>
        /// <param name="gains">Gain values in dB for each filter</param>
        /// <param name="frequencies">Center frequencies for each filter</param>
        /// <param name="sampleRate">Audio sample rate in Hz</param>
        public SimpleBandEQ(float[] gains, float[] frequencies, int sampleRate)
        {
            filters = new MultiBandBiquadFilter[3];

            for (int i = 0; i < 3; i++)
            {
                if (i < gains.Length && Math.Abs(gains[i]) > 0.1f)
                {
                    filters[i] = new MultiBandBiquadFilter(
                        frequencies[i],
                        gains[i],
                        1.0f,
                        sampleRate,
                        BiquadType.Peaking
                    );
                }
            }
        }

        /// <summary>
        /// Processes audio samples through the EQ filters
        /// </summary>
        /// <param name="samples">Audio samples to process in-place</param>
        public void Process(Span<float> samples)
        {
            foreach (var filter in filters)
            {
                filter?.Process(samples);
            }
        }

        /// <summary>
        /// Resets all EQ filter states
        /// </summary>
        public void Reset()
        {
            foreach (var filter in filters)
            {
                filter?.Reset();
            }
        }
    }

    #endregion
}
