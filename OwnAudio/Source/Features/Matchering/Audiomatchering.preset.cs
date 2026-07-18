using OwnaudioNET.Effects;
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
            PlaybackSystem system, string? tempDirectory = null, bool eqOnlyMode = true)
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
                ProcessEQMatching(sourceFile, processedBaseSample, outputFile);

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
            var preset = _systemPresets[system];

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

            float originalRMS = _calcRms(audioData);

            var curve = _conservativeCurve(preset.FrequencyResponse);
            var qFactors = _presetQFactors(curve);

            float totalBoosts = 0f;
            foreach (float g in curve) if (g > 0) totalBoosts += g;

            float protectiveGain = 0.95f * Math.Max(0.7f, 1.0f - (totalBoosts * 0.03f));

            for (int i = 0; i < audioData.Length; i++)
                audioData[i] *= protectiveGain;

            Log.Info($"Applied protective gain: {20 * Math.Log10(protectiveGain):F1}dB (total boosts: {totalBoosts:F1}dB)");

            var presetEQ = new Equalizer30BandEffect(sampleRate);
            for (int i = 0; i < _freqBands.Length; i++)
                presetEQ.SetBandGain(i, _freqBands[i], qFactors[i], curve[i]);

            CompressorEffect? compressor = eqOnlyMode
                ? null
                : new CompressorEffect(CompressorEffect.DbToLinear(-15f), 1.8f, 50f, 200f, 2.0f, sampleRate);

            var audioConfig = new Ownaudio.Core.AudioConfig
            {
                SampleRate = sampleRate,
                Channels = channels,
                BufferSize = 512
            };

            presetEQ.Initialize(audioConfig);
            compressor?.Initialize(audioConfig);

            Log.Info($"Applying {(eqOnlyMode ? "EQ-only" : "full")} {preset.Name} effects to base sample...");

            int samplesPerChunk = 512 * channels;
            int totalSamples = (audioData.Length / channels) * channels;
            float maxLevel = 0f;

            for (int offset = 0; offset < totalSamples; offset += samplesPerChunk)
            {
                int count = Math.Min(samplesPerChunk, totalSamples - offset);
                int frames = count / channels;
                var chunk = audioData.AsSpan(offset, count);

                presetEQ.Process(chunk, frames);
                compressor?.Process(chunk, frames);

                for (int i = 0; i < chunk.Length; i++)
                {
                    float abs = Math.Abs(chunk[i]);
                    if (abs > maxLevel) maxLevel = abs;

                    if (abs > 0.95f)
                        chunk[i] = Math.Sign(chunk[i]) * (0.95f + 0.05f * MathF.Tanh((abs - 0.95f) * 4f));
                }
            }

            Log.Info($"\nBase sample processed. Max level: {20 * Math.Log10(maxLevel):F1}dB");

            float levelCompensation = originalRMS / Math.Max(_calcRms(audioData), 1e-10f);
            float finalMax = 0f;

            for (int i = 0; i < audioData.Length; i++)
            {
                audioData[i] *= levelCompensation;
                finalMax = Math.Max(finalMax, Math.Abs(audioData[i]));
            }

            Log.Info($"Level compensation applied: {20 * Math.Log10(levelCompensation):F1}dB");
            Log.Info($"Final max level: {20 * Math.Log10(finalMax):F1}dB");

            OwnaudioNET.Recording.WaveFile.Create(processedBaseSample, audioData, sampleRate, channels, 24);

            Log.Info($"Enhanced base sample created: {processedBaseSample}");
        }

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
            catch
            {
                throw new Exception("Load error target audio data!");
            }
        }

        /// <summary>
        /// Tames the preset curve before it's used as a matchering target - full strength
        /// presets overdrive the matcher badly.
        /// </summary>
        private float[] _conservativeCurve(float[] originalCurve)
        {
            var curve = new float[originalCurve.Length];

            for (int i = 0; i < originalCurve.Length; i++)
            {
                float freq = _freqBands[i];

                float factor = freq switch
                {
                    <= 63f => 0.75f,
                    <= 250f => 0.8f,
                    <= 1000f => 0.85f,
                    <= 4000f => 0.85f,
                    <= 8000f => 0.8f,
                    _ => 0.75f
                };

                float maxBoost = freq switch
                {
                    < 100f => 3f,
                    < 500f => 3f,
                    < 2000f => 3.5f,
                    < 5000f => 3.5f,
                    < 10000f => 3f,
                    _ => 3f
                };

                curve[i] = Math.Clamp(originalCurve[i] * factor, -4f, maxBoost);
            }

            Log.Info("Conservative preset curve created for matchering:");

            for (int i = 0; i < curve.Length; i++)
            {
                if (Math.Abs(curve[i]) > 0.5f)
                    Log.Info($"{_bandNames[i]}: {curve[i]:+0.1;-0.1}dB (was {originalCurve[i]:+0.1;-0.1}dB)");
            }

            return curve;
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
                        Log.Error($"Error processing {Path.GetFileName(sourceFiles[i])}: {ex.Message}");
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
