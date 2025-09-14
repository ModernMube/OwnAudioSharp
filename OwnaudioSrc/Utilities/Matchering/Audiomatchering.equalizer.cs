using Ownaudio.Fx;
using Ownaudio.Sources;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Ownaudio.Utilities.Matchering
{
    /// <summary>
    /// Provides audio spectrum analysis and EQ matching functionality for audio processing applications.
    /// Implements advanced FFT-based frequency analysis and intelligent EQ adjustment algorithms.
    /// </summary>
    partial class AudioAnalyzer
    {
        #region EQ Calculation and Smoothing

        /// <summary>
        /// More aggressive EQ calculation for direct processing
        /// </summary>
        /// <param name="source">Source audio spectrum to analyze</param>
        /// <param name="target">Target audio spectrum to match</param>
        /// <returns>Array of EQ adjustment values in decibels for each frequency band</returns>
        /// <remarks>
        /// Calculates the raw differences between source and target spectrums, 
        /// then applies intelligent scaling with spectral balance consideration.
        /// The method outputs detailed adjustment information to console.
        /// </remarks>
        private float[] CalculateDirectEQAdjustments(AudioSpectrum source, AudioSpectrum target)
        {
            var rawAdjustments = new float[FrequencyBands.Length];

            for (int i = 0; i < rawAdjustments.Length; i++)
            {
                float sourceLevel = 20 * (float)Math.Log10(Math.Max(source.FrequencyBands[i], 1e-10f));
                float targetLevel = 20 * (float)Math.Log10(Math.Max(target.FrequencyBands[i], 1e-10f));
                rawAdjustments[i] = targetLevel - sourceLevel;
            }

            var adjustments = ApplyIntelligentScaling(rawAdjustments);

            Console.WriteLine("Balanced EQ Adjustments:");
            var bandNames = new[] { 
                "20Hz", "25Hz", "31Hz", "40Hz", "50Hz", "63Hz", "80Hz", "100Hz", "125Hz", "160Hz",
                "200Hz", "250Hz", "315Hz", "400Hz", "500Hz", "630Hz", "800Hz", "1kHz", "1.25kHz", "1.6kHz",
                "2kHz", "2.5kHz", "3.15kHz", "4kHz", "5kHz", "6.3kHz", "8kHz", "10kHz", "12.5kHz", "16kHz"
            };

            for (int i = 0; i < adjustments.Length; i++)
            {
                Console.WriteLine($"{bandNames[i]}: {adjustments[i]:+0.1;-0.1} dB (raw: {rawAdjustments[i]:+0.1;-0.1} dB)");
            }

            return adjustments;
        }

        #endregion

        private void ApplyDirectEQProcessing(string inputFile, string outputFile,
            float[] eqAdjustments, DynamicAmpSettings dynamicAmp, 
            AudioSpectrum sourceSpectrum, AudioSpectrum targetSpectrum) //new parameters
        {
            try
            {
                Console.WriteLine($"Starting EQ processing with dynamic Q optimization: {inputFile} -> {outputFile}");

                using var source = new Source();
                source.LoadAsync(inputFile).Wait();

                if (!source.IsLoaded)
                    throw new InvalidOperationException($"Cannot load audio file: {inputFile}");

                var audioData = source.GetFloatAudioData(TimeSpan.Zero);
                var channels = source.CurrentDecoder?.StreamInfo.Channels ?? 2;
                var sampleRate = source.CurrentDecoder?.StreamInfo.SampleRate ?? 44100;

                Console.WriteLine($"Audio loaded: {channels} channels, {sampleRate} Hz");
                for (int i = 0; i < audioData.Length; i++)
                    audioData[i] *= 0.85f;

                var optimizedQFactors = CalculateOptimalQFactors(eqAdjustments, sourceSpectrum, targetSpectrum);

                var directEQ = new Equalizer30Band(sampleRate);
                
                for (int i = 0; i < FrequencyBands.Length; i++)
                {
                    directEQ.SetBandGain(i, FrequencyBands[i], optimizedQFactors[i], eqAdjustments[i]);
                }

                var globalCompressor = new Compressor(
                    Compressor.DbToLinear(-22.0f),
                    1.3f,
                    40.0f,
                    300.0f,
                    1.0f,
                    sampleRate
                );
                //var globalCompressor = new Compressor(
                //    Compressor.DbToLinear(-28.0f),  // Magasabb küszöb - kevesebb kompresszió
                //    1.15f,                          // Alacsonyabb arány - természetesebb hang
                //    60.0f,                          // Lassabb attack - simább átmenetek
                //    500.0f,                         // Hosszabb release - természetesebb lecsengés
                //    0.5f,                           // Kevesebb makeup gain - dinamika megőrzés
                //    sampleRate
                //);

                //var dynamicAmplifier = new DynamicAmp(
                //    dynamicAmp.TargetLevel + 1.0f,
                //    dynamicAmp.AttackTime * 0.8f,
                //    dynamicAmp.ReleaseTime * 1.2f,
                //    0.003f,
                //    dynamicAmp.MaxGain * 1.2f,
                //    sampleRate,
                //    0.25f
                //);
                var dynamicAmplifier = new DynamicAmp(
                    dynamicAmp.TargetLevel + 3.0f,      // Több headroom a dinamikának
                    dynamicAmp.AttackTime * 2.0f,       // Lassabb attack - kevésbé agresszív
                    dynamicAmp.ReleaseTime * 2.5f,      // Sokkal lassabb release
                    0.001f,                             // Kisebb threshold - finomabb működés
                    dynamicAmp.MaxGain * 0.65f,          // Alacsonyabb max gain - biztonságosabb
                    sampleRate,
                    0.15f                               // Kisebb mix - kevésbé drasztikus hatás
                );

                int chunkSize = 512 * channels;
                var processedData = new List<float>();
                int totalSamples = audioData.Length;
                int processedSamples = 0;

                for (int offset = 0; offset < totalSamples; offset += chunkSize)
                {
                    int samplesToProcess = Math.Min(chunkSize, totalSamples - offset);
                    var chunk = new float[samplesToProcess];
                    Array.Copy(audioData, offset, chunk, 0, samplesToProcess);

                    directEQ.Process(chunk.AsSpan());
                    globalCompressor.Process(chunk.AsSpan());
                    dynamicAmplifier.Process(chunk.AsSpan());

                    // Safety clipping
                    for (int i = 0; i < chunk.Length; i++)
                        chunk[i] = Math.Max(-1.0f, Math.Min(1.0f, chunk[i]));

                    processedData.AddRange(chunk);

                    processedSamples += samplesToProcess;
                    float progress = (float)processedSamples / totalSamples * 100f;
                    Console.Write($"\rProcessing: {progress:F1}%");
                }

                Console.WriteLine("\nWriting to file...");
                Ownaudio.Utilities.WaveFile.WriteFile(outputFile, processedData.ToArray(), sampleRate, channels, 24);
                Console.WriteLine($"Processing completed: {outputFile}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during processing: {ex.Message}");
                throw;
            }
        }


        /// <summary>
        /// Intelligent scaling that maintains spectral balance and musicality
        /// </summary>
        /// <param name="rawAdjustments">Raw EQ adjustment values calculated from spectrum differences</param>
        /// <returns>Scaled and balanced EQ adjustment values optimized for musical content</returns>
        /// <remarks>
        /// Analyzes spectral energy distribution across low, mid, and high frequency ranges.
        /// Applies dynamic global scaling based on total correction needed and frequency-specific 
        /// scaling factors. Includes musical limiting to prevent excessive boosts/cuts and 
        /// maintains spectral balance through refined correction algorithms.
        /// </remarks>
        private float[] ApplyIntelligentScaling(float[] rawAdjustments)
        {
            var adjustments = new float[rawAdjustments.Length];

            float lowEnergy = rawAdjustments.Take(9).Sum();      // 20-125 Hz
            float midEnergy = rawAdjustments.Skip(9).Take(11).Sum(); // 160-2000 Hz  
            float highEnergy = rawAdjustments.Skip(20).Sum();     // 2.5kHz+

            Console.WriteLine($"Raw energy distribution - Low:{lowEnergy:F1}dB Mid:{midEnergy:F1}dB High:{highEnergy:F1}dB");

            float totalBoost = rawAdjustments.Where(x => x > 0).Sum();

            float globalScaling = totalBoost switch
            {
                > 40f => 0.3f,  // Very conservative for large corrections
                > 25f => 0.4f,  // Conservative for moderate corrections  
                > 15f => 0.5f,  // Moderate scaling
                > 8f => 0.6f,   // Standard scaling
                _ => 0.7f       // More aggressive for small corrections
            };

            for (int i = 0; i < rawAdjustments.Length; i++)
            {
                float freq = FrequencyBands[i];
                float rawGain = rawAdjustments[i];

                float freqScaling = freq switch
                {
                    <= 40f => 0.4f,    // Very conservative deep sub-bass
                    <= 80f => 0.45f,     // Conservative sub-bass
                    <= 160f => 0.55f,    // Moderate low-bass
                    <= 400f => 0.4f,    // Good low-mid
                    <= 800f => 0.6f,    // Good mid-bass
                    <= 1600f => 0.65f,  // Moderate mid-range
                    <= 3150f => 0.55f,  // Conservative upper-mid
                    <= 6300f => 0.6f,   // Moderate presence
                    <= 10000f => 0.7f,  // Good brilliance
                    <= 12500f => 0.6f,  // Conservative upper brilliance
                    _ => 0.5f           // Conservative air
                };

                float scaledGain = rawGain * globalScaling * freqScaling;

                float maxBoost = freq switch
                {
                    < 50f => 2.7f,      // Very conservative deep sub-bass
                    < 125f => 3.3f,     // Conservative sub-bass
                    < 400f => 3.0f,     // Moderate bass boost
                    < 1000f => 5.0f,    // Good low-mid boost
                    < 2500f => 4.5f,    // Moderate mid boost
                    < 5000f => 4.0f,    // Conservative presence boost
                    < 8000f => 5.5f,    // Good upper presence boost
                    < 12500f => 5.0f,   // Conservative brilliance boost
                    _ => 4.0f           // Conservative air boost
                };

                float maxCut = 8.0f; // Standard cut limit

                adjustments[i] = Math.Max(-maxCut, Math.Min(maxBoost, scaledGain));
            }

            // Apply refined spectral balance correction
            return ApplyRefinedSpectralBalance(adjustments);
        }

        /// <summary>
        /// Refined spectral balance with specific 2-5kHz control
        /// </summary>
        /// <param name="adjustments">EQ adjustment values to balance</param>
        /// <returns>Spectrally balanced EQ adjustment values with controlled mid-presence range</returns>
        /// <remarks>
        /// Analyzes detailed energy distribution across frequency ranges (low, low-mid, mid, presence, high).
        /// Specifically controls 2-5kHz dominance to prevent harsh or fatiguing sound.
        /// Applies proportional reduction to overly dominant frequency ranges while maintaining 
        /// overall spectral coherence.
        /// </remarks>
        private float[] ApplyRefinedSpectralBalance(float[] adjustments)
        {
            float lowSum = adjustments.Take(9).Sum();            // 20-125 Hz
            float lowMidSum = adjustments.Skip(9).Take(5).Sum();  // 160-630 Hz
            float midSum = adjustments.Skip(14).Take(6).Sum();    // 800-2.5kHz
            float presenceSum = adjustments.Skip(20).Take(3).Sum(); // 3.15-5kHz
            float highSum = adjustments.Skip(23).Sum();          // 6.3kHz+

            Console.WriteLine($"Detailed energy - Low:{lowSum:F1} LowMid:{lowMidSum:F1} Mid:{midSum:F1} Presence:{presenceSum:F1} High:{highSum:F1}");

            float midPresenceSum = midSum + presenceSum; // Combined 1-4kHz

            if (midPresenceSum > 6.0f) // 2-5kHz range too strong
            {
                Console.WriteLine("Reducing 2-5kHz dominance...");
                for (int i = 20; i <= 24; i++) // 2kHz-5kHz range
                {
                    if (adjustments[i] > 2.0f) adjustments[i] *= 0.8f;
                }
            }

            // Standard checks for other ranges
            if (highSum > midPresenceSum + 4.0f) // High frequencies too dominant
            {
                Console.WriteLine("Reducing high frequency dominance...");
                for (int i = 23; i < adjustments.Length; i++) // 6.3kHz+
                {
                    adjustments[i] *= 0.9f;
                }
            }

            if (lowSum > midPresenceSum + 4.0f) // Low frequencies too dominant
            {
                Console.WriteLine("Reducing low frequency dominance...");
                for (int i = 0; i < 9; i++) // 20-125 Hz
                {
                    adjustments[i] *= 0.90f;
                }
            }

            for (int i = 26; i <= 28; i++) // 8kHz, 10kHz, 12.5kHz
            {
                if (i < adjustments.Length && adjustments[i] > 0)
                {
                    adjustments[i] *= 0.77f;
                }
            }

            return adjustments;
        }
    }
}
