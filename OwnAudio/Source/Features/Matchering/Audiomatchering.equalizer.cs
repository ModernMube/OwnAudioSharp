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

            // 1. Smooth the spectrums first to avoid jagged EQ curves
            // This is key for T-RackS style "musical" matching
            float[] smoothedSource = SmoothSpectrum(source.FrequencyBands);
            float[] smoothedTarget = SmoothSpectrum(target.FrequencyBands);

            for (int i = 0; i < rawAdjustments.Length; i++)
            {
                float sourceLevel = 20 * (float)Math.Log10(Math.Max(smoothedSource[i], 1e-10f));
                float targetLevel = 20 * (float)Math.Log10(Math.Max(smoothedTarget[i], 1e-10f));
                rawAdjustments[i] = targetLevel - sourceLevel;
            }

            var adjustments = ApplyIntelligentScaling(rawAdjustments);

            Console.WriteLine("Balanced EQ Adjustments:");
            return adjustments;
        }

        /// <summary>
        /// Smooths spectral data using a weighted moving average.
        /// </summary>
        private float[] SmoothSpectrum(float[] spectrum)
        {
            float[] smoothed = new float[spectrum.Length];
            for (int i = 0; i < spectrum.Length; i++)
            {
                float sum = spectrum[i] * 2.0f; // Center weight
                float div = 2.0f;

                if (i > 0) { sum += spectrum[i - 1]; div += 1.0f; }
                if (i < spectrum.Length - 1) { sum += spectrum[i + 1]; div += 1.0f; }

                // Wider smoothing for high frequencies to avoid harshness
                if (i > 20) 
                {
                    if (i > 1) { sum += spectrum[i - 2] * 0.5f; div += 0.5f; }
                    if (i < spectrum.Length - 2) { sum += spectrum[i + 2] * 0.5f; div += 0.5f; }
                }

                smoothed[i] = sum / div;
            }
            return smoothed;
        }

        /// <summary>
        /// Applies intelligent scaling to raw EQ adjustments that maintains spectral balance and musicality.
        /// </summary>
        private float[] ApplyIntelligentScaling(float[] rawAdjustments)
        {
            var adjustments = new float[rawAdjustments.Length];

            float totalBoost = rawAdjustments.Where(x => x > 0).Sum();

            // Tuned for "Closer Matching" (less restrictive than before)
            float globalScaling = totalBoost switch
            {
                > 60f => 0.5f,  // Only damp very extreme corrections
                > 40f => 0.6f,  
                > 20f => 0.8f,  
                _ => 1.0f       // Allow full correction for small differences
            };

            for (int i = 0; i < rawAdjustments.Length; i++)
            {
                float freq = FrequencyBands[i];
                float rawGain = rawAdjustments[i];

                // Frequency scaling - allow more correction in lows/highs, careful in mids
                float freqScaling = freq switch
                {
                    <= 100f => 0.9f,   // Solid low end matching
                    <= 1000f => 0.8f,  // Mids slightly protected
                    <= 4000f => 0.85f, // Critical vocal range
                    _ => 0.95f         // Airy highs
                };

                float scaledGain = rawGain * globalScaling * freqScaling;

                // Increased limits for closer matching
                float maxBoost = 9.0f; 
                float maxCut = 12.0f;

                adjustments[i] = Math.Max(-maxCut, Math.Min(maxBoost, scaledGain));
            }

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

                // Apply safe headroom pre-gain
                for (int i = 0; i < audioData.Length; i++)
                    audioData[i] *= 0.85f; 

                // Calculate optimal Q factors
                var optimizedQFactors = CalculateOptimalQFactors(eqAdjustments, sourceSpectrum, targetSpectrum);

                // 1. Equalizer
                var directEQ = new Equalizer30BandEffect();
                for (int i = 0; i < FrequencyBands.Length; i++)
                {
                    directEQ.SetBandGain(i, FrequencyBands[i], optimizedQFactors[i], eqAdjustments[i]);
                }

                // 2. Compressor (Dynamic Settings)
                // Use the calculated threshold and ratio from Crest Factor analysis
                var globalCompressor = new CompressorEffect(
                    CompressorEffect.DbToLinear(compSettings.Threshold), // Dynamic Threshold
                    compSettings.Ratio,                                  // Dynamic Ratio
                    15.0f,    // Fast-ish attack
                    150.0f,   // Smooth release
                    1.0f      // No makeup here, let DynamicAmp handle levels
                );

                // 3. Dynamic Amplifier
                // Gentle leveling, not maximizing
                var dynamicAmplifier = new DynamicAmpEffect(
                    dynamicAmp.TargetLevel - 1.5f, // Reduced by 2.5dB total (was +1.0) to avoid "too loud" feel
                    dynamicAmp.AttackTime * 0.8f,
                    dynamicAmp.ReleaseTime * 1.0f,
                    0.001f,
                    dynamicAmp.MaxGain * 0.85f,
                    sampleRate,
                    0.15f
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
