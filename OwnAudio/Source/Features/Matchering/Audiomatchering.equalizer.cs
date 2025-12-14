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
        private float[] CalculateDirectEQAdjustments(AudioSpectrum source, AudioSpectrum target)
        {
            var rawAdjustments = new float[FrequencyBands.Length];

            // 1. Smooth the spectrums first to prevent tracking random noise spikes
        // Increased smoothing (0.5) to ensure we match tonal balance, not jagged noise
        float[] smoothedSource = SmoothSpectrum(source.FrequencyBands, 0.5f);
        float[] smoothedTarget = SmoothSpectrum(target.FrequencyBands, 0.5f);

            for (int i = 0; i < rawAdjustments.Length; i++)
            {
                float sourceLinear = Math.Max(smoothedSource[i], 1e-10f);
                float targetLinear = Math.Max(smoothedTarget[i], 1e-10f);
                
                float sourceLevel = 20 * (float)Math.Log10(sourceLinear);
                float targetLevel = 20 * (float)Math.Log10(targetLinear);

                // SAFETY: Noise Floor Protection
                // If source is extremely quiet (below -80 dB noise floor), DO NOT BOOST it to match target.
                // Relaxed from -60dB to -80dB to allow more legitimate corrections
                // This prevents boosting extreme hiss/noise to match musical elements.
                if (sourceLevel < -80.0f && targetLevel > sourceLevel)
                {
                    // Allow cut (if source is noise but louder than target?), but restrict boost.
                    rawAdjustments[i] = 0.0f; 
                }
                else
                {
                    rawAdjustments[i] = targetLevel - sourceLevel;
                }
            }

            var adjustments = ApplyIntelligentScaling(rawAdjustments);

            // COMPREHENSIVE DEBUG OUTPUT
            Console.WriteLine("\n=== CALCULATED EQ ADJUSTMENTS ===");
            string[] bandNames = new[] {
                "20Hz", "25Hz", "31Hz", "40Hz", "50Hz", "63Hz", "80Hz", "100Hz", "125Hz", "160Hz",
                "200Hz", "250Hz", "315Hz", "400Hz", "500Hz", "630Hz", "800Hz", "1kHz", "1.25kHz", "1.6kHz",
                "2kHz", "2.5kHz", "3.15kHz", "4kHz", "5kHz", "6.3kHz", "8kHz", "10kHz", "12.5kHz", "16kHz"
            };
            
            for (int i = 0; i < adjustments.Length; i++)
            {
                float srcDb = 20 * (float)Math.Log10(Math.Max(smoothedSource[i], 1e-10f));
                float tgtDb = 20 * (float)Math.Log10(Math.Max(smoothedTarget[i], 1e-10f));
                string limited = (Math.Abs(adjustments[i] - rawAdjustments[i]) > 0.01f) ? " [LIMITED]" : "";
                
                Console.WriteLine($"{bandNames[i],8}: {adjustments[i],6:F1} dB (Raw: {rawAdjustments[i],6:F1} dB) " +
                                $"[Src: {srcDb,6:F1} dB -> Tgt: {tgtDb,6:F1} dB]{limited}");
            }
            
            return adjustments;
        }

        /// <summary>
    /// Smooths spectral data using adaptive weighted moving average.
    /// </summary>
    /// <param name="spectrum">Spectrum data to smooth</param>
    /// <param name="smoothingFactor">Smoothing intensity (0.0 = no smoothing, 1.0 = heavy smoothing)</param>
    private float[] SmoothSpectrum(float[] spectrum, float smoothingFactor = 0.25f)
    {
        // Adaptive Smoothing: Configurable balance between detail preservation and noise reduction
        // Lower smoothingFactor = more detail preserved (surgical matching)
        // Higher smoothingFactor = more smoothing (musical matching)
        float[] smoothed = new float[spectrum.Length];
        
        for (int i = 0; i < spectrum.Length; i++)
        {
            // Adaptive weighting based on smoothing factor
            float centerWeight = 1.0f + smoothingFactor * 2.0f; // Was fixed at 4.0f
            float neighborWeight = smoothingFactor; // Was fixed at 1.0f
            
            float sum = spectrum[i] * centerWeight;
            float div = centerWeight;

            if (i > 0) { sum += spectrum[i - 1] * neighborWeight; div += neighborWeight; }
            if (i < spectrum.Length - 1) { sum += spectrum[i + 1] * neighborWeight; div += neighborWeight; }

            smoothed[i] = sum / div;
        }
        return smoothed;
    }

        /// <summary>
        /// Applies intelligent scaling to raw EQ adjustments.
        /// SURGICAL MODE: Forces 100% application for maximum similarity.
        /// </summary>
        private float[] ApplyIntelligentScaling(float[] rawAdjustments)
        {
            var adjustments = new float[rawAdjustments.Length];

            // SURGICAL MATCHING: No global damping.
            float globalScaling = 1.0f;

            for (int i = 0; i < rawAdjustments.Length; i++)
            {
                // No frequency Dependent scaling - Flat 100% transfer
                float scaledGain = rawAdjustments[i] * globalScaling;

                // Max limits allowed by DSP (prevent digital clipping/instability)
                // FIXED: Increased to 18dB to match Equalizer30BandEffect capability
                // This allows full spectral matching range
                float maxBoost = 18.0f;  // Matches Equalizer capacity
                float maxCut = 18.0f;    // Symmetric limits

                adjustments[i] = Math.Max(-maxCut, Math.Min(maxBoost, scaledGain));
            }

            // Skip RefinedSpectralBalance for surgical matching to allow exact curve copying
            return adjustments;
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
        /// Processes audio directly through effect chain without real-time playback.
        /// Creates effects, initializes them, and processes audio in chunks.
        /// </remarks>
        /// <summary>
        /// Applies direct EQ processing to an audio file with dynamic Q optimization and advanced audio processing chain.
        /// </summary>
        private void ApplyDirectEQProcessing(string inputFile, string outputFile,
            float[] eqAdjustments, DynamicAmpSettings dynamicAmp,
            (float Threshold, float Ratio) compSettings,
            AudioSpectrum sourceSpectrum, AudioSpectrum targetSpectrum)
        {
            try
            {
                Console.WriteLine($"Starting EQ processing with direct effect chain: {inputFile} -> {outputFile}");

                // Load source audio file
                using var fileSource = new FileSource(inputFile);

                if (fileSource.Duration == 0)
                    throw new InvalidOperationException($"Cannot load audio file: {inputFile}");

                var audioData = fileSource.GetFloatAudioData(TimeSpan.Zero);
                var channels = fileSource.StreamInfo.Channels;
                var sampleRate = fileSource.StreamInfo.SampleRate;

                // SMART HEADROOM CALCULATION:
                // Calculate headroom based on average boost rather than max boost
                // to avoid excessive attenuation that degrades SNR
                float maxBoost = eqAdjustments.Max();
                float totalBoost = eqAdjustments.Where(x => x > 0).Sum();
                int boostCount = eqAdjustments.Count(x => x > 0);
                float avgBoost = boostCount > 0 ? totalBoost / boostCount : 0;
                
                // Use average boost + safety margin instead of max boost
                // This provides adequate headroom without excessive attenuation
                float effectiveBoost = Math.Min(maxBoost, avgBoost + 4.0f);
                float preGainDb = 0.0f;
                
                if (effectiveBoost > 0)
                {
                    // Attenuate by effective boost amount + small safety margin
                    preGainDb = -(effectiveBoost + 2.0f); 
                    // Clamp to reasonable range to preserve SNR
                    preGainDb = Math.Clamp(preGainDb, -12.0f, 0.0f);
                    
                    float linearPreGain = (float)Math.Pow(10, preGainDb / 20.0f);
                    
                    Console.WriteLine($"Applying Smart Headroom: {preGainDb:F1}dB (Max: {maxBoost:F1}dB, Avg: {avgBoost:F1}dB, Effective: {effectiveBoost:F1}dB)");
                    
                    for (int i = 0; i < audioData.Length; i++)
                        audioData[i] *= linearPreGain;
                }

                // Calculate optimal Q factors
                var optimizedQFactors = CalculateOptimalQFactors(eqAdjustments, sourceSpectrum, targetSpectrum);

                // 1. Equalizer
                var directEQ = new Equalizer30BandEffect();
                
                // DEBUG: Show applied EQ configuration
                Console.WriteLine("\n=== APPLIED EQ CONFIGURATION ===");
                string[] bandNames = new[] {
                    "20Hz", "25Hz", "31Hz", "40Hz", "50Hz", "63Hz", "80Hz", "100Hz", "125Hz", "160Hz",
                    "200Hz", "250Hz", "315Hz", "400Hz", "500Hz", "630Hz", "800Hz", "1kHz", "1.25kHz", "1.6kHz",
                    "2kHz", "2.5kHz", "3.15kHz", "4kHz", "5kHz", "6.3kHz", "8kHz", "10kHz", "12.5kHz", "16kHz"
                };
                
                for (int i = 0; i < FrequencyBands.Length; i++)
                {
                    directEQ.SetBandGain(i, FrequencyBands[i], optimizedQFactors[i], eqAdjustments[i]);
                    Console.WriteLine($"Band {i,2} ({bandNames[i],8}): Gain = {eqAdjustments[i],6:F1} dB, Q = {optimizedQFactors[i]:F2}");
                }

                // 2. Compressor (Dynamic Settings)
                // Use the calculated threshold and ratio from Crest Factor analysis
                var globalCompressor = new CompressorEffect(
                    CompressorEffect.DbToLinear(compSettings.Threshold), // Dynamic Threshold
                    compSettings.Ratio,                                  // Dynamic Ratio
                    10.0f,    // Fast attack (Surgical)
                    100.0f,   // Fast release (Surgical)
                    1.0f      // No makeup here, let DynamicAmp handle levels
                );

                // 3. Dynamic Amplifier
                // Adjust max gain to ensure we can recover from the pre-gain attenuation
                float headroomRecoveryGain = (effectiveBoost > 0) ? (float)Math.Pow(10, (effectiveBoost + 2.0f) / 20.0f) : 1.0f;
                float totalMaxGain = dynamicAmp.MaxGain * headroomRecoveryGain;

                var dynamicAmplifier = new DynamicAmpEffect(
                    dynamicAmp.TargetLevel - 0.2f, 
                    dynamicAmp.AttackTime,         
                    dynamicAmp.ReleaseTime,        
                    0.001f,
                    totalMaxGain,                  // Expanded gain capability
                    sampleRate,
                    0.15f,
                    headroomRecoveryGain           // Initial gain to match headroom attenuation
                );

                // 4. Limiter (New Mastering Stage)
                // Transparent mastering settings
                var outputLimiter = new LimiterEffect(
                    sampleRate,
                    threshold: -0.5f,  // Catch peaks just below 0
                    ceiling: -0.2f,    // Safe true peak ceiling
                    release: 60.0f,    // Transparent release
                    lookAheadMs: 5.0f
                );

                // Initialize all effects
                var audioConfig = new Ownaudio.Core.AudioConfig
                {
                    SampleRate = sampleRate,
                    Channels = channels,
                    BufferSize = 512
                };

                directEQ.Initialize(audioConfig);
                globalCompressor.Initialize(audioConfig);
                dynamicAmplifier.Initialize(audioConfig);
                outputLimiter.Initialize(audioConfig);

                Console.WriteLine("Effects initialized (EQ -> Comp -> Amp -> Limiter), processing audio...");

                // Process audio in chunks
                var processedData = new List<float>(audioData.Length);
                int framesPerChunk = 512;
                int samplesPerChunk = framesPerChunk * channels;
                int totalSamples = audioData.Length;
                int processedSamples = 0;

                for (int offset = 0; offset < totalSamples; offset += samplesPerChunk)
                {
                    int samplesToProcess = Math.Min(samplesPerChunk, totalSamples - offset);
                    int framesToProcess = samplesToProcess / channels;

                    var chunk = new float[samplesToProcess];
                    Array.Copy(audioData, offset, chunk, 0, samplesToProcess);

                    // Chain Processing
                    directEQ.Process(chunk.AsSpan(), framesToProcess);
                    globalCompressor.Process(chunk.AsSpan(), framesToProcess);
                    dynamicAmplifier.Process(chunk.AsSpan(), framesToProcess);
                    outputLimiter.Process(chunk.AsSpan(), framesToProcess);

                    processedData.AddRange(chunk);

                    processedSamples += samplesToProcess;
                    float progress = (float)processedSamples / totalSamples * 100f;
                    Console.Write($"\rProcessing: {progress:F1}%");
                }

                Console.WriteLine("\n\nWriting to file...");
                OwnaudioNET.Recording.WaveFile.Create(outputFile, processedData.ToArray(), sampleRate, channels, 24);
                Console.WriteLine($"Processing completed: {outputFile}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nError during processing: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                throw;
            }
        }

        #endregion
    }
}
