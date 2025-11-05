using OwnaudioNET.Effects;
using OwnaudioNET.Sources;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OwnaudioNET.Features.Matchering
{
    /// <summary>
    /// Provides audio spectrum analysis and EQ matching functionality for audio processing applications.
    /// Implements advanced FFT-based frequency analysis and intelligent EQ adjustment algorithms.
    /// </summary>
    partial class AudioAnalyzer
    {
        #region EQ Calculation and Smoothing

        /// <summary>
        /// Calculates aggressive EQ adjustments for direct audio processing by analyzing source and target spectrums.
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

            //for (int i = 0; i < adjustments.Length; i++)
            //{
            //    Console.WriteLine($"{bandNames[i]}: {adjustments[i]:+0.1;-0.1} dB (raw: {rawAdjustments[i]:+0.1;-0.1} dB)");
            //}

            return adjustments;
        }

        /// <summary>
        /// Applies intelligent scaling to raw EQ adjustments that maintains spectral balance and musicality.
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
                    <= 40f => 0.4f,
                    <= 80f => 0.45f,
                    <= 160f => 0.55f,
                    <= 400f => 0.4f,
                    <= 800f => 0.6f,
                    <= 1600f => 0.65f,
                    <= 2000f => 0.7f,   // New range: vocal fundamental frequencies
                    <= 2500f => 0.8f,   // Increased from 0.55f to 0.8f (vocal presence)
                    <= 3150f => 0.85f,  // Increased from 0.55f to 0.85f (critical clarity)
                    <= 4000f => 0.8f,   // New: important intelligibility range
                    <= 5000f => 0.7f,   // Increased from 0.6f to 0.7f
                    <= 6300f => 0.8f,
                    <= 8000f => 0.88f,
                    <= 10000f => 0.95f,
                    <= 12500f => 0.85f,
                    _ => 0.8f
                };

                float scaledGain = rawGain * globalScaling * freqScaling;

                float maxBoost = freq switch
                {
                    < 50f => 2.7f,
                    < 125f => 3.3f,
                    < 400f => 3.0f,
                    < 1000f => 5.0f,
                    < 2000f => 5.5f,    // New: vocal fundamental frequencies
                    < 2500f => 6.5f,    // Increased from 4.5f to 6.5f (vocal presence)
                    < 3200f => 7.0f,    // New: critical clarity range
                    < 4000f => 6.5f,    // Increased from 4.0f to 6.5f (intelligibility)
                    < 5000f => 5.5f,    // Increased from 4.0f to 5.5f
                    < 6300f => 6.8f,
                    < 8000f => 7.5f,
                    < 10000f => 7.8f,
                    < 12500f => 7.5f,
                    _ => 6.5f
                };

                float maxCut = 8.0f; // Standard cut limit

                adjustments[i] = Math.Max(-maxCut, Math.Min(maxBoost, scaledGain));
            }

            // Apply refined spectral balance correction
            return ApplyRefinedSpectralBalance(adjustments);
        }

        /// <summary>
        /// Applies refined spectral balance correction with specific 2-5kHz vocal presence control.
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
            float vocalPresenceSum = adjustments.Skip(20).Take(4).Sum(); // 2.5-4kHz (vocal presence)
            float highPresenceSum = adjustments.Skip(24).Take(2).Sum();  // 5-6.3kHz
            float highSum = adjustments.Skip(26).Sum();          // 8kHz+

            // Only intervene in extreme cases
            if (vocalPresenceSum > 12.0f) // Higher threshold
            {
                Console.WriteLine("Limiting extreme vocal presence boost...");
                for (int i = 20; i <= 23; i++) // 2.5kHz-4kHz range
                {
                    if (adjustments[i] > 5.0f) // Only for large boosts
                    {
                        adjustments[i] *= 0.9f; // Gentler limitation
                    }
                }
            }

            // Low frequency dominance control unchanged
            if (lowSum > vocalPresenceSum + 6.0f) // Higher tolerance
            {
                Console.WriteLine("Reducing low frequency dominance...");
                for (int i = 0; i < 9; i++)
                {
                    adjustments[i] *= 0.85f;
                }
            }

            return adjustments;
        }

        #endregion

        #region Direct EQ Processing

        /// <summary>
        /// Applies direct EQ processing to an audio file with dynamic Q optimization and advanced audio processing chain.
        /// </summary>
        /// <param name="inputFile">Path to the input audio file</param>
        /// <param name="outputFile">Path to the output processed audio file</param>
        /// <param name="eqAdjustments">Array of EQ adjustment values in decibels for each frequency band</param>
        /// <param name="dynamicAmp">Dynamic amplifier settings for level adjustment</param>
        /// <param name="sourceSpectrum">Source audio spectrum for Q factor optimization</param>
        /// <param name="targetSpectrum">Target audio spectrum for Q factor optimization</param>
        /// <exception cref="InvalidOperationException">Thrown when the audio file cannot be loaded</exception>
        /// <exception cref="Exception">Thrown when any processing error occurs</exception>
        /// <remarks>
        /// Loads the audio file, applies pre-gain reduction, calculates optimal Q factors for EQ bands,
        /// processes the audio through a 30-band equalizer, global compressor, and dynamic amplifier.
        /// Includes safety clipping and progress reporting during processing.
        /// </remarks>
        private void ApplyDirectEQProcessing(string inputFile, string outputFile,
            float[] eqAdjustments, DynamicAmpSettings dynamicAmp,
            AudioSpectrum sourceSpectrum, AudioSpectrum targetSpectrum)
        {
            try
            {
                Console.WriteLine($"Starting EQ processing with dynamic Q optimization: {inputFile} -> {outputFile}");

                //using var source = new Source();

                using var source = new FileSource(inputFile);
                //source.LoadAsync(inputFile).Wait();

                if (source.Duration == 0)
                    throw new InvalidOperationException($"Cannot load audio file: {inputFile}");

                var audioData = source.GetFloatAudioData(TimeSpan.Zero);

                var channels = source.StreamInfo.Channels;
                var sampleRate = source.StreamInfo.SampleRate;

                Console.WriteLine($"Audio loaded: {channels} channels, {sampleRate} Hz");
                for (int i = 0; i < audioData.Length; i++)
                    audioData[i] *= 0.9f;

                var optimizedQFactors = CalculateOptimalQFactors(eqAdjustments, sourceSpectrum, targetSpectrum);

                var directEQ = new Equalizer30BandEffect(sampleRate);

                for (int i = 0; i < FrequencyBands.Length; i++)
                {
                    directEQ.SetBandGain(i, FrequencyBands[i], optimizedQFactors[i], eqAdjustments[i]);
                }

                var globalCompressor = new CompressorEffect(
                    CompressorEffect.DbToLinear(-26.0f),      
                    1.1f,                               
                    60.0f,                              
                    500.0f,                             
                    1.0f,
                    sampleRate
                );

                var dynamicAmplifier = new DynamicAmpEffect(
                    dynamicAmp.TargetLevel + 1.0f,      
                    dynamicAmp.AttackTime * 0.8f,       
                    dynamicAmp.ReleaseTime * 1.0f,      
                    0.001f,                             
                    dynamicAmp.MaxGain * 0.85f,         
                    sampleRate,
                    0.15f                               
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

                    directEQ.Process(chunk.AsSpan(), chunkSize);
                    globalCompressor.Process(chunk.AsSpan(), chunkSize);
                    dynamicAmplifier.Process(chunk.AsSpan(), chunkSize);

                    // Safety clipping
                    for (int i = 0; i < chunk.Length; i++)
                        chunk[i] = Math.Max(-1.0f, Math.Min(1.0f, chunk[i]));

                    processedData.AddRange(chunk);

                    processedSamples += samplesToProcess;
                    float progress = (float)processedSamples / totalSamples * 100f;
                    Console.Write($"\rProcessing: {progress:F1}%");
                }

                Console.WriteLine("\nWriting to file...");
                OwnaudioLegacy.Utilities.WaveFile.WriteFile(outputFile, processedData.ToArray(), sampleRate, channels, 24);
                Console.WriteLine($"Processing completed: {outputFile}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during processing: {ex.Message}");
                throw;
            }
        }

        #endregion
    }
}
