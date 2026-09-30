using Ownaudio.Safe.Effects;
using OwnaudioNET.Sources;
using Logger;
using System;
using System.IO;
using System.Reflection;

namespace OwnaudioNET.Features.Matchering
{
    /// <summary>
    /// Preset based matching - we bake the preset curve into a neutral base sample
    /// and then match the source track to that.
    /// </summary>
    partial class AudioAnalyzer
    {
        /// <summary>
        /// Renders the source through a playback system preset. The preset is applied to
        /// the embedded base sample first, that becomes the matchering target.
        /// </summary>
        /// <param name="sourceFile"></param>
        /// <param name="outputFile"></param>
        /// <param name="system">Which playback system curve to bake in.</param>
        /// <param name="tempDirectory">Where the intermediate wavs go, temp path if null.</param>
        /// <param name="eqOnlyMode">Skip the compressor on the base sample.</param>
        public void ProcessWithEnhancedPreset(string sourceFile, string outputFile,
            PlaybackSystem system, string? tempDirectory = null, bool eqOnlyMode = false)
        {
            if (string.IsNullOrEmpty(tempDirectory))
                tempDirectory = Path.GetTempPath();

            long stamp = DateTime.Now.Ticks;
            string processedBaseSample = Path.Combine(tempDirectory, $"processed_base_{system}_{stamp}.wav");
            string baseSampleFile = Path.Combine(tempDirectory, $"base_sample_{system}_{stamp}.wav");

            _loadBaseSample(baseSampleFile);

            try
            {
                Log.Info($"=== ENHANCED PRESET PROCESSING: {_systemPresets[system].Name} ===");
                Log.Info($"Mode: {(eqOnlyMode ? "EQ Only" : "Full Effects Chain")}");

                _applyPresetToBase(baseSampleFile, processedBaseSample, system, eqOnlyMode);
                _matchToTarget(sourceFile, processedBaseSample, outputFile, _systemPresets[system]);

                Log.Info($"Enhanced preset processing completed: {outputFile}");
            }
            finally
            {
                try
                {
                    if (File.Exists(processedBaseSample)) File.Delete(processedBaseSample);
                    if (File.Exists(baseSampleFile)) File.Delete(baseSampleFile);
                }
                catch (Exception ex)
                {
                    Log.Warning($"Could not delete temporary file: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Bakes the preset EQ (and optionally its compressor) into the base sample,
        /// then matches the level back to where it started.
        /// </summary>
        private void _applyPresetToBase(string baseSampleFile, string processedBaseSample,
            PlaybackSystem system, bool eqOnlyMode = false)
        {
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

            _bakePresetIntoBase(audioData, sampleRate, channels, system, eqOnlyMode);

            OwnaudioNET.Recording.WaveFile.Create(processedBaseSample, audioData, sampleRate, channels, 24);

            Log.Info($"Enhanced base sample created: {processedBaseSample}");
        }

        /// <summary>
        /// Bakes the whole preset into the buffer in place - EQ, its own compressor unless
        /// eqOnlyMode, then the level pushed to the preset's target loudness. What comes out
        /// is what the preset is meant to sound like, so the matcher has something real to chase.
        /// </summary>
        private void _bakePresetIntoBase(float[] audioData, int sampleRate, int channels,
            PlaybackSystem system, bool eqOnlyMode)
        {
            var preset = _systemPresets[system];

            float[] wanted = new float[_freqBands.Length];
            for (int i = 0; i < wanted.Length; i++)
                wanted[i] = Math.Clamp(preset.FrequencyResponse[i], -MaxBandCorrectionDb, MaxBandCorrectionDb);

            float[] qFactors = _presetQFactors(wanted);
            float[] bandGains = _deconvolveToBandGains(wanted, qFactors, sampleRate);

            float maxBoost = 0f;
            foreach (float g in bandGains) if (g > maxBoost) maxBoost = g;

            if (maxBoost > 0f)
            {
                float preGain = MathF.Pow(10f, Math.Clamp(-(maxBoost + 2f), -12f, 0f) / 20f);
                for (int i = 0; i < audioData.Length; i++) audioData[i] *= preGain;

                Log.Info($"Headroom for the preset boosts: {20 * MathF.Log10(preGain):F1}dB (max band {maxBoost:F1}dB)");
            }

            using StandaloneEffect presetEQ = NativeMastering.Equalizer30(
                sampleRate, channels, _freqBands, qFactors, bandGains);

            OwnCompressorSettings presetCompressor = preset.Compressor;

            using StandaloneEffect? compressor = eqOnlyMode ? null : NativeMastering.OwnCompressor(
                sampleRate, channels, presetCompressor);

            Log.Info($"Applying {(eqOnlyMode ? "EQ-only" : "full")} {preset.Name} chain to base sample...");

            if (compressor is not null)
                Log.Info($"Preset OwnCompressor: {presetCompressor.ThresholdDb:F1}dB, {presetCompressor.Ratio:F1}:1, knee {presetCompressor.KneeDb:F0}dB, " +
                         $"{presetCompressor.AttackMs:F1}/{presetCompressor.ReleaseMs:F0}ms, range {presetCompressor.RangeDb:F0}dB, makeup {presetCompressor.MakeupDb:F1}dB, " +
                         $"{presetCompressor.ChannelMode}, key HPF {presetCompressor.SidechainHighPassHz:F0}Hz");

            StandaloneEffect[] chain = compressor is null ? new[] { presetEQ } : new[] { presetEQ, compressor };
            int totalSamples = (audioData.Length / channels) * channels;

            NativeMastering.Render(audioData, channels, chain);
            NativeMastering.CompensateLatency(audioData, totalSamples, channels, chain);

            _normalizeToPreset(audioData, totalSamples, channels, sampleRate, preset);
        }

        /// <summary>
        /// Pulls the baked sample up to the preset's integrated loudness and lets the rider's
        /// true-peak limiter hold the peaks at the preset's own ceiling - the way a loud master is
        /// actually made, and in the units the rider will chase later. Two rounds, since the
        /// limiter takes some of the loudness back on the loud presets. The peak to loudness ratio measured afterwards is
        /// the preset's, which is what drives the compressor settings downstream.
        /// </summary>
        private void _normalizeToPreset(float[] audioData, int totalSamples, int channels,
            int sampleRate, PlaybackPreset preset)
        {
            for (int pass = 0; pass < PresetLoudnessPasses; pass++)
            {
                float measured = MeasureLoudness(audioData, sampleRate, channels).IntegratedLufs;
                float gain = MathF.Pow(10f, (preset.TargetLoudness - measured) / 20f);

                for (int i = 0; i < audioData.Length; i++) audioData[i] *= gain;

                Log.Info($"Level pushed by {20 * MathF.Log10(gain):+0.0;-0.0}dB toward {preset.TargetLoudness:F1} LUFS");

                using StandaloneEffect limiter = NativeMastering.TruePeakLimiter(sampleRate, channels, preset.Leveler.CeilingDbtp);
                var limiterChain = new[] { limiter };

                NativeMastering.Render(audioData, channels, limiterChain);
                NativeMastering.CompensateLatency(audioData, totalSamples, channels, limiterChain);
            }

            LoudnessInfo achieved = MeasureLoudness(audioData, sampleRate, channels);
            Log.Info($"Baked base sample: {achieved.IntegratedLufs:F1} LUFS, {achieved.TruePeakDbtp:F1} dBTP, " +
                     $"PLR {achieved.PeakToLoudnessDb:F1}dB (preset asks {preset.TargetLoudness:F1} LUFS, {preset.DynamicRange:F1}dB)");
        }

        private const int PresetLoudnessPasses = 2;

        /// <summary>
        /// Dumps the embedded basesample blob out to a wav we can open.
        /// </summary>
        private void _loadBaseSample(string path)
        {
            try
            {
                using Stream stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("OwnaudioNET.basesample.bin")!;
                using var memoryStream = new MemoryStream();
                stream.CopyTo(memoryStream);

                OwnaudioNET.Recording.WaveFile.Create(path, memoryStream.ToArray(), 48000, 2, 24);
            }
            catch (Exception ex)
            {
                Log.Error($"[Matchering] Embedded base sample could not be written to '{path}'", ex);
                throw new Exception("Load error target audio data!", ex);
            }
        }

        /// <summary>
        /// Q factors for the preset EQ. Wider down low, tighter around the presence
        /// region, plus a nudge for the bigger gains.
        /// </summary>
        private float[] _presetQFactors(float[] curve)
        {
            var qFactors = new float[_freqBands.Length];

            for (int i = 0; i < _freqBands.Length; i++)
            {
                float freq = _freqBands[i];
                float gain = Math.Abs(curve[i]);

                float baseQ = freq switch
                {
                    <= 63f => 2.5f,
                    <= 250f => 3.0f,
                    <= 1000f => 3.8f,
                    <= 4000f => 4.0f,
                    <= 10000f => 3.8f,
                    _ => 3.0f
                };

                float gainAdjustment = gain switch
                {
                    <= 1f => 1.0f,
                    <= 3f => 1.05f,
                    <= 5f => 1.1f,
                    _ => 1.2f
                };

                qFactors[i] = Math.Clamp(baseQ * gainAdjustment, 2.5f, 5.0f);
            }

            return qFactors;
        }

        /// <summary>
        /// Same preset run over a bunch of files.
        /// </summary>
        /// <param name="fileNameSuffix">Appended to each output name, defaults to the system name.</param>
        public void BatchProcessWithEnhancedPreset(string[] sourceFiles, string baseSampleFile,
            string outputDirectory, PlaybackSystem system, string? fileNameSuffix = null)
        {
            Directory.CreateDirectory(outputDirectory);

            string suffix = fileNameSuffix ?? $"_{system.ToString().ToLower()}";
            string tempDirectory = Path.Combine(Path.GetTempPath(), $"enhanced_preset_{DateTime.Now.Ticks}");
            Directory.CreateDirectory(tempDirectory);

            try
            {
                Log.Info($"=== BATCH ENHANCED PRESET PROCESSING ===");
                Log.Info($"System: {_systemPresets[system].Name}");
                Log.Info($"Processing {sourceFiles.Length} files...");

                for (int i = 0; i < sourceFiles.Length; i++)
                {
                    string _fileName = Path.GetFileNameWithoutExtension(sourceFiles[i]);
                    string outputFile = Path.Combine(outputDirectory, $"{_fileName}{suffix}.wav");

                    Log.Info($"\n[{i + 1}/{sourceFiles.Length}] Processing: {Path.GetFileName(sourceFiles[i])}");

                    try
                    {
                        ProcessWithEnhancedPreset(sourceFiles[i], outputFile, system, tempDirectory);
                        Log.Info($"Completed: {Path.GetFileName(outputFile)}");
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Error processing {Path.GetFileName(sourceFiles[i])}", ex);
                    }
                }

                Log.Info($"\nBatch processing completed. Files saved to: {outputDirectory}");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, true);
                }
                catch (Exception ex)
                {
                    Log.Warning($"Could not delete temp directory: {ex.Message}");
                }
            }
        }
    }
}
