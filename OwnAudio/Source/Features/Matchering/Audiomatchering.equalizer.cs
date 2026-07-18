using OwnaudioNET.Effects;
using OwnaudioNET.Sources;
using Logger;
using System;

namespace OwnaudioNET.Features.Matchering
{
    /// <summary>
    /// EQ curve calculation and the offline render chain.
    /// </summary>
    partial class AudioAnalyzer
    {
        #region EQ Calculation and Smoothing

        /// <summary>
        /// Per band dB delta between source and target, smoothed a bit and clamped
        /// to what the 30 band EQ can actually do.
        /// </summary>
        private float[] _calcEqAdjustments(AudioSpectrum source, AudioSpectrum target)
        {
            float[] adjustments = new float[_freqBands.Length];

            float[] src = _smoothSpectrum(source.FrequencyBands, 0.5f);
            float[] tgt = _smoothSpectrum(target.FrequencyBands, 0.5f);

            Log.Info("\n=== CALCULATED EQ ADJUSTMENTS ===");

            for (int i = 0; i < adjustments.Length; i++)
            {
                float srcDb = 20 * (float)Math.Log10(Math.Max(src[i], 1e-10f));
                float tgtDb = 20 * (float)Math.Log10(Math.Max(tgt[i], 1e-10f));

                float raw = (srcDb < -80.0f && tgtDb > srcDb) ? 0.0f : tgtDb - srcDb;
                adjustments[i] = Math.Clamp(raw, -18.0f, 18.0f);

                string limited = (Math.Abs(adjustments[i] - raw) > 0.01f) ? " [LIMITED]" : "";
                Log.Info($"{_bandNames[i],8}: {adjustments[i],6:F1} dB (Raw: {raw,6:F1} dB) " +
                                $"[Src: {srcDb,6:F1} dB -> Tgt: {tgtDb,6:F1} dB]{limited}");
            }

            return adjustments;
        }

        /// <summary>
        /// Weighted 3 tap moving average over the bands. smoothingFactor is how much
        /// the neighbours count against the center bin.
        /// </summary>
        private float[] _smoothSpectrum(float[] spectrum, float smoothingFactor = 0.25f)
        {
            float[] smoothed = new float[spectrum.Length];

            for (int i = 0; i < spectrum.Length; i++)
            {
                float centerWeight = 1.0f + smoothingFactor * 2.0f;
                float sum = spectrum[i] * centerWeight;
                float div = centerWeight;

                if (i > 0) { sum += spectrum[i - 1] * smoothingFactor; div += smoothingFactor; }
                if (i < spectrum.Length - 1) { sum += spectrum[i + 1] * smoothingFactor; div += smoothingFactor; }

                smoothed[i] = sum / div;
            }
            return smoothed;
        }

        #endregion

        #region Direct EQ Processing

        /// <summary>
        /// Offline render: Compressor -> EQ -> DynamicAmp -> Limiter, chunked, in place.
        /// We pull some pre-gain first so the EQ boosts don't slam into the ceiling,
        /// then hand that headroom back through the AGC initial gain.
        /// </summary>
        private void _applyEqProcessing(string inputFile, string outputFile,
            float[] eqAdjustments, DynamicAmpSettings dynamicAmp,
            (float Threshold, float Ratio) compSettings,
            AudioSpectrum sourceSpectrum, AudioSpectrum targetSpectrum)
        {
            Log.Info($"Starting EQ processing with direct effect chain: {inputFile} -> {outputFile}");

            using var fileSource = new FileSource(inputFile);

            if (fileSource.Duration == 0)
                throw new InvalidOperationException($"Cannot load audio file: {inputFile}");

            var audioData = fileSource.GetFloatAudioData(TimeSpan.Zero);
            var channels = fileSource.StreamInfo.Channels;
            var sampleRate = fileSource.StreamInfo.SampleRate;

            float maxBoost = 0f, totalBoost = 0f;
            int boostCount = 0;

            foreach (float g in eqAdjustments)
            {
                if (g > maxBoost) maxBoost = g;
                if (g > 0) { totalBoost += g; boostCount++; }
            }

            float avgBoost = boostCount > 0 ? totalBoost / boostCount : 0;
            float effectiveBoost = Math.Min(maxBoost, avgBoost + 4.0f);

            if (effectiveBoost > 0)
            {
                float preGainDb = Math.Clamp(-(effectiveBoost + 2.0f), -12.0f, 0.0f);
                float linearPreGain = (float)Math.Pow(10, preGainDb / 20.0f);

                Log.Info($"Applying Smart Headroom: {preGainDb:F1}dB (Max: {maxBoost:F1}dB, Avg: {avgBoost:F1}dB, Effective: {effectiveBoost:F1}dB)");

                for (int i = 0; i < audioData.Length; i++)
                    audioData[i] *= linearPreGain;
            }

            var qFactors = _optimalQFactors(eqAdjustments, sourceSpectrum, targetSpectrum);
            Log.Info("\n=== MASTERING CHAIN CONFIGURATION ===");

            var globalCompressor = new CompressorEffect(
                CompressorEffect.DbToLinear(compSettings.Threshold),
                compSettings.Ratio,
                10.0f,
                100.0f,
                1.0f
            );

            Log.Info($"\n[1] COMPRESSOR:");
            Log.Info($"    Threshold: {compSettings.Threshold:F1} dB");
            Log.Info($"    Ratio: {compSettings.Ratio:F1}:1");
            Log.Info($"    Attack: 10ms, Release: 100ms (Surgical)");

            var directEQ = new Equalizer30BandEffect();

            Log.Info($"\n[2] EQUALIZER (30-Band Parametric):");

            for (int i = 0; i < _freqBands.Length; i++)
            {
                directEQ.SetBandGain(i, _freqBands[i], qFactors[i], eqAdjustments[i]);
                Log.Info($"    Band {i,2} ({_bandNames[i],8}): {eqAdjustments[i],+6:F1} dB, Q={qFactors[i]:F2}");
            }

            float headroomRecoveryGain = (effectiveBoost > 0) ? (float)Math.Pow(10, (effectiveBoost + 2.0f) / 20.0f) : 1.0f;
            float maxGain = Math.Min(dynamicAmp.MaxGain * headroomRecoveryGain, 3.0f);

            var dynamicAmplifier = new DynamicAmpEffect(
                targetLevel: dynamicAmp.TargetLevel,
                attackTimeSeconds: 0.2f,
                releaseTimeSeconds: 0.8f,
                noiseThresholdDbOrLinear: -50.0f,
                maxGainValue: maxGain,
                sampleRateHz: sampleRate,
                rmsWindowSeconds: 0.8f,
                initialGain: headroomRecoveryGain,
                maxGainChangePerSecondDb: 12.0f,
                maxGainReductionDb: 6.0f
            );

            Log.Info($"\n[3] DYNAMIC AMPLIFIER (AGC - Optimized):");
            Log.Info($"    Target Level: {dynamicAmp.TargetLevel:F1} dB");
            Log.Info($"    Attack: 0.2s, Release: 0.8s (Fast & Musical)");
            Log.Info($"    Max Gain: {maxGain:F2}x ({20 * MathF.Log10(maxGain):+F1} dB - CAPPED)");
            Log.Info($"    Initial Gain: {headroomRecoveryGain:F2}x ({20 * MathF.Log10(headroomRecoveryGain):+F1} dB compensation)");

            var outputLimiter = new LimiterEffect(
                sampleRate,
                threshold: -0.5f,
                ceiling: -0.2f,
                release: 60.0f,
                lookAheadMs: 5.0f
            );

            Log.Info($"\n[4] LIMITER (True Peak):");
            Log.Info($"    Threshold: -0.5 dB, Ceiling: -0.2 dB");

            var audioConfig = new Ownaudio.Core.AudioConfig
            {
                SampleRate = sampleRate,
                Channels = channels,
                BufferSize = 512
            };

            globalCompressor.Initialize(audioConfig);
            directEQ.Initialize(audioConfig);
            dynamicAmplifier.Initialize(audioConfig);
            outputLimiter.Initialize(audioConfig);

            Log.Info("\n=== PROCESSING AUDIO ===");
            Log.Info($"Chain: Compressor → EQ → DynamicAmp → Limiter");
            Log.Info($"Sample Rate: {sampleRate} Hz, Channels: {channels}");
            Log.Info($"Total Samples: {audioData.Length:N0}, Total Frames: {audioData.Length / channels:N0}");

            int samplesPerChunk = 512 * channels;
            int totalSamples = (audioData.Length / channels) * channels;

            for (int offset = 0; offset < totalSamples; offset += samplesPerChunk)
            {
                int count = Math.Min(samplesPerChunk, totalSamples - offset);
                int frames = count / channels;
                var chunk = audioData.AsSpan(offset, count);

                globalCompressor.Process(chunk, frames);
                directEQ.Process(chunk, frames);
                dynamicAmplifier.Process(chunk, frames);
                outputLimiter.Process(chunk, frames);

                Log.Info($"\rProcessing: {(float)(offset + count) / totalSamples * 100f:F1}%");
            }

            Log.Info("\nWriting to file...");
            OwnaudioNET.Recording.WaveFile.Create(outputFile, audioData, sampleRate, channels, 24);
            Log.Info($"Processing completed: {outputFile}");
        }

        #endregion
    }
}
