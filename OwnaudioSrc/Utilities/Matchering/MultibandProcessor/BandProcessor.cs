using Ownaudio.Fx;
using Ownaudio.Sources;
using System;

namespace Ownaudio.Utilities.Matchering
{
    #region Band Processor

    /// <summary>
    /// Individual frequency band processor with EQ, compression, and dynamic amplification
    /// </summary>
    public class BandProcessor
    {
        /// <summary>
        /// Equalizer for the frequency band
        /// </summary>
        private readonly SimpleBandEQ equalizer;

        /// <summary>
        /// Compressor for the frequency band
        /// </summary>
        private readonly Compressor compressor;

        /// <summary>
        /// Dynamic amplifier (applied selectively)
        /// </summary>
        private readonly DynamicAmp? dynamicAmp;

        /// <summary>
        /// Index of the frequency band
        /// </summary>
        private readonly int bandIndex;

        /// <summary>
        /// Initializes a new instance of the BandProcessor class
        /// </summary>
        /// <param name="eqSettings">EQ settings for this band</param>
        /// <param name="compression">Compression settings for this band</param>
        /// <param name="dynamicAmp">Dynamic amplification settings</param>
        /// <param name="bandIndex">Index of the frequency band</param>
        public BandProcessor(float[] eqSettings, CompressionSettings compression,
                            DynamicAmpSettings dynamicAmp, int bandIndex)
        {
            this.bandIndex = bandIndex;

            equalizer = new SimpleBandEQ(eqSettings, GetBandFrequencies(bandIndex),
                                       SourceManager.OutputEngineOptions.SampleRate);

            var adjustedCompression = AdjustCompressionForBand(compression, bandIndex);
            compressor = new Compressor(
                Compressor.DbToLinear(adjustedCompression.Threshold),
                adjustedCompression.Ratio,
                adjustedCompression.AttackTime,
                adjustedCompression.ReleaseTime,
                Math.Min(Compressor.DbToLinear(adjustedCompression.MakeupGain), 2.0f),
                SourceManager.OutputEngineOptions.SampleRate
            );

            if (bandIndex == 1)
            {
                this.dynamicAmp = new DynamicAmp(
                    dynamicAmp.TargetLevel,
                    dynamicAmp.AttackTime,
                    dynamicAmp.ReleaseTime,
                    noiseThreshold: 0.005f,
                    dynamicAmp.MaxGain,
                    SourceManager.OutputEngineOptions.SampleRate,
                    rmsWindowSeconds: 0.3f
                );
            }
        }

        /// <summary>
        /// Processes audio samples through the band processor chain
        /// </summary>
        /// <param name="samples">Audio samples to process in-place</param>
        public void Process(Span<float> samples)
        {
            equalizer.Process(samples);
            compressor.Process(samples);
            dynamicAmp?.Process(samples);
        }

        /// <summary>
        /// Resets all processing states in the band processor
        /// </summary>
        public void Reset()
        {
            equalizer.Reset();
            compressor.Reset();
            dynamicAmp?.Reset();
        }

        /// <summary>
        /// Gets center frequencies for the specified frequency band
        /// </summary>
        /// <param name="bandIndex">Index of the frequency band</param>
        /// <returns>Array of center frequencies for the band</returns>
        private float[] GetBandFrequencies(int bandIndex)
        {
            switch (bandIndex)
            {
                case 0: return new float[] { 62.5f, 125f, 250f };
                case 1: return new float[] { 500f, 1000f, 1500f };
                case 2: return new float[] { 3000f, 4000f, 6000f };
                case 3: return new float[] { 10000f, 14000f, 18000f };
                default: return new float[] { 1000f, 2000f, 4000f };
            }
        }

        /// <summary>
        /// Adjusts compression settings based on frequency band characteristics
        /// </summary>
        /// <param name="baseSettings">Base compression settings</param>
        /// <param name="bandIndex">Index of the frequency band</param>
        /// <returns>Band-adjusted compression settings</returns>
        private CompressionSettings AdjustCompressionForBand(CompressionSettings baseSettings, int bandIndex)
        {
            var adjusted = new CompressionSettings
            {
                Threshold = baseSettings.Threshold,
                Ratio = baseSettings.Ratio,
                AttackTime = baseSettings.AttackTime,
                ReleaseTime = baseSettings.ReleaseTime,
                MakeupGain = baseSettings.MakeupGain
            };

            switch (bandIndex)
            {
                case 0:
                    adjusted.AttackTime *= 2.0f;
                    adjusted.ReleaseTime *= 1.5f;
                    adjusted.Threshold += 2.0f;
                    break;

                case 1:
                    break;

                case 2:
                    adjusted.AttackTime *= 0.5f;
                    adjusted.ReleaseTime *= 0.8f;
                    break;

                case 3: 
                    adjusted.AttackTime *= 0.5f;  
                    adjusted.ReleaseTime *= 0.6f;
                    adjusted.Ratio = Math.Min(adjusted.Ratio * 1.1f, 4.0f); 
                    adjusted.Threshold += 1.0f; 
                    break;
            }

            return adjusted;
        }
    }

    #endregion
}
