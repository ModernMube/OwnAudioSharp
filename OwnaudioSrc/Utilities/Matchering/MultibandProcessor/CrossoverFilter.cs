using System;

namespace Ownaudio.Utilities.Matchering
{
    #region Crossover Filter

    /// <summary>
    /// Crossover filter for splitting audio into frequency bands using Linkwitz-Riley filters
    /// </summary>
    public class CrossoverFilter
    {
        /// <summary>
        /// Low-pass filters for each crossover frequency
        /// </summary>
        private readonly LinkwitzRileyFilter[] lowpassFilters;

        /// <summary>
        /// High-pass filters for each crossover frequency
        /// </summary>
        private readonly LinkwitzRileyFilter[] highpassFilters;

        /// <summary>
        /// Crossover frequencies in Hz
        /// </summary>
        private readonly float[] crossoverFreqs;

        /// <summary>
        /// Number of frequency bands
        /// </summary>
        private readonly int bandCount;

        /// <summary>
        /// Initializes a new instance of the CrossoverFilter class
        /// </summary>
        /// <param name="crossoverFrequencies">Array of crossover frequencies in Hz</param>
        /// <param name="sampleRate">Audio sample rate in Hz</param>
        public CrossoverFilter(float[] crossoverFrequencies, int sampleRate)
        {
            crossoverFreqs = crossoverFrequencies;
            bandCount = crossoverFreqs.Length + 1;

            lowpassFilters = new LinkwitzRileyFilter[crossoverFreqs.Length];
            highpassFilters = new LinkwitzRileyFilter[crossoverFreqs.Length];

            for (int i = 0; i < crossoverFreqs.Length; i++)
            {
                lowpassFilters[i] = new LinkwitzRileyFilter(crossoverFreqs[i], sampleRate, FilterType.Lowpass);
                highpassFilters[i] = new LinkwitzRileyFilter(crossoverFreqs[i], sampleRate, FilterType.Highpass);
            }
        }

        /// <summary>
        /// Splits input signal into multiple frequency bands
        /// </summary>
        /// <param name="input">Input audio signal</param>
        /// <param name="bands">Output frequency bands</param>
        public void ProcessToBands(Span<float> input, float[][] bands)
        {
            for (int i = 0; i < bands.Length; i++)
            {
                var targetSpan = bands[i].AsSpan(0, input.Length);
                input.CopyTo(targetSpan);
            }

            for (int i = 0; i < crossoverFreqs.Length; i++)
            {
                lowpassFilters[i].Process(bands[i].AsSpan(0, input.Length));
                highpassFilters[i].Process(bands[i + 1].AsSpan(0, input.Length));
            }
        }

        /// <summary>
        /// Combines processed frequency bands back into single output signal
        /// </summary>
        /// <param name="output">Output audio signal</param>
        /// <param name="bands">Input frequency bands to combine</param>
        public void CombineBands(Span<float> output, float[][] bands)
        {
            output.Fill(0);

            for (int bandIndex = 0; bandIndex < bands.Length; bandIndex++)
            {
                var bandSpan = bands[bandIndex].AsSpan(0, output.Length);
                for (int i = 0; i < output.Length; i++)
                {
                    output[i] += bandSpan[i];
                    output[i] = Math.Max(-0.95f, Math.Min(0.95f, output[i]));
                }
            }
        }

        /// <summary>
        /// Resets all crossover filter states
        /// </summary>
        public void Reset()
        {
            foreach (var filter in lowpassFilters)
                filter.Reset();
            foreach (var filter in highpassFilters)
                filter.Reset();
        }
    }

    #endregion
}
