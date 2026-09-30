using Ownaudio.Safe.Effects;
using OwnaudioNET.Sources;
using Logger;
using System;
using System.Numerics;

namespace OwnaudioNET.Features.Matchering
{
    /// <summary>
    /// EQ curve calculation and the offline render chain.
    /// </summary>
    partial class AudioAnalyzer
    {
        #region EQ Calculation and Smoothing

        /// <summary>
        /// How far a single band is allowed to be pushed. Matching is a tonal balance
        /// job, not surgery - the old 18 dB let one bad measurement wreck a master.
        /// </summary>
        private const float MaxBandCorrectionDb = 9.0f;

        /// <summary>
        /// Per band dB delta between source and target. The broadband level difference
        /// is taken out first: that is a gain change, and leaving it in meant the EQ
        /// and the AGC downstream both corrected for the same thing.
        /// </summary>
        private float[] _calcEqAdjustments(AudioSpectrum source, AudioSpectrum target)
        {
            float[] src = _smoothSpectrum(source.FrequencyBands, 0.5f);
            float[] tgt = _smoothSpectrum(target.FrequencyBands, 0.5f);

            int bands = _freqBands.Length;
            float[] wanted = new float[bands];
            bool[] usable = new bool[bands];

            float offsetSum = 0f;
            int offsetCount = 0;

            for (int i = 0; i < bands; i++)
            {
                float srcDb = 20 * (float)Math.Log10(Math.Max(src[i], 1e-10f));
                float tgtDb = 20 * (float)Math.Log10(Math.Max(tgt[i], 1e-10f));

                usable[i] = srcDb > -80.0f && tgtDb > -80.0f;
                if (!usable[i]) continue;

                wanted[i] = tgtDb - srcDb;
                offsetSum += wanted[i];
                offsetCount++;
            }

            float offset = offsetCount > 0 ? offsetSum / offsetCount : 0f;
            for (int i = 0; i < bands; i++)
                wanted[i] = usable[i] ? Math.Clamp(wanted[i] - offset, -MaxBandCorrectionDb, MaxBandCorrectionDb) : 0f;

            Log.Info("\n=== CALCULATED EQ ADJUSTMENTS ===");
            Log.Info($"Broadband offset removed: {offset:+0.0;-0.0} dB (handled as gain, not EQ)");

            for (int i = 0; i < bands; i++)
            {
                string skipped = usable[i] ? "" : " [NO DATA]";
                Log.Info($"{_bandNames[i],8}: {wanted[i],6:F1} dB{skipped}");
            }

            return wanted;
        }

        /// <summary>
        /// Turns the wanted curve into the gains the filter bank has to be set to.
        /// A 1/3 octave bell still bleeds into its neighbours, so setting every band
        /// to its wanted value overshoots by 60-120%. Solves the bank's response
        /// matrix instead, with a little ridge on the diagonal to stop the solution
        /// from ringing band to band.
        /// </summary>
        private float[] _deconvolveToBandGains(float[] wanted, float[] qFactors, int sampleRate)
        {
            int n = _freqBands.Length;
            var m = new double[n, n];

            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    m[j, i] = _bellResponseDb(_freqBands[i], qFactors[i], 1.0f, _freqBands[j], sampleRate);

            var ata = new double[n, n];
            var atb = new double[n];
            double ridge = 0.05;

            for (int r = 0; r < n; r++)
            {
                for (int c = 0; c < n; c++)
                {
                    double sum = 0;
                    for (int k = 0; k < n; k++) sum += m[k, r] * m[k, c];
                    ata[r, c] = sum + (r == c ? ridge : 0.0);
                }

                double b = 0;
                for (int k = 0; k < n; k++) b += m[k, r] * wanted[k];
                atb[r] = b;
            }

            double[] solved = _solve(ata, atb, n);
            float[] gains = new float[n];

            for (int i = 0; i < n; i++)
            {
                float g = (float)solved[i];
                gains[i] = float.IsFinite(g) ? Math.Clamp(g, -MaxBandCorrectionDb, MaxBandCorrectionDb) : 0f;
            }

            Log.Info("\n=== FILTER BANK SOLUTION (wanted -> set) ===");
            for (int i = 0; i < n; i++)
                Log.Info($"{_bandNames[i],8}: wanted {wanted[i],5:F1} dB -> set {gains[i],5:F1} dB (Q={qFactors[i]:F2})");

            return gains;
        }

        /// <summary>
        /// Magnitude of an RBJ peaking filter at one frequency, in dB.
        /// </summary>
        private static float _bellResponseDb(float centreFreq, float q, float gainDb, float atFreq, int sampleRate)
        {
            double a = Math.Pow(10.0, gainDb / 40.0);
            double w0 = 2 * Math.PI * centreFreq / sampleRate;
            double alpha = Math.Sin(w0) / (2 * q);
            double cosW0 = Math.Cos(w0);

            double b0 = 1 + alpha * a, b1 = -2 * cosW0, b2 = 1 - alpha * a;
            double a0 = 1 + alpha / a, a1 = -2 * cosW0, a2 = 1 - alpha / a;

            double w = 2 * Math.PI * atFreq / sampleRate;
            Complex z1 = Complex.FromPolarCoordinates(1.0, -w);
            Complex z2 = z1 * z1;

            Complex num = b0 + b1 * z1 + b2 * z2;
            Complex den = a0 + a1 * z1 + a2 * z2;

            return (float)(20.0 * Math.Log10(Math.Max((num / den).Magnitude, 1e-12)));
        }

        /// <summary>
        /// Gaussian elimination with partial pivoting. Small and offline, nothing
        /// fancy needed.
        /// </summary>
        private static double[] _solve(double[,] a, double[] b, int n)
        {
            for (int col = 0; col < n; col++)
            {
                int pivot = col;
                for (int r = col + 1; r < n; r++)
                    if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col])) pivot = r;

                if (pivot != col)
                {
                    for (int c = 0; c < n; c++) (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
                    (b[col], b[pivot]) = (b[pivot], b[col]);
                }

                double d = a[col, col];
                if (Math.Abs(d) < 1e-12) continue;

                for (int r = col + 1; r < n; r++)
                {
                    double f = a[r, col] / d;
                    if (f == 0) continue;

                    for (int c = col; c < n; c++) a[r, c] -= f * a[col, c];
                    b[r] -= f * b[col];
                }
            }

            var x = new double[n];
            for (int r = n - 1; r >= 0; r--)
            {
                double sum = b[r];
                for (int c = r + 1; c < n; c++) sum -= a[r, c] * x[c];

                x[r] = Math.Abs(a[r, r]) > 1e-12 ? sum / a[r, r] : 0.0;
            }

            return x;
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
        /// Offline render: 30-band EQ -> OwnCompressor -> OwnDynamicAmp, chunked, in place.
        /// The EQ goes first - compressing the un-corrected signal and boosting bands afterwards
        /// undid whatever control it had. We pull some pre-gain so the EQ boosts don't slam into
        /// the ceiling, move the compressor threshold down by the same amount, and hand the
        /// headroom back through the rider's initial gain. The EQ and the compressor run first
        /// and what they left is measured, so the rider starts on exactly the gain the target
        /// needs instead of chasing it. Its true-peak limiter is the last stage, no separate
        /// limiter follows it.
        /// </summary>
        private void _applyEqProcessing(string inputFile, string outputFile,
            AudioSpectrum sourceSpectrum, AudioSpectrum targetSpectrum, PlaybackPreset? preset)
        {
            Log.Info($"Starting EQ processing with direct effect chain: {inputFile} -> {outputFile}");

            using var fileSource = new FileSource(inputFile);

            if (fileSource.Duration == 0)
                throw new InvalidOperationException($"Cannot load audio file: {inputFile}");

            var audioData = fileSource.GetFloatAudioData(TimeSpan.Zero);
            var channels = fileSource.StreamInfo.Channels;
            var sampleRate = fileSource.StreamInfo.SampleRate;

            float[] eqAdjustments = _calcEqAdjustments(sourceSpectrum, targetSpectrum);

            float maxBoost = 0f, totalBoost = 0f;
            int boostCount = 0;

            foreach (float g in eqAdjustments)
            {
                if (g > maxBoost) maxBoost = g;
                if (g > 0) { totalBoost += g; boostCount++; }
            }

            float avgBoost = boostCount > 0 ? totalBoost / boostCount : 0;
            float effectiveBoost = Math.Min(maxBoost, avgBoost + 4.0f);
            float preGainDb = 0f;

            if (effectiveBoost > 0)
            {
                preGainDb = Math.Clamp(-(effectiveBoost + 2.0f), -12.0f, 0.0f);
                float linearPreGain = (float)Math.Pow(10, preGainDb / 20.0f);

                Log.Info($"Applying Smart Headroom: {preGainDb:F1}dB (Max: {maxBoost:F1}dB, Avg: {avgBoost:F1}dB, Effective: {effectiveBoost:F1}dB)");

                for (int i = 0; i < audioData.Length; i++)
                    audioData[i] *= linearPreGain;
            }

            var qFactors = _optimalQFactors(eqAdjustments, sourceSpectrum, targetSpectrum);
            float[] bandGains = _deconvolveToBandGains(eqAdjustments, qFactors, sampleRate);

            OwnCompressorSettings measured = _compressorSettings(sourceSpectrum, targetSpectrum, preset);
            OwnCompressorSettings compressor = measured with
            {
                ThresholdDb = Math.Clamp(measured.ThresholdDb + preGainDb, -60f, 0f)
            };

            OwnDynamicAmpSettings planned = _levelerSettings(sourceSpectrum, targetSpectrum, compressor, preset);

            if (preset is not null)
                Log.Info($"Rider target from the {preset.Name} preset: {planned.TargetLoudness:F1} LUFS");

            Log.Info("\n=== MASTERING CHAIN CONFIGURATION ===");

            Log.Info($"\n[1] EQUALIZER (30-Band Parametric):");

            for (int i = 0; i < _freqBands.Length; i++)
                Log.Info($"    Band {i,2} ({_bandNames[i],8}): {bandGains[i],+6:F1} dB, Q={qFactors[i]:F2}");

            using StandaloneEffect directEQ = NativeMastering.Equalizer30(
                sampleRate, channels, _freqBands, qFactors, bandGains);

            using StandaloneEffect ownCompressor = NativeMastering.OwnCompressor(sampleRate, channels, compressor);

            Log.Info($"\n[2] OWNCOMPRESSOR:");
            Log.Info($"    Threshold: {compressor.ThresholdDb:F1} dB (measured {measured.ThresholdDb:F1} dB, shifted by the {preGainDb:F1} dB pre-gain)");
            Log.Info($"    Ratio: {compressor.Ratio:F2}:1, Knee: {compressor.KneeDb:F1} dB, Range: {compressor.RangeDb:F1} dB");
            Log.Info($"    Attack: {compressor.AttackMs:F0}ms, Release: {compressor.ReleaseMs:F0}ms (auto), Look-ahead: {compressor.LookaheadMs:F1}ms");
            Log.Info($"    {compressor.ChannelMode}, link {compressor.StereoLink:F2}, key high-pass {compressor.SidechainHighPassHz:F0} Hz");

            int totalSamples = (audioData.Length / channels) * channels;

            Log.Info("\n=== PROCESSING AUDIO ===");
            Log.Info($"Chain: EQ → OwnCompressor → OwnDynamicAmp");
            Log.Info($"Sample Rate: {sampleRate} Hz, Channels: {channels}");
            Log.Info($"Total Samples: {audioData.Length:N0}, Total Frames: {audioData.Length / channels:N0}");

            StandaloneEffect[] toneAndDynamics = { directEQ, ownCompressor };

            NativeMastering.Render(audioData, channels, toneAndDynamics, done => Log.Info($"\rEQ + compressor: {done * 100f:F1}%"));
            NativeMastering.CompensateLatency(audioData, totalSamples, channels, toneAndDynamics);

            float shapedLufs = MeasureLoudness(audioData, sampleRate, channels).IntegratedLufs;
            OwnDynamicAmpSettings leveler = _startingFrom(planned, shapedLufs, new Levels(sourceSpectrum).Lufs);

            using StandaloneEffect ownDynamicAmp = NativeMastering.OwnDynamicAmp(sampleRate, channels, leveler);

            Log.Info($"\n[3] OWNDYNAMICAMP (BS.1770 rider + true-peak limiter):");
            Log.Info($"    Target: {leveler.TargetLoudness:F1} LUFS, Window: {leveler.WindowSeconds:F1}s, Tolerance: {leveler.ToleranceDb:F1} dB");
            Log.Info($"    Rates: +{leveler.RiseRateDbPerSec:F1}/-{leveler.FallRateDbPerSec:F1} dB/s, Boost/Cut: {leveler.MaxBoostDb:F1}/{leveler.MaxCutDb:F1} dB");
            Log.Info($"    Gates: relative {leveler.RelativeGateLu:F0} LU, freeze {leveler.FreezeThresholdLufs:F0} LUFS");
            Log.Info($"    Initial Gain: {leveler.InitialGainDb:+0.0;-0.0} dB from the {shapedLufs:F1} LUFS the EQ and compressor left, Ceiling: {leveler.CeilingDbtp:F1} dBTP");

            StandaloneEffect[] rider = { ownDynamicAmp };

            NativeMastering.Render(audioData, channels, rider, done => Log.Info($"\rRider: {done * 100f:F1}%"));
            NativeMastering.CompensateLatency(audioData, totalSamples, channels, rider);

            Log.Info("\nWriting to file...");
            OwnaudioNET.Recording.WaveFile.Create(outputFile, audioData, sampleRate, channels, 24);
            Log.Info($"Processing completed: {outputFile}");
        }

        #endregion
    }
}
