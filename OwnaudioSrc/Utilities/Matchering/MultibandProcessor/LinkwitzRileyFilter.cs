using System;

namespace Ownaudio.Utilities.Matchering
{
    #region Linkwitz-Riley Filter

    /// <summary>
    /// Enumeration of basic filter types
    /// </summary>
    public enum FilterType
    {
        /// <summary>
        /// Low-pass filter type
        /// </summary>
        Lowpass,

        /// <summary>
        /// High-pass filter type
        /// </summary>
        Highpass
    }

    /// <summary>
    /// Linkwitz-Riley crossover filter implementation (24dB/octave, 4th order)
    /// </summary>
    public class LinkwitzRileyFilter
    {
        /// <summary>
        /// First stage of the cascaded biquad filters
        /// </summary>
        private readonly MultiBandBiquadFilter stage1;

        /// <summary>
        /// Second stage of the cascaded biquad filters
        /// </summary>
        private readonly MultiBandBiquadFilter stage2;

        /// <summary>
        /// Initializes a new instance of the LinkwitzRileyFilter class
        /// </summary>
        /// <param name="frequency">Crossover frequency in Hz</param>
        /// <param name="sampleRate">Audio sample rate in Hz</param>
        /// <param name="type">Filter type (lowpass or highpass)</param>
        public LinkwitzRileyFilter(float frequency, int sampleRate, FilterType type)
        {
            var biquadType = type == FilterType.Lowpass ? BiquadType.Lowpass : BiquadType.Highpass;

            stage1 = new MultiBandBiquadFilter(frequency, 0, 0.7071f, sampleRate, biquadType);
            stage2 = new MultiBandBiquadFilter(frequency, 0, 0.5f, sampleRate, biquadType);
        }

        /// <summary>
        /// Processes audio samples through both filter stages
        /// </summary>
        /// <param name="samples">Audio samples to process in-place</param>
        public void Process(Span<float> samples)
        {
            stage1.Process(samples);
            stage2.Process(samples);
        }

        /// <summary>
        /// Resets both filter stages
        /// </summary>
        public void Reset()
        {
            stage1.Reset();
            stage2.Reset();
        }
    }

    #endregion
}
