using System;
using Logger;
using OwnaudioNET.Effects;

namespace OwnaudioNET.Features.Matchering
{
    /// <summary>
    /// Dynamics side of the matching. The OwnCompressor takes the peak to loudness ratio
    /// from the source to the target, the OwnDynamicAmp takes the integrated loudness and
    /// the loudness range there, and its true-peak limiter holds the ceiling the target has.
    /// </summary>
    partial class AudioAnalyzer
    {
        #region Measured Levels

        /// <summary>
        /// The loudness readings a setting is derived from. A spectrum without them (built by
        /// hand, or from an older analysis) gets an estimate from its RMS and peak.
        /// </summary>
        private readonly struct Levels
        {
            public readonly float Lufs;
            public readonly float Range;
            public readonly float TruePeak;
            public readonly float Floor;
            public readonly float SideToMid;
            public readonly bool Stereo;

            public Levels(AudioSpectrum spectrum)
            {
                LoudnessInfo? stats = spectrum.LoudnessStats;

                if (stats is not null)
                {
                    Lufs = stats.IntegratedLufs;
                    Range = stats.LoudnessRangeLu;
                    TruePeak = stats.TruePeakDbtp;
                    Floor = stats.NoiseFloorLufs;
                    SideToMid = stats.SideToMidDb;
                    Stereo = stats.SideToMidDb > MonoSideToMidDb;
                }
                else
                {
                    Lufs = spectrum.Loudness;
                    Range = EstimatedRangeLu;
                    TruePeak = _toDb(spectrum.PeakLevel);
                    Floor = spectrum.Loudness - 20f;
                    SideToMid = MonoSideToMidDb;
                    Stereo = false;
                }
            }

            public float PeakToLoudness => TruePeak - Lufs;
        }

        /// <summary>
        /// Loudness range assumed when a spectrum carries no measurement, a typical pop master.
        /// </summary>
        private const float EstimatedRangeLu = 6f;

        #endregion

        #region OwnCompressor Settings

        /// <summary>
        /// Threshold, ratio and range come from how much more peak to loudness ratio the source
        /// has than the target. The threshold sits a few dB over the source's integrated loudness
        /// so only the peaks cross it, the ratio takes exactly the excess off the true peak, and
        /// the range stops it there - the compressor can never squash further than the match
        /// asks. A source already denser than the target only gets its top peaks caught. Against
        /// a preset, the system's knee, timing, look-ahead, detector and key high-pass replace
        /// the derived ones.
        /// </summary>
        private OwnCompressorSettings _compressorSettings(AudioSpectrum source, AudioSpectrum target,
            PlaybackPreset? preset)
        {
            var s = new Levels(source);
            var t = new Levels(target);

            float excess = s.PeakToLoudness - t.PeakToLoudness;
            float threshold, ratio, range;

            if (excess > 1f)
            {
                threshold = s.Lufs + Math.Clamp(s.PeakToLoudness * 0.35f, 3f, 8f);
                float over = Math.Max(s.TruePeak - threshold, 1f);
                float reduction = Math.Min(excess, over * 0.85f);

                ratio = over / (over - reduction);
                range = excess + 2f;
            }
            else
            {
                threshold = s.TruePeak - 4f;
                ratio = 1.5f;
                range = 2f;
            }

            threshold = Math.Clamp(threshold, -40f, -0.5f);
            ratio = Math.Clamp(ratio, 1.2f, 6f);

            float sideDiff = s.SideToMid - t.SideToMid;

            OwnCompressorSettings settings = new OwnCompressorSettings
            {
                ThresholdDb = threshold,
                Ratio = ratio,
                KneeDb = Math.Clamp(12f - 2f * (ratio - 1f), 4f, 12f),
                AttackMs = Math.Clamp(30f - 2.5f * Math.Max(excess, 0f), 5f, 30f),
                ReleaseMs = Math.Clamp(60f + 15f * s.Range, 80f, 400f),
                AutoRelease = true,
                LookaheadMs = 2f,
                Detector = OwnCompressorDetector.Peak,
                ChannelMode = s.Stereo ? OwnCompressorChannelMode.MidSide : OwnCompressorChannelMode.LeftRight,
                StereoLink = s.Stereo && t.Stereo ? Math.Clamp(0.8f - 0.05f * sideDiff, 0.5f, 1f) : 1f,
                SidechainHighPassHz = _sidechainHighPass(source),
                MakeupDb = 0f,
                RangeDb = Math.Clamp(range, 1f, 24f)
            };

            if (preset is not null)
            {
                OwnCompressorSettings system = preset.Compressor;

                settings = settings with
                {
                    KneeDb = system.KneeDb,
                    AttackMs = system.AttackMs,
                    ReleaseMs = system.ReleaseMs,
                    AutoRelease = system.AutoRelease,
                    LookaheadMs = system.LookaheadMs,
                    Detector = system.Detector,
                    SidechainHighPassHz = system.SidechainHighPassHz
                };

                Log.Info($"OwnCompressor character from the {preset.Name} preset");
            }

            Log.Info($"Dynamics Match: PLR source {s.PeakToLoudness:F1}dB vs target {t.PeakToLoudness:F1}dB, " +
                     $"LRA {s.Range:F1} vs {t.Range:F1} LU");
            Log.Info($"OwnCompressor: {settings.ThresholdDb:F1}dB, {settings.Ratio:F2}:1, knee {settings.KneeDb:F1}dB, " +
                     $"{settings.AttackMs:F0}/{settings.ReleaseMs:F0}ms{(settings.AutoRelease ? " auto" : "")}, range {settings.RangeDb:F1}dB, " +
                     $"{settings.ChannelMode} link {settings.StereoLink:F2}, key HPF {settings.SidechainHighPassHz:F0}Hz");

            return settings;
        }

        /// <summary>
        /// The more of the source's energy sits under 80 Hz, the higher the key high-pass goes,
        /// so the kick and the bass stop pumping the whole mix.
        /// </summary>
        private static float _sidechainHighPass(AudioSpectrum source)
        {
            double low = 0, total = 0;

            for (int i = 0; i < source.FrequencyBands.Length; i++)
            {
                double p = (double)source.FrequencyBands[i] * source.FrequencyBands[i];
                total += p;
                if (_freqBands[i] <= 80f) low += p;
            }

            if (total <= 1e-20) return 0f;

            float shareDb = (float)(10.0 * Math.Log10(Math.Max(low, 1e-20) / total));

            return Math.Clamp(30f + (shareDb + 12f) * 10f, 20f, 150f);
        }

        #endregion

        #region OwnDynamicAmp Settings

        /// <summary>
        /// The rider chases the target's integrated loudness (the preset's, when there is one)
        /// and its true peak ceiling. Where the source moves more than the target, the window
        /// shortens and the rates go up so the rider takes the extra range out; where it moves
        /// less, a long window only sets the level and leaves the arrangement alone. It starts
        /// from the gain the match already knows it needs. Against a preset, the system's window,
        /// rates, tolerance, gates, ceiling and limiter timing replace the derived ones.
        /// </summary>
        private OwnDynamicAmpSettings _levelerSettings(AudioSpectrum source, AudioSpectrum target,
            OwnCompressorSettings compressor, PlaybackPreset? preset)
        {
            var s = new Levels(source);
            var t = new Levels(target);

            float targetLufs = preset?.Leveler.TargetLoudness ?? t.Lufs;
            float gain = targetLufs - s.Lufs;
            float rangeExcess = s.Range - t.Range;

            float window, tolerance, rise;

            if (rangeExcess > 1f)
            {
                window = Math.Clamp(12f - 1.5f * rangeExcess, 3f, 12f);
                tolerance = Math.Clamp(t.Range / 6f, 0.5f, 1.5f);
                rise = Math.Clamp(1f + 0.25f * rangeExcess, 1f, 4f);
            }
            else
            {
                window = Math.Clamp(10f + s.Range, 10f, 30f);
                tolerance = 1f;
                rise = 1f;
            }

            OwnDynamicAmpSettings settings = new OwnDynamicAmpSettings
            {
                TargetLoudness = Math.Clamp(targetLufs, -40f, -5f),
                WindowSeconds = window,
                MaxBoostDb = Math.Clamp(Math.Max(gain, 0f) + 6f, 6f, 30f),
                MaxCutDb = Math.Clamp(Math.Max(-gain, 0f) + 6f, 6f, 30f),
                RiseRateDbPerSec = rise,
                FallRateDbPerSec = Math.Min(2f * rise, 40f),
                ToleranceDb = tolerance,
                SmoothingMs = Math.Clamp(window * 60f, 200f, 1500f),
                RelativeGateLu = -Math.Clamp(t.Range + 4f, 8f, 20f),
                FreezeThresholdLufs = Math.Clamp(Math.Min(s.Floor, s.Lufs - 20f), -70f, -40f),
                CeilingDbtp = Math.Clamp(t.TruePeak, -3f, -1f),
                LookaheadMs = 5f,
                LimiterReleaseMs = Math.Clamp(compressor.ReleaseMs, 60f, 250f),
                InitialGainDb = Math.Clamp(gain, -30f, 30f)
            };

            if (preset is not null)
            {
                OwnDynamicAmpSettings system = preset.Leveler;

                settings = settings with
                {
                    WindowSeconds = system.WindowSeconds,
                    RiseRateDbPerSec = system.RiseRateDbPerSec,
                    FallRateDbPerSec = system.FallRateDbPerSec,
                    ToleranceDb = system.ToleranceDb,
                    SmoothingMs = system.SmoothingMs,
                    RelativeGateLu = system.RelativeGateLu,
                    CeilingDbtp = system.CeilingDbtp,
                    LookaheadMs = system.LookaheadMs,
                    LimiterReleaseMs = system.LimiterReleaseMs
                };

                Log.Info($"OwnDynamicAmp character from the {preset.Name} preset");
            }

            Log.Info($"OwnDynamicAmp: {settings.TargetLoudness:F1} LUFS from {s.Lufs:F1} LUFS, window {settings.WindowSeconds:F1}s, " +
                     $"tolerance {settings.ToleranceDb:F1}dB, +{settings.MaxBoostDb:F0}/-{settings.MaxCutDb:F0}dB, " +
                     $"ceiling {settings.CeilingDbtp:F1} dBTP, starts at {settings.InitialGainDb:+0.0;-0.0}dB");

            return settings;
        }

        /// <summary>
        /// The same settings with the gain the render took off in front of the chain added back
        /// onto the start and the boost headroom.
        /// </summary>
        private static OwnDynamicAmpSettings _withPreGain(OwnDynamicAmpSettings settings, float preGainDb)
        {
            float initial = Math.Clamp(settings.InitialGainDb - preGainDb, -30f, 30f);

            return settings with
            {
                InitialGainDb = initial,
                MaxBoostDb = Math.Clamp(Math.Max(settings.MaxBoostDb, initial + 6f), 0f, 30f)
            };
        }

        /// <summary>
        /// The same settings starting from the gain that takes audio measured at `measuredLufs`
        /// to the target, with the boost and cut room around it.
        /// </summary>
        private static OwnDynamicAmpSettings _startingFrom(OwnDynamicAmpSettings settings, float measuredLufs)
        {
            float initial = Math.Clamp(settings.TargetLoudness - measuredLufs, -30f, 30f);

            return settings with
            {
                InitialGainDb = initial,
                MaxBoostDb = Math.Clamp(Math.Max(settings.MaxBoostDb, initial + 6f), 0f, 30f),
                MaxCutDb = Math.Clamp(Math.Max(settings.MaxCutDb, 6f - initial), 0f, 30f)
            };
        }

        #endregion

        #region Legacy AGC Settings

        /// <summary>
        /// The settings the old DynamicAmpEffect takes, kept for callers still driving one
        /// from a profile.
        /// </summary>
        private DynamicAmpSettings _ampSettings(AudioSpectrum source, AudioSpectrum target)
        {
            return new DynamicAmpSettings
            {
                TargetLevel = new Levels(target).Lufs,
                AttackTime = 0.1f,
                ReleaseTime = 0.5f,
                MaxGain = 6.0f
            };
        }

        #endregion
    }
}
