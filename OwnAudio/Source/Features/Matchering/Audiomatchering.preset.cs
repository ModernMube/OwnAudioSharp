using OwnaudioNET.Effects;
using OwnaudioNET.Sources;
using Logger;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace OwnaudioNET.Features.Matchering
{
    partial class AudioAnalyzer
    {
        /// <summary>
        /// Enhanced preset processing using base sample as reference
        /// First applies preset to base sample, then matches source to the processed base sample
        /// </summary>
        /// <param name="sourceFile">Source audio file to process</param>
        /// <param name="baseSampleFile">Base reference sample (20-30 sec FLAC)</param>
        /// <param name="outputFile">Final output file path</param>
        /// <param name="system">Playback system preset to apply</param>
        /// <param name="tempDirectory">Directory for temporary files (optional)</param>
        /// <param name="eqOnlyMode">If true, applies only EQ without compression/dynamics</param>
        public void ProcessWithEnhancedPreset(string sourceFile, string outputFile,
            PlaybackSystem system, string? tempDirectory = null, bool eqOnlyMode = true)
        {
            if (string.IsNullOrEmpty(tempDirectory))
                tempDirectory = Path.GetTempPath();

            // Generate temporary file paths
            string processedBaseSample = Path.Combine(tempDirectory,
                $"processed_base_{system}_{DateTime.Now.Ticks}.wav");

            string baseSampleFile = Path.Combine(tempDirectory,
                $"base_sample_{system}_{DateTime.Now.Ticks}.wav");

            if (!LoadBaseSample(baseSampleFile))
                return;

            try
            {
                Log.Info($"=== ENHANCED PRESET PROCESSING: {SystemPresets[system].Name} ===");
                Log.Info($"Mode: {(eqOnlyMode ? "EQ Only" : "Full Effects Chain")}");

                // Step 1: Apply preset effects to base sample
                ApplyPresetToBaseSample(baseSampleFile, processedBaseSample, system, eqOnlyMode);

                // Step 2: Use processed base sample as target for EQ matching
                ProcessEQMatching(sourceFile, processedBaseSample, outputFile);

                Log.Info($"Enhanced preset processing completed: {outputFile}");
                // PrintEnhancedPresetResults(sourceFile, baseSampleFile, processedBaseSample, outputFile, system);
                // DISABLED: This method creates 3 additional FileSource instances which causes MiniAudio DLL crash (0xC0000005)
                // The native library does not handle rapid create/destroy cycles well
            }
            finally
            {
                try
                {
                    if (File.Exists(processedBaseSample))
                        File.Delete(processedBaseSample);

                    if (File.Exists(baseSampleFile))
                        File.Delete(baseSampleFile);
                }
                catch (Exception ex)
                {
                    Log.Warning($"Could not delete temporary file: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Applies preset effects to the base sample to create enhanced target
        /// </summary>
        /// <param name="baseSampleFile">Input base sample file</param>
        /// <param name="processedBaseSample">Output processed base sample file</param>
        /// <param name="system">Playback system preset</param>
        /// <param name="eqOnlyMode">If true, applies only EQ without compression/dynamics</param>
        private void ApplyPresetToBaseSample(string baseSampleFile, string processedBaseSample,
            PlaybackSystem system, bool eqOnlyMode = false)
        {
            var preset = SystemPresets[system];

            // CRITICAL: Lock to prevent race condition during MiniAudio initialization
            // This FileSource creation must be synchronized with AnalyzeAudioFile()
            float[] audioData;
            int channels;
            int sampleRate;

            lock (_analyzerLock)
            {
                using var source = new FileSource(baseSampleFile);

                if (source.Duration == 0)
                    throw new InvalidOperationException($"Cannot load base sample file: {baseSampleFile}");

                audioData = source.GetFloatAudioData(TimeSpan.Zero);
                channels = source.StreamInfo.Channels;
                sampleRate = source.StreamInfo.SampleRate;
            }

            Log.Info($"Base sample loaded: {audioData.Length / channels / sampleRate:F1}s, {channels}ch, {sampleRate}Hz");

            // Calculate current RMS for level matching later
            float originalRMS = CalculateRMS(audioData);

            // Minimal gain reduction for headroom
            for (int i = 0; i < audioData.Length; i++)
                audioData[i] *= 0.95f;

            // Create CONSERVATIVE preset curve - much smaller adjustments
            var enhancedCurve = CreateConservativePresetCurve(preset.FrequencyResponse);
            var baseSpectrum = AnalyzeAudioFile(baseSampleFile);
            var optimizedQFactors = CalculateEnhancedPresetQFactors(enhancedCurve, baseSpectrum);

            // Apply MINIMAL gain reduction based on EQ boosts only
            float totalBoosts = enhancedCurve.Where(x => x > 0).Sum();
            float protectiveGain = Math.Max(0.7f, 1.0f - (totalBoosts * 0.03f)); // 3% reduction per dB boost

            for (int i = 0; i < audioData.Length; i++)
                audioData[i] *= protectiveGain;

            Log.Info($"Applied protective gain: {20 * Math.Log10(protectiveGain):F1}dB (total boosts: {totalBoosts:F1}dB)");

            // Setup EQ (always applied)
            var presetEQ = new Equalizer30BandEffect(sampleRate);
            for (int i = 0; i < FrequencyBands.Length; i++)
            {
                presetEQ.SetBandGain(i, FrequencyBands[i], optimizedQFactors[i], enhancedCurve[i]);
            }

            // Setup optional dynamics processing
            CompressorEffect? enhancedCompressor = null;

            if (!eqOnlyMode)
            {
                // ONLY compressor, NO DynamicAmp to preserve level
                enhancedCompressor = new CompressorEffect(
                    CompressorEffect.DbToLinear(-15f),
                    1.8f,
                    50f,
                    200f,
                    2.0f,
                    sampleRate
                );
            }

            // Initialize effects with audio configuration
            var audioConfig = new Ownaudio.Core.AudioConfig
            {
                SampleRate = sampleRate,
                Channels = channels,
                BufferSize = 512
            };

            presetEQ.Initialize(audioConfig);
            if (enhancedCompressor != null)
            {
                enhancedCompressor.Initialize(audioConfig);
            }

            // Process audio with effects chain
            int framesPerChunk = 512;
            int samplesPerChunk = framesPerChunk * channels;
            var processedData = new List<float>();
            int totalSamples = audioData.Length;
            float maxLevel = 0f;

            Log.Info($"Applying {(eqOnlyMode ? "EQ-only" : "full")} {preset.Name} effects to base sample...");

            for (int offset = 0; offset < totalSamples; offset += samplesPerChunk)
            {
                int samplesToProcess = Math.Min(samplesPerChunk, totalSamples - offset);
                int framesToProcess = samplesToProcess / channels;

                var chunk = new float[samplesToProcess];
                Array.Copy(audioData, offset, chunk, 0, samplesToProcess);

                // Always apply EQ - NOTE: frameCount, not sampleCount!
                presetEQ.Process(chunk.AsSpan(), framesToProcess);

                // Conditionally apply ONLY compression (NO DynamicAmp)
                if (!eqOnlyMode && enhancedCompressor != null)
                {
                    enhancedCompressor.Process(chunk.AsSpan(), framesToProcess);
                }

                // Monitor levels with gentler limiting
                for (int i = 0; i < chunk.Length; i++)
                {
                    float sample = chunk[i];
                    maxLevel = Math.Max(maxLevel, Math.Abs(sample));

                    // Very gentle soft limiting to preserve dynamics
                    if (Math.Abs(sample) > 0.95f)
                    {
                        float sign = Math.Sign(sample);
                        float limited = sign * (0.95f + 0.05f * MathF.Tanh((Math.Abs(sample) - 0.95f) * 4f));
                        chunk[i] = limited;
                    }
                }

                processedData.AddRange(chunk);

                if ((offset / samplesPerChunk) % 50 == 0)
                {
                    float progress = (float)(offset + samplesToProcess) / totalSamples * 100f;
                    Log.Info($"\rProcessing base sample: {progress:F1}%");
                }
            }

            Log.Info($"\nBase sample processed. Max level: {20 * Math.Log10(maxLevel):F1}dB");

            // AUTOMATIC LEVEL MATCHING - preserve original RMS level
            float processedRMS = CalculateRMS(processedData.ToArray());
            float levelCompensation = originalRMS / Math.Max(processedRMS, 1e-10f);

            // Apply compensation gain to match original level
            for (int i = 0; i < processedData.Count; i++)
            {
                processedData[i] *= levelCompensation;
            }

            float finalMaxLevel = processedData.Max(Math.Abs);
            Log.Info($"Level compensation applied: {20 * Math.Log10(levelCompensation):F1}dB");
            Log.Info($"Final max level: {20 * Math.Log10(finalMaxLevel):F1}dB");

            // Write processed base sample
            OwnaudioNET.Recording.WaveFile.Create(processedBaseSample, processedData.ToArray(), sampleRate, channels, 24);

            Log.Info($"Enhanced base sample created: {processedBaseSample}");
        }

        /// <summary>
        /// Loads the embedded basesample audio from resources.
        /// </summary>
        private bool LoadBaseSample(string path)
        {
            bool isLoadSample = false;

            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                string resourceName = "basesample.bin";

                foreach (var name in assembly.GetManifestResourceNames())
                {
                    if (name.EndsWith(resourceName))
                    {
                        resourceName = name;
                        break;
                    }
                }

                using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
                using var memoryStream = new MemoryStream();
                stream.CopyTo(memoryStream);

                OwnaudioNET.Recording.WaveFile.Create(path, memoryStream.ToArray(), 48000, 2, 24);

                isLoadSample = true;
            }
            catch
            {
                isLoadSample = false;
                throw new Exception("Load error target audio data!");
            }

            return isLoadSample;
        }

        /// <summary>
        /// Creates CONSERVATIVE preset curve to avoid overdriving the matchering algorithm
        /// </summary>
        /// <param name="originalCurve">Original preset frequency response curve</param>
        /// <returns>Conservative curve suitable for matchering target</returns>
        private float[] CreateConservativePresetCurve(float[] originalCurve)
        {
            var conservativeCurve = new float[originalCurve.Length];

            for (int i = 0; i < originalCurve.Length; i++)
            {
                float freq = FrequencyBands[i];
                float originalGain = originalCurve[i];

                // Tuned scaling factors - narrower Q allows for more gain without mud
                float conservativeFactor = freq switch
                {
                    <= 63f => 0.75f,    // Bass needs to be solid (was 0.6)
                    <= 250f => 0.8f,    // Low-mids (was 0.7)
                    <= 1000f => 0.85f,  // Mids (was 0.8)
                    <= 4000f => 0.85f,  // Presence (was 0.8)
                    <= 8000f => 0.8f,   // Brilliance (was 0.7)
                    _ => 0.75f          // Air (was 0.6)
                };

                float conservativeGain = originalGain * conservativeFactor;

                // Tighter limits but slightly relaxed for bass/air
                float maxConservativeBoost = freq switch
                {
                    < 100f => 3f,       // Allow reasonable bass boost (was 2f)
                    < 500f => 3f,       // (was 2.5f)
                    < 2000f => 3.5f,    // (was 3f)
                    < 5000f => 3.5f,    // (was 3f)
                    < 10000f => 3f,     // (was 2.5f)
                    _ => 3f             // (was 2f)
                };

                conservativeCurve[i] = Math.Max(-4f, Math.Min(maxConservativeBoost, conservativeGain));
            }

            Log.Info("Conservative preset curve created for matchering:");
            var bandNames = new[] {
                "20Hz", "25Hz", "31Hz", "40Hz", "50Hz", "63Hz", "80Hz", "100Hz", "125Hz", "160Hz",
                "200Hz", "250Hz", "315Hz", "400Hz", "500Hz", "630Hz", "800Hz", "1kHz", "1.25kHz", "1.6kHz",
                "2kHz", "2.5kHz", "3.15kHz", "4kHz", "5kHz", "6.3kHz", "8kHz", "10kHz", "12.5kHz", "16kHz"
            };

            for (int i = 0; i < conservativeCurve.Length; i++)
            {
                if (Math.Abs(conservativeCurve[i]) > 0.5f)
                {
                    Log.Info($"{bandNames[i]}: {conservativeCurve[i]:+0.1;-0.1}dB (was {originalCurve[i]:+0.1;-0.1}dB)");
                }
            }

            return conservativeCurve;
        }

        /// <summary>
        /// Calculates optimized Q factors for enhanced preset processing
        /// </summary>
        /// <param name="enhancedCurve">Enhanced frequency response curve</param>
        /// <param name="baseSpectrum">Base sample spectrum analysis</param>
        /// <returns>Array of optimized Q factors</returns>
        private float[] CalculateEnhancedPresetQFactors(float[] enhancedCurve, AudioSpectrum baseSpectrum)
        {
            var qFactors = new float[FrequencyBands.Length];

            for (int i = 0; i < FrequencyBands.Length; i++)
            {
                float freq = FrequencyBands[i];
                float gain = Math.Abs(enhancedCurve[i]);

                // Base Q for enhanced processing - UPDATED for 30-band standard
                // We use slightly wider values than surgical matching (avg ~3.5) for musicality
                float baseQ = freq switch
                {
                    <= 63f => 2.5f,     // Wide for bass foundation (was 0.5)
                    <= 250f => 3.0f,    // Moderate for low-mids (was 0.6)
                    <= 1000f => 3.8f,   // Standard 1/3 octave spacing (was 0.8)
                    <= 4000f => 4.0f,   // Precise presence (was 0.9)
                    <= 10000f => 3.8f,  // Moderate brilliance (was 0.8)
                    _ => 3.0f           // Wider air (was 0.7)
                };

                // Gain-based adjustment
                float gainAdjustment = gain switch
                {
                    <= 1f => 1.0f,
                    <= 3f => 1.05f,
                    <= 5f => 1.1f,
                    _ => 1.2f
                };

                qFactors[i] = Math.Max(2.5f, Math.Min(5.0f, baseQ * gainAdjustment));
            }

            return qFactors;
        }

        /// <summary>
        /// Prints comprehensive results from enhanced preset processing
        /// </summary>
        /// <param name="sourceFile">Original source file</param>
        /// <param name="baseSampleFile">Original base sample file</param>
        /// <param name="processedBaseSample">Processed base sample file</param>
        /// <param name="outputFile">Final output file</param>
        /// <param name="system">Applied playback system</param>
        private void PrintEnhancedPresetResults(string sourceFile, string baseSampleFile,
            string processedBaseSample, string outputFile, PlaybackSystem system)
        {
            Console.WriteLine("\n=== ENHANCED PRESET PROCESSING RESULTS ===");
            Console.WriteLine($"Applied System: {SystemPresets[system].Name}");
            Console.WriteLine($"Source: {Path.GetFileName(sourceFile)}");
            Console.WriteLine($"Base Sample: {Path.GetFileName(baseSampleFile)}");
            Console.WriteLine($"Output: {Path.GetFileName(outputFile)}");

            try
            {
                var originalBaseSpectrum = AnalyzeAudioFile(baseSampleFile);
                var processedBaseSpectrum = AnalyzeAudioFile(processedBaseSample);
                var finalSpectrum = AnalyzeAudioFile(outputFile);

                Console.WriteLine("\nSpectrum Analysis:");
                Console.WriteLine($"Original Base - RMS: {originalBaseSpectrum.RMSLevel:F3}, Loudness: {originalBaseSpectrum.Loudness:F1}dBFS");
                Console.WriteLine($"Enhanced Base - RMS: {processedBaseSpectrum.RMSLevel:F3}, Loudness: {processedBaseSpectrum.Loudness:F1}dBFS");
                Console.WriteLine($"Final Output - RMS: {finalSpectrum.RMSLevel:F3}, Loudness: {finalSpectrum.Loudness:F1}dBFS");

                float baseEnhancement = processedBaseSpectrum.Loudness - originalBaseSpectrum.Loudness;
                Console.WriteLine($"Base Sample Enhancement: {baseEnhancement:+0.1;-0.1}dB");

                Console.WriteLine("\n=== PROCESSING CHAIN SUMMARY ===");
                Console.WriteLine("Step 1: Enhanced preset effects applied to base sample");
                Console.WriteLine("Step 2: EQ matching applied from source to enhanced base");
                Console.WriteLine("Result: Source audio with enhanced preset characteristics");
            }
            catch (Exception ex)
            {
                Log.Error($"Analysis error: {ex.Message}");
            }
        }

        /// <summary>
        /// Batch processing for multiple sources with the same enhanced preset
        /// </summary>
        /// <param name="sourceFiles">Array of source files to process</param>
        /// <param name="baseSampleFile">Base reference sample</param>
        /// <param name="outputDirectory">Output directory for processed files</param>
        /// <param name="system">Playback system preset</param>
        /// <param name="fileNameSuffix">Suffix for output filenames</param>
        public void BatchProcessWithEnhancedPreset(string[] sourceFiles, string baseSampleFile,
            string outputDirectory, PlaybackSystem system, string? fileNameSuffix = null)
        {
            if (!Directory.Exists(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            string suffix = fileNameSuffix ?? $"_{system.ToString().ToLower()}";
            string tempDirectory = Path.Combine(Path.GetTempPath(), $"enhanced_preset_{DateTime.Now.Ticks}");
            Directory.CreateDirectory(tempDirectory);

            try
            {
                Log.Info($"=== BATCH ENHANCED PRESET PROCESSING ===");
                Log.Info($"System: {SystemPresets[system].Name}");
                Log.Info($"Processing {sourceFiles.Length} files...");

                for (int i = 0; i < sourceFiles.Length; i++)
                {
                    string sourceFile = sourceFiles[i];
                    string fileName = Path.GetFileNameWithoutExtension(sourceFile);
                    string outputFile = Path.Combine(outputDirectory, $"{fileName}{suffix}.wav");

                    Log.Info($"\n[{i + 1}/{sourceFiles.Length}] Processing: {Path.GetFileName(sourceFile)}");

                    try
                    {
                        ProcessWithEnhancedPreset(sourceFile, outputFile, system, tempDirectory);
                        Log.Info($"Completed: {Path.GetFileName(outputFile)}");
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Error processing {Path.GetFileName(sourceFile)}: {ex.Message}");
                    }
                }

                Log.Info($"\nBatch processing completed. Files saved to: {outputDirectory}");
            }
            finally
            {
                // Cleanup temp directory
                try
                {
                    if (Directory.Exists(tempDirectory))
                        Directory.Delete(tempDirectory, true);
                }
                catch (Exception ex)
                {
                    Log.Warning($"Could not delete temp directory: {ex.Message}");
                }
            }
        }
    }
}
