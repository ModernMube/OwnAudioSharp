using Ownaudio.Processors;
using Ownaudio.Sources;
using System;

namespace Ownaudio.Utilities.Matchering
{
    #region Main Multiband Processor

    /// <summary>
    /// Advanced multiband processor with crossover filters for frequency-dependent audio processing
    /// </summary>
    public class MultibandProcessor : SampleProcessorBase
    {
        /// <summary>
        /// Crossover filter for splitting audio into frequency bands
        /// </summary>
        private readonly CrossoverFilter crossover;

        /// <summary>
        /// Individual processors for each frequency band
        /// </summary>
        private readonly BandProcessor[] bandProcessors;

        /// <summary>
        /// Crossover frequencies used for band separation
        /// </summary>
        private readonly float[] crossoverFreqs;

        /// <summary>
        /// Number of frequency bands
        /// </summary>
        private readonly int bandCount;

        /// <summary>
        /// Audio data arrays for each frequency band
        /// </summary>
        private float[][] bands;

        /// <summary>
        /// Initializes a new instance of the MultibandProcessor class
        /// </summary>
        /// <param name="eqAdjustments">EQ adjustment values for each frequency band</param>
        /// <param name="compressionSettings">Compression settings for each band</param>
        /// <param name="dynamicAmp">Dynamic amplification settings</param>
        public MultibandProcessor(float[] eqAdjustments, CompressionSettings[] compressionSettings,
                                DynamicAmpSettings dynamicAmp)
        {
            crossoverFreqs = new float[] { 250f, 2000f, 8000f };
            bandCount = crossoverFreqs.Length + 1;

            bands = new float[bandCount][];

            crossover = new CrossoverFilter(crossoverFreqs, SourceManager.OutputEngineOptions.SampleRate);

            bandProcessors = new BandProcessor[bandCount];
            for (int i = 0; i < bandCount; i++)
            {
                bandProcessors[i] = new BandProcessor(
                    GetBandEQSettings(eqAdjustments, i),
                    compressionSettings.Length > i ? compressionSettings[i] : compressionSettings[0],
                    dynamicAmp,
                    i
                );
            }
        }

        /// <summary>
        /// Processes audio samples through the multiband processor
        /// </summary>
        /// <param name="samples">Audio samples to process in-place</param>
        public override void Process(Span<float> samples)
        {
            for (int i = 0; i < bandCount; i++)
            {
                if (bands[i] == null || bands[i].Length < samples.Length)
                    bands[i] = new float[samples.Length];
            }

            crossover.ProcessToBands(samples, bands);

            for (int i = 0; i < bandCount; i++)
            {
                bandProcessors[i].Process(bands[i]);
            }

            crossover.CombineBands(samples, bands);
        }

        /// <summary>
        /// Resets all internal processing states
        /// </summary>
        public override void Reset()
        {
            crossover.Reset();
            foreach (var processor in bandProcessors)
            {
                processor.Reset();
            }
        }

        /// <summary>
        /// Maps global EQ settings to specific frequency band settings
        /// </summary>
        /// <param name="globalEQ">Global 10-band EQ settings</param>
        /// <param name="bandIndex">Index of the frequency band</param>
        /// <returns>EQ settings for the specified band</returns>
        private float[] GetBandEQSettings(float[] globalEQ, int bandIndex)
        {
            var bandEQ = new float[3];

            switch (bandIndex)
            {
                case 0:
                    bandEQ[0] = globalEQ[0];
                    bandEQ[1] = globalEQ[1];
                    bandEQ[2] = globalEQ[2];
                    break;

                case 1:
                    bandEQ[0] = globalEQ[2];
                    bandEQ[1] = globalEQ[3];
                    bandEQ[2] = globalEQ[4];
                    break;

                case 2:
                    bandEQ[0] = globalEQ[5];
                    bandEQ[1] = globalEQ[6];
                    bandEQ[2] = globalEQ[7];
                    break;

                case 3:
                    bandEQ[0] = globalEQ[7];
                    bandEQ[1] = globalEQ[8];
                    bandEQ[2] = globalEQ[9];
                    break;
            }

            return bandEQ;
        }
    }

    #endregion
}
