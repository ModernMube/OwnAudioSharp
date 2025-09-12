using Ownaudio.Fx;
using Ownaudio.Sources;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ownaudio.Utilities.Matchering
{
    /// <summary>
    /// Audio playback system presets based on audio engineering standards
    /// </summary>
    public enum PlaybackSystem
    {
        /// <summary>
        /// Concert sound reinforcement system for large venues
        /// </summary>
        ConcertPA,

        /// <summary>
        /// Nightclub or DJ sound system optimized for dance music
        /// </summary>
        ClubPA,

        /// <summary>
        /// High-fidelity home speakers for critical listening
        /// </summary>
        HiFiSpeakers,

        /// <summary>
        /// Near-field studio monitors for professional mixing
        /// </summary>
        StudioMonitors,

        /// <summary>
        /// Over-ear headphones for personal listening
        /// </summary>
        Headphones,

        /// <summary>
        /// In-ear monitors or earbuds for portable listening
        /// </summary>
        Earbuds,

        /// <summary>
        /// Automotive audio system compensated for road noise
        /// </summary>
        CarStereo,

        /// <summary>
        /// Television or soundbar speakers optimized for dialogue
        /// </summary>
        Television,

        /// <summary>
        /// Radio transmission standards for FM/AM broadcast
        /// </summary>
        RadioBroadcast,

        /// <summary>
        /// Smartphone or tablet built-in speakers
        /// </summary>
        Smartphone
    }

    /// <summary>
    /// Preset configuration for specific playback systems
    /// </summary>
    public class PlaybackPreset
    {
        /// <summary>
        /// Human-readable name of the preset
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Detailed description of the preset's intended use
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// 30-band EQ frequency response curve in dB (20Hz to 16kHz)
        /// </summary>
        public float[] FrequencyResponse { get; set; } = new float[30];

        /// <summary>
        /// Target loudness level in LUFS for optimal playback
        /// </summary>
        public float TargetLoudness { get; set; }

        /// <summary>
        /// Recommended dynamic range in dB for the playback system
        /// </summary>
        public float DynamicRange { get; set; }

        /// <summary>
        /// Compression settings optimized for the playback system
        /// </summary>
        public CompressionSettings Compression { get; set; } = new();

        /// <summary>
        /// Dynamic amplifier settings for automatic level control
        /// </summary>
        public DynamicAmpSettings DynamicAmp { get; set; } = new();
    }

    partial class AudioAnalyzer
    {
        /// <summary>
        /// Predefined preset configurations for different playback systems
        /// </summary>
        private static readonly Dictionary<PlaybackSystem, PlaybackPreset> SystemPresets =
            new Dictionary<PlaybackSystem, PlaybackPreset>
            {
                [PlaybackSystem.ConcertPA] = new PlaybackPreset
                {
                    Name = "Concert PA System",
                    Description = "Large venue sound reinforcement with extended dynamics",
                    FrequencyResponse = new float[]
                    {
                        // 20-16kHz: Concert PA characteristic curve
                        -2f, -1f, 0f, +1f, +2f, +2f, +1f, +1f, 0f, 0f,     // 20-160Hz: Controlled low end
                        +1f, +1f, +1f, 0f, 0f, -1f, -1f, 0f, +1f, +2f,     // 200-1.6kHz: Clear midrange
                        +2f, +1f, 0f, +1f, +2f, +1f, 0f, -1f, -1f, -2f     // 2-16kHz: Controlled highs
                    },
                    TargetLoudness = -16f,  // Higher dynamic range for live music
                    DynamicRange = 18f,
                    Compression = new CompressionSettings
                    {
                        Threshold = -18f,
                        Ratio = 2.5f,
                        AttackTime = 10f,
                        ReleaseTime = 100f,
                        MakeupGain = 2f
                    },
                    DynamicAmp = new DynamicAmpSettings
                    {
                        TargetLevel = -16f,
                        AttackTime = 0.1f,
                        ReleaseTime = 0.5f,
                        MaxGain = 6f
                    }
                },

                [PlaybackSystem.ClubPA] = new PlaybackPreset
                {
                    Name = "Club/DJ Sound System",
                    Description = "Dance music optimized with enhanced bass and presence",
                    FrequencyResponse = new float[]
                    {
                        // Club sound: Enhanced bass and presence
                        +3f, +4f, +4f, +3f, +2f, +2f, +1f, +1f, 0f, 0f,    // 20-160Hz: Strong bass
                        0f, 0f, +1f, +1f, +1f, +1f, +2f, +2f, +3f, +3f,     // 200-1.6kHz: Forward mids
                        +2f, +1f, +2f, +3f, +2f, +1f, +1f, 0f, 0f, -1f     // 2-16kHz: Dance presence
                    },
                    TargetLoudness = -11f,  // Loud for club environment
                    DynamicRange = 8f,      // Compressed for dancefloor
                    Compression = new CompressionSettings
                    {
                        Threshold = -15f,
                        Ratio = 4f,
                        AttackTime = 3f,
                        ReleaseTime = 50f,
                        MakeupGain = 3f
                    },
                    DynamicAmp = new DynamicAmpSettings
                    {
                        TargetLevel = -11f,
                        AttackTime = 0.05f,
                        ReleaseTime = 0.2f,
                        MaxGain = 4f
                    }
                },

                [PlaybackSystem.HiFiSpeakers] = new PlaybackPreset
                {
                    Name = "Hi-Fi Home Speakers",
                    Description = "Neutral response for critical listening in treated rooms",
                    FrequencyResponse = new float[]
                    {
                        // Hi-Fi: Neutral with slight warmth
                        0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f,           // 20-160Hz: Flat bass
                        0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f,            // 200-1.6kHz: Neutral mids
                        0f, 0f, 0f, 0f, +0.5f, +1f, +1f, +0.5f, 0f, 0f    // 2-16kHz: Slight air boost
                    },
                    TargetLoudness = -18f,  // Audiophile dynamics
                    DynamicRange = 20f,
                    Compression = new CompressionSettings
                    {
                        Threshold = -25f,
                        Ratio = 1.5f,
                        AttackTime = 20f,
                        ReleaseTime = 200f,
                        MakeupGain = 1f
                    },
                    DynamicAmp = new DynamicAmpSettings
                    {
                        TargetLevel = -18f,
                        AttackTime = 0.3f,
                        ReleaseTime = 2f,
                        MaxGain = 3f
                    }
                },

                [PlaybackSystem.StudioMonitors] = new PlaybackPreset
                {
                    Name = "Studio Near-Field Monitors",
                    Description = "Reference standard for professional mixing",
                    FrequencyResponse = new float[]
                    {
                        // Studio monitors: True reference
                        0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f,           // 20-160Hz: Flat
                        0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f,            // 200-1.6kHz: Flat
                        0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f             // 2-16kHz: Flat
                    },
                    TargetLoudness = -20f,  // Reference level
                    DynamicRange = 22f,
                    Compression = new CompressionSettings
                    {
                        Threshold = -30f,
                        Ratio = 1.2f,
                        AttackTime = 50f,
                        ReleaseTime = 300f,
                        MakeupGain = 0f
                    },
                    DynamicAmp = new DynamicAmpSettings
                    {
                        TargetLevel = -20f,
                        AttackTime = 0.5f,
                        ReleaseTime = 3f,
                        MaxGain = 2f
                    }
                },

                [PlaybackSystem.Headphones] = new PlaybackPreset
                {
                    Name = "Over-Ear Headphones",
                    Description = "Compensated for typical headphone frequency response",
                    FrequencyResponse = new float[]
                    {
                        // Headphone compensation curve
                        +1f, +1f, +1f, +2f, +2f, +1f, +1f, 0f, 0f, -1f,    // 20-160Hz: Sub-bass boost
                        -1f, -1f, 0f, +1f, +2f, +2f, +1f, 0f, -1f, -2f,     // 200-1.6kHz: Presence dip
                        -1f, +1f, +2f, +1f, 0f, +1f, +2f, +3f, +2f, +1f     // 2-16kHz: Headphone curve
                    },
                    TargetLoudness = -16f,  // Personal listening level
                    DynamicRange = 16f,
                    Compression = new CompressionSettings
                    {
                        Threshold = -20f,
                        Ratio = 2f,
                        AttackTime = 5f,
                        ReleaseTime = 80f,
                        MakeupGain = 2f
                    },
                    DynamicAmp = new DynamicAmpSettings
                    {
                        TargetLevel = -16f,
                        AttackTime = 0.2f,
                        ReleaseTime = 1f,
                        MaxGain = 4f
                    }
                },

                [PlaybackSystem.Earbuds] = new PlaybackPreset
                {
                    Name = "In-Ear Monitors/Earbuds",
                    Description = "Enhanced for in-ear acoustics and isolation",
                    FrequencyResponse = new float[]
                    {
                        // IEM curve with bass compensation
                        +2f, +3f, +3f, +2f, +1f, 0f, 0f, 0f, 0f, 0f,       // 20-160Hz: Bass boost for seal
                        +1f, +2f, +2f, +2f, +2f, +1f, 0f, +1f, +2f, +3f,    // 200-1.6kHz: Clear vocals
                        +3f, +2f, +1f, +2f, +3f, +2f, +1f, 0f, -1f, -2f     // 2-16kHz: Controlled highs
                    },
                    TargetLoudness = -14f,  // Mobile listening level
                    DynamicRange = 12f,
                    Compression = new CompressionSettings
                    {
                        Threshold = -18f,
                        Ratio = 3f,
                        AttackTime = 2f,
                        ReleaseTime = 40f,
                        MakeupGain = 3f
                    },
                    DynamicAmp = new DynamicAmpSettings
                    {
                        TargetLevel = -14f,
                        AttackTime = 0.1f,
                        ReleaseTime = 0.5f,
                        MaxGain = 5f
                    }
                },

                [PlaybackSystem.CarStereo] = new PlaybackPreset
                {
                    Name = "Car Stereo System",
                    Description = "Optimized for road noise and cabin acoustics",
                    FrequencyResponse = new float[]
                    {
                        // Car audio curve: Road noise compensation
                        +2f, +2f, +1f, +1f, 0f, 0f, 0f, +1f, +2f, +3f,      // 20-160Hz: Engine compensation
                        +3f, +2f, +1f, +2f, +3f, +4f, +4f, +3f, +2f, +1f,    // 200-1.6kHz: Vocal clarity
                        +2f, +3f, +4f, +3f, +2f, +1f, +2f, +3f, +2f, +1f     // 2-16kHz: Wind noise comp
                    },
                    TargetLoudness = -12f,  // Loud environment
                    DynamicRange = 10f,
                    Compression = new CompressionSettings
                    {
                        Threshold = -16f,
                        Ratio = 3.5f,
                        AttackTime = 3f,
                        ReleaseTime = 60f,
                        MakeupGain = 4f
                    },
                    DynamicAmp = new DynamicAmpSettings
                    {
                        TargetLevel = -12f,
                        AttackTime = 0.05f,
                        ReleaseTime = 0.3f,
                        MaxGain = 6f
                    }
                },

                [PlaybackSystem.Television] = new PlaybackPreset
                {
                    Name = "Television/Soundbar",
                    Description = "Dialogue clarity and late-night listening friendly",
                    FrequencyResponse = new float[]
                    {
                        // TV curve: Dialogue focused
                        -1f, -1f, 0f, +1f, +1f, +1f, +2f, +3f, +3f, +2f,    // 20-160Hz: Controlled bass
                        +2f, +3f, +4f, +4f, +3f, +2f, +2f, +3f, +2f, +1f,    // 200-1.6kHz: Speech clarity
                        +1f, +1f, +2f, +1f, 0f, 0f, +1f, +1f, 0f, -1f       // 2-16kHz: Soft highs
                    },
                    TargetLoudness = -15f,  // Living room level
                    DynamicRange = 12f,
                    Compression = new CompressionSettings
                    {
                        Threshold = -18f,
                        Ratio = 4f,
                        AttackTime = 1f,
                        ReleaseTime = 30f,
                        MakeupGain = 3f
                    },
                    DynamicAmp = new DynamicAmpSettings
                    {
                        TargetLevel = -15f,
                        AttackTime = 0.1f,
                        ReleaseTime = 0.8f,
                        MaxGain = 4f
                    }
                },

                [PlaybackSystem.RadioBroadcast] = new PlaybackPreset
                {
                    Name = "Radio Broadcast",
                    Description = "FM/AM radio transmission standards",
                    FrequencyResponse = new float[]
                    {
                        // Radio curve: Limited bandwidth, high compression
                        0f, +1f, +2f, +2f, +2f, +2f, +2f, +2f, +2f, +1f,     // 20-160Hz: Controlled lows
                        +2f, +3f, +4f, +4f, +4f, +3f, +3f, +4f, +3f, +2f,     // 200-1.6kHz: Forward mids
                        +2f, +2f, +1f, +1f, 0f, 0f, +1f, 0f, -2f, -4f        // 2-16kHz: HF rolloff
                    },
                    TargetLoudness = -9f,   // Broadcast loudness
                    DynamicRange = 6f,
                    Compression = new CompressionSettings
                    {
                        Threshold = -12f,
                        Ratio = 6f,
                        AttackTime = 0.5f,
                        ReleaseTime = 20f,
                        MakeupGain = 6f
                    },
                    DynamicAmp = new DynamicAmpSettings
                    {
                        TargetLevel = -9f,
                        AttackTime = 0.02f,
                        ReleaseTime = 0.1f,
                        MaxGain = 8f
                    }
                },

                [PlaybackSystem.Smartphone] = new PlaybackPreset
                {
                    Name = "Smartphone/Tablet Speaker",
                    Description = "Small speaker compensation with midrange focus",
                    FrequencyResponse = new float[]
                    {
                        // Phone speaker curve: Limited bass, enhanced mids
                        -6f, -4f, -2f, -1f, 0f, +1f, +2f, +3f, +4f, +4f,     // 20-160Hz: Bass limitation
                        +5f, +6f, +6f, +5f, +4f, +4f, +5f, +6f, +5f, +4f,     // 200-1.6kHz: Strong mids
                        +3f, +3f, +2f, +2f, +1f, +1f, +2f, +1f, 0f, -2f       // 2-16kHz: Controlled highs
                    },
                    TargetLoudness = -10f,  // Mobile environment
                    DynamicRange = 8f,
                    Compression = new CompressionSettings
                    {
                        Threshold = -14f,
                        Ratio = 5f,
                        AttackTime = 1f,
                        ReleaseTime = 25f,
                        MakeupGain = 5f
                    },
                    DynamicAmp = new DynamicAmpSettings
                    {
                        TargetLevel = -10f,
                        AttackTime = 0.02f,
                        ReleaseTime = 0.2f,
                        MaxGain = 8f
                    }
                }
            };

        /// <summary>
        /// Processes audio file using playback system preset
        /// </summary>
        /// <param name="sourceFile">Input audio file path</param>
        /// <param name="outputFile">Output audio file path</param>
        /// <param name="system">Target playback system</param>
        /// <exception cref="InvalidOperationException">Thrown when audio file cannot be loaded</exception>
        /// <exception cref="ArgumentException">Thrown when file paths are invalid</exception>
        public void ProcessWithPreset(string sourceFile, string outputFile, PlaybackSystem system)
        {
            Console.WriteLine($"Processing audio for {SystemPresets[system].Name}...");
            Console.WriteLine($"Description: {SystemPresets[system].Description}");

            var preset = SystemPresets[system];
            var sourceSpectrum = AnalyzeAudioFile(sourceFile);

            // Apply preset-based processing
            ApplyPresetProcessing(sourceFile, outputFile, preset, sourceSpectrum);

            Console.WriteLine($"Audio optimized for {preset.Name}");
            PrintPresetResults(sourceSpectrum, preset);
        }

        /// <summary>
        /// Gets available playback system presets
        /// </summary>
        /// <returns>Dictionary of available presets with their configurations</returns>
        public static Dictionary<PlaybackSystem, PlaybackPreset> GetAvailablePresets()
        {
            return new Dictionary<PlaybackSystem, PlaybackPreset>(SystemPresets);
        }

        /// <summary>
        /// Improved preset processing with distortion protection and Q-factor optimization
        /// </summary>
        /// <param name="inputFile">Input audio file path</param>
        /// <param name="outputFile">Output audio file path</param>
        /// <param name="preset">Playback preset configuration</param>
        /// <param name="sourceSpectrum">Source audio spectrum analysis</param>
        /// <exception cref="InvalidOperationException">Thrown when audio processing fails</exception>
        /// <exception cref="ArgumentNullException">Thrown when preset is null</exception>
        private void ApplyPresetProcessing(string inputFile, string outputFile,
            PlaybackPreset preset, AudioSpectrum sourceSpectrum)
        {
            try
            {
                using var source = new Source();
                source.LoadAsync(inputFile).Wait();

                if (!source.IsLoaded)
                    throw new InvalidOperationException($"Cannot load audio file: {inputFile}");

                var audioData = source.GetFloatAudioData(TimeSpan.Zero);
                var channels = source.CurrentDecoder?.StreamInfo.Channels ?? 2;
                var sampleRate = source.CurrentDecoder?.StreamInfo.SampleRate ?? 44100;

                Console.WriteLine($"Applying preset with distortion protection: {preset.Name}");

                // Apply intelligent scaling to preset curve to prevent distortion
                var adjustedCurve = ApplyPresetIntelligentScaling(preset.FrequencyResponse, sourceSpectrum);

                // Calculate optimal Q factors for the preset curve
                var optimizedQFactors = CalculatePresetQFactors(adjustedCurve, sourceSpectrum);

                // Create 30-band EQ with optimized Q factors
                var presetEQ = new Equalizer30Band(sampleRate);

                // Set each band with optimized parameters
                for (int i = 0; i < FrequencyBands.Length; i++)
                {
                    presetEQ.SetBandGain(i, FrequencyBands[i], optimizedQFactors[i], adjustedCurve[i]);
                }

                // More conservative compressor settings for preset processing
                var compressor = new Compressor(
                    Compressor.DbToLinear(Math.Max(-25f, preset.Compression.Threshold)), // More conservative threshold
                    Math.Min(3f, preset.Compression.Ratio), // Limit ratio to prevent over-compression
                    preset.Compression.AttackTime,
                    preset.Compression.ReleaseTime,
                    Math.Min(3f, preset.Compression.MakeupGain), // Limit makeup gain
                    sampleRate
                );

                // Conservative dynamic amp settings
                var dynamicAmp = new DynamicAmp(
                    preset.DynamicAmp.TargetLevel + 2f, // Add headroom
                    Math.Max(0.1f, preset.DynamicAmp.AttackTime), // Prevent too fast attack
                    Math.Max(0.5f, preset.DynamicAmp.ReleaseTime), // Prevent too fast release
                    0.003f,
                    Math.Min(4f, preset.DynamicAmp.MaxGain), // Limit max gain
                    sampleRate,
                    0.25f
                );

                // Process audio in chunks with improved monitoring
                int chunkSize = 512 * channels;
                var processedData = new List<float>();
                int totalSamples = audioData.Length;
                float maxLevel = 0f;
                int clippedSamples = 0;

                for (int offset = 0; offset < totalSamples; offset += chunkSize)
                {
                    int samplesToProcess = Math.Min(chunkSize, totalSamples - offset);
                    var chunk = new float[samplesToProcess];
                    Array.Copy(audioData, offset, chunk, 0, samplesToProcess);

                    // Apply processing chain
                    presetEQ.Process(chunk.AsSpan());
                    compressor.Process(chunk.AsSpan());
                    dynamicAmp.Process(chunk.AsSpan());

                    // Monitor levels and apply soft limiting
                    for (int i = 0; i < chunk.Length; i++)
                    {
                        float sample = chunk[i];
                        maxLevel = Math.Max(maxLevel, Math.Abs(sample));

                        // Soft limiting at -0.5dB to prevent harsh clipping
                        if (Math.Abs(sample) > 0.94f)
                        {
                            chunk[i] = sample > 0 ? 0.94f : -0.94f;
                            clippedSamples++;
                        }
                    }

                    processedData.AddRange(chunk);

                    float progress = (float)(offset + samplesToProcess) / totalSamples * 100f;
                    Console.Write($"\rProcessing: {progress:F1}%");
                }

                Console.WriteLine($"\nMax level: {20 * Math.Log10(maxLevel):F1}dB");
                if (clippedSamples > 0)
                {
                    Console.WriteLine($"Warning: {clippedSamples} samples were soft-limited");
                }

                Console.WriteLine("Writing to file...");
                Ownaudio.Utilities.WaveFile.WriteFile(outputFile, processedData.ToArray(), sampleRate, channels, 24);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during preset processing: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Applies intelligent scaling to preset frequency response curves to prevent distortion
        /// </summary>
        /// <param name="presetCurve">Original preset frequency response curve</param>
        /// <param name="sourceSpectrum">Source audio spectrum for analysis</param>
        /// <returns>Scaled frequency response curve optimized for distortion-free processing</returns>
        private float[] ApplyPresetIntelligentScaling(float[] presetCurve, AudioSpectrum sourceSpectrum)
        {
            var adjustedCurve = new float[presetCurve.Length];

            // Calculate total boost in preset
            float totalBoost = presetCurve.Where(x => x > 0).Sum();

            // Apply global scaling based on total boost
            float globalScaling = totalBoost switch
            {
                > 30f => 0.4f,  // Very aggressive presets need heavy scaling
                > 20f => 0.5f,  // Aggressive presets need moderate scaling
                > 12f => 0.6f,  // Moderate presets need light scaling
                > 6f => 0.75f,  // Conservative presets need minimal scaling
                _ => 0.85f      // Minimal presets need almost no scaling
            };

            Console.WriteLine($"Preset scaling applied: {globalScaling:F2} (Total boost: {totalBoost:F1}dB)");

            for (int i = 0; i < presetCurve.Length; i++)
            {
                float freq = FrequencyBands[i];
                float originalGain = presetCurve[i];

                // Apply frequency-specific scaling
                float freqScaling = freq switch
                {
                    <= 50f => 0.6f,     // Conservative sub-bass
                    <= 200f => 0.7f,    // Moderate bass
                    <= 1000f => 0.8f,   // Good low-mid
                    <= 4000f => 0.65f,  // Conservative presence
                    <= 8000f => 0.75f,  // Moderate brilliance
                    _ => 0.7f           // Conservative air
                };

                // Combine scalings
                float scaledGain = originalGain * globalScaling * freqScaling;

                // Apply per-frequency limits
                float maxBoost = freq switch
                {
                    < 100f => 3f,
                    < 500f => 4f,
                    < 2000f => 4.5f,
                    < 5000f => 3.5f,
                    < 10000f => 4f,
                    _ => 3f
                };

                adjustedCurve[i] = Math.Max(-6f, Math.Min(maxBoost, scaledGain));
            }

            // Print adjustment info
            Console.WriteLine("Preset curve adjustments:");
            var bandNames = new[] {
        "20Hz", "25Hz", "31Hz", "40Hz", "50Hz", "63Hz", "80Hz", "100Hz", "125Hz", "160Hz",
        "200Hz", "250Hz", "315Hz", "400Hz", "500Hz", "630Hz", "800Hz", "1kHz", "1.25kHz", "1.6kHz",
        "2kHz", "2.5kHz", "3.15kHz", "4kHz", "5kHz", "6.3kHz", "8kHz", "10kHz", "12.5kHz", "16kHz"
    };

            for (int i = 0; i < adjustedCurve.Length; i++)
            {
                if (Math.Abs(adjustedCurve[i]) > 0.5f)
                {
                    Console.WriteLine($"{bandNames[i]}: {adjustedCurve[i]:+0.1;-0.1}dB (was {presetCurve[i]:+0.1;-0.1}dB)");
                }
            }

            return adjustedCurve;
        }

        /// <summary>
        /// Calculates optimal Q factors for preset curves based on frequency and gain characteristics
        /// </summary>
        /// <param name="adjustedCurve">Scaled frequency response curve</param>
        /// <param name="sourceSpectrum">Source audio spectrum analysis</param>
        /// <returns>Array of optimized Q factors for each frequency band</returns>
        private float[] CalculatePresetQFactors(float[] adjustedCurve, AudioSpectrum sourceSpectrum)
        {
            var qFactors = new float[FrequencyBands.Length];

            for (int i = 0; i < FrequencyBands.Length; i++)
            {
                float freq = FrequencyBands[i];
                float gain = Math.Abs(adjustedCurve[i]);

                // Base Q for presets (wider than EQ matching for musicality)
                float baseQ = freq switch
                {
                    <= 63f => 0.4f,     // Very wide for sub-bass
                    <= 250f => 0.5f,    // Wide for bass
                    <= 1000f => 0.7f,   // Moderate for low-mid
                    <= 4000f => 0.8f,   // Standard for mid/presence
                    <= 10000f => 0.7f,  // Moderate for brilliance
                    _ => 0.6f           // Wide for air
                };

                // Adjust Q based on gain amount (less aggressive than EQ matching)
                float gainAdjustment = gain switch
                {
                    <= 1f => 1.0f,
                    <= 2f => 1.05f,
                    <= 3f => 1.1f,
                    <= 4f => 1.2f,
                    _ => 1.3f
                };

                qFactors[i] = Math.Max(0.3f, Math.Min(2f, baseQ * gainAdjustment));
            }

            return qFactors;
        }

        /// <summary>
        /// Prints detailed preset application results to console
        /// </summary>
        /// <param name="source">Source audio spectrum analysis</param>
        /// <param name="preset">Applied preset configuration</param>
        private void PrintPresetResults(AudioSpectrum source, PlaybackPreset preset)
        {
            Console.WriteLine("\n=== PRESET APPLICATION RESULTS ===");
            Console.WriteLine($"Preset: {preset.Name}");
            Console.WriteLine($"Target Loudness: {preset.TargetLoudness:F1} LUFS");
            Console.WriteLine($"Target Dynamic Range: {preset.DynamicRange:F1} dB");
            Console.WriteLine($"Source - RMS: {source.RMSLevel:F3}, Loudness: {source.Loudness:F1} LUFS");

            Console.WriteLine("\nApplied EQ Curve:");
            var bandNames = new[] {
                "20Hz", "25Hz", "31Hz", "40Hz", "50Hz", "63Hz", "80Hz", "100Hz", "125Hz", "160Hz",
                "200Hz", "250Hz", "315Hz", "400Hz", "500Hz", "630Hz", "800Hz", "1kHz", "1.25kHz", "1.6kHz",
                "2kHz", "2.5kHz", "3.15kHz", "4kHz", "5kHz", "6.3kHz", "8kHz", "10kHz", "12.5kHz", "16kHz"
            };

            for (int i = 0; i < preset.FrequencyResponse.Length; i++)
            {
                if (Math.Abs(preset.FrequencyResponse[i]) > 0.1f)
                {
                    Console.WriteLine($"{bandNames[i]}: {preset.FrequencyResponse[i]:+0.1;-0.1} dB");
                }
            }
        }
    }
}
