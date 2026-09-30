using Logger;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using OwnaudioNET.Effects;

namespace OwnaudioNET.Features.Matchering
{
    /// <summary>
    /// The matching maths without the render - analysis straight from a buffer, the
    /// settings a live chain has to be set to, and preset targets built in memory.
    /// </summary>
    partial class AudioAnalyzer
    {
        #region Constants and Fields

        /// <summary>
        /// The native 30 band equalizer's default Q, and the default the deconvolution
        /// solves against. The engine takes a Q per band now, so `fixedQ: 0` gives the
        /// optimized ones and they do reach the filter - the default stays here so an
        /// existing profile keeps producing the curve it always did.
        /// </summary>
        public const float NativeBandQ = 4.318474f;

        private readonly ConcurrentDictionary<(PlaybackSystem System, bool EqOnly), AudioSpectrum> _presetTargets =
            new ConcurrentDictionary<(PlaybackSystem, bool), AudioSpectrum>();

        #endregion

        #region Buffer Analysis

        /// <summary>
        /// What AnalyzeAudioFile does, on samples we already have in memory.
        /// </summary>
        /// <returns>Weighted average spectrum of the buffer.</returns>
        public AudioSpectrum AnalyzeAudioBuffer(float[] interleaved, int sampleRate, int channels)
        {
            if (interleaved is null || interleaved.Length == 0)
                throw new ArgumentException("There is nothing to analyze.", nameof(interleaved));

            if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
            if (sampleRate < 1) throw new ArgumentOutOfRangeException(nameof(sampleRate));

            AudioSpectrum spectrum = _analyzeChannels(interleaved, sampleRate, channels);
            spectrum.LoudnessStats = MeasureLoudness(interleaved, sampleRate, channels);

            return spectrum;
        }

        private AudioSpectrum _analyzeChannels(float[] interleaved, int sampleRate, int channels)
        {
            if (channels == 1) return _analyzeMono(interleaved, sampleRate);

            int perChannel = interleaved.Length / channels;
            var channelSpectra = new List<AudioSpectrum>(channels);

            for (int c = 0; c < channels; c++)
            {
                float[] scratch = new float[perChannel];
                for (int i = 0; i < perChannel; i++)
                    scratch[i] = interleaved[i * channels + c];

                Log.Info($"Analyzing Channel {c + 1}...");
                channelSpectra.Add(_analyzeMono(scratch, sampleRate));
            }

            return _averageSpectra(channelSpectra);
        }

        #endregion

        #region Profile Calculation

        /// <summary>
        /// The match, stopped before the render. fixedQ is the Q the deconvolution assumes
        /// on every band (0 for per band Qs); cutOnly drops the curve so nothing is boosted.
        /// </summary>
        public MatcheringProfile CalculateProfile(AudioSpectrum source, AudioSpectrum target, int sampleRate,
            float fixedQ = NativeBandQ, bool cutOnly = true)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (target is null) throw new ArgumentNullException(nameof(target));

            return _profile(source, target, sampleRate, fixedQ, cutOnly, null);
        }

        /// <summary>
        /// The same match, against a playback system preset. The target is the baked preset
        /// sample, and the AGC numbers are the preset's own instead of the measured ones.
        /// </summary>
        /// <param name="eqOnlyMode">Leaves the preset compressor out of the target.</param>
        public MatcheringProfile CalculateProfile(AudioSpectrum source, PlaybackSystem system, int sampleRate,
            float fixedQ = NativeBandQ, bool cutOnly = true, bool eqOnlyMode = false)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));

            AudioSpectrum target = GetPresetTargetSpectrum(system, eqOnlyMode);

            return _profile(source, target, sampleRate, fixedQ, cutOnly, _systemPresets[system]);
        }

        private MatcheringProfile _profile(AudioSpectrum source, AudioSpectrum target, int sampleRate,
            float fixedQ, bool cutOnly, PlaybackPreset? preset)
        {
            float[] wanted = _calcEqAdjustments(source, target);
            float shift = cutOnly ? _cutOnlyShift(wanted) : 0f;

            if (shift > 0f)
            {
                for (int i = 0; i < wanted.Length; i++) wanted[i] -= shift;
                Log.Info($"Cut-only curve: pushed down by {shift:F1} dB, the AGC behind it takes the level back");
            }

            float[] qFactors = fixedQ > 0f ? _flatQ(fixedQ) : _optimalQFactors(wanted, source, target);
            float[] bandGains = _deconvolveToBandGains(wanted, qFactors, sampleRate);

            DynamicAmpSettings amp = preset?.DynamicAmp ?? _ampSettings(source, target);
            OwnCompressorSettings compressor = _compressorSettings(source, target, preset);
            OwnDynamicAmpSettings leveler = _withPreGain(_levelerSettings(source, target, compressor, preset), -shift);

            if (preset is not null)
                Log.Info($"AGC from the {preset.Name} preset: {amp.TargetLevel:F1} LUFS target, max {amp.MaxGain:F2}x");

            return new MatcheringProfile
            {
                WantedCurveDb = wanted,
                BandGainsDb = bandGains,
                QFactors = qFactors,
                Compressor = compressor,
                Leveler = leveler,
                CompThresholdDb = compressor.ThresholdDb,
                CompRatio = compressor.Ratio,
                TargetLoudness = leveler.TargetLoudness,
                MaxGain = amp.MaxGain,
                AmpAttackSeconds = amp.AttackTime,
                AmpReleaseSeconds = amp.ReleaseTime,
                SourceLoudness = new Levels(source).Lufs,
                SourceCrestDb = _crestDb(source),
                TargetCrestDb = _crestDb(target),
                SourcePeakToLoudnessDb = new Levels(source).PeakToLoudness,
                TargetPeakToLoudnessDb = new Levels(target).PeakToLoudness,
                CutOnlyShiftDb = shift
            };
        }

        /// <summary>
        /// Drops the loudest band to 0 dB, but not so far that the deepest cut hits the clamp.
        /// </summary>
        private static float _cutOnlyShift(float[] wanted)
        {
            float peak = float.MinValue, trough = float.MaxValue;

            foreach (float g in wanted)
            {
                if (g > peak) peak = g;
                if (g < trough) trough = g;
            }

            return Math.Max(0f, Math.Min(peak, MaxBandCorrectionDb + trough));
        }

        private static float[] _flatQ(float q)
        {
            float[] qFactors = new float[_freqBands.Length];
            Array.Fill(qFactors, q);

            return qFactors;
        }

        private static float _crestDb(AudioSpectrum spectrum) =>
            20f * MathF.Log10(spectrum.PeakLevel / Math.Max(spectrum.RMSLevel, 1e-10f));

        #endregion

        #region Preset Targets

        /// <summary>
        /// The spectrum a preset asks for, built in memory and cached per system.
        /// </summary>
        /// <param name="system"></param>
        /// <param name="eqOnlyMode">Leaves the preset compressor out of the bake.</param>
        public AudioSpectrum GetPresetTargetSpectrum(PlaybackSystem system, bool eqOnlyMode = false)
        {
            AudioSpectrum cached = _presetTargets.GetOrAdd((system, eqOnlyMode), key =>
            {
                Log.Info($"=== PRESET TARGET SPECTRUM: {_systemPresets[key.System].Name} ===");

                var (audioData, sampleRate, channels) = _baseSampleData();
                _bakePresetIntoBase(audioData, sampleRate, channels, key.System, key.EqOnly);

                return AnalyzeAudioBuffer(audioData, sampleRate, channels);
            });

            return _copyOf(cached);
        }

        /// <summary>
        /// The embedded base sample as floats - the blob is a raw float dump, 48k stereo.
        /// </summary>
        private static (float[] Data, int SampleRate, int Channels) _baseSampleData()
        {
            using Stream stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("OwnaudioNET.basesample.bin")!;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);

            byte[] raw = memory.ToArray();
            float[] data = new float[raw.Length / sizeof(float)];
            Buffer.BlockCopy(raw, 0, data, 0, data.Length * sizeof(float));

            return (data, 48000, 2);
        }

        /// <summary>
        /// Cached spectra go out as copies; AudioSpectrum has public setters.
        /// </summary>
        private static AudioSpectrum _copyOf(AudioSpectrum spectrum)
        {
            return new AudioSpectrum
            {
                FrequencyBands = (float[])spectrum.FrequencyBands.Clone(),
                RMSLevel = spectrum.RMSLevel,
                PeakLevel = spectrum.PeakLevel,
                DynamicRange = spectrum.DynamicRange,
                Loudness = spectrum.Loudness,
                LoudnessStats = spectrum.LoudnessStats is null ? null : new LoudnessInfo
                {
                    IntegratedLufs = spectrum.LoudnessStats.IntegratedLufs,
                    LoudnessRangeLu = spectrum.LoudnessStats.LoudnessRangeLu,
                    TruePeakDbtp = spectrum.LoudnessStats.TruePeakDbtp,
                    NoiseFloorLufs = spectrum.LoudnessStats.NoiseFloorLufs,
                    SideToMidDb = spectrum.LoudnessStats.SideToMidDb
                }
            };
        }

        #endregion
    }

    /// <summary>
    /// What a real time matchering chain has to be set to. No audio in it, so it serializes.
    /// </summary>
    public sealed class MatcheringProfile
    {
        /// <summary>
        /// The 30 band curve we want to hear, dB. The one worth drawing.
        /// </summary>
        public float[] WantedCurveDb { get; init; } = new float[30];

        /// <summary>
        /// What the filter bank has to be set to for that curve to come out of it.
        /// </summary>
        public float[] BandGainsDb { get; init; } = new float[30];

        /// <summary>
        /// Q the deconvolution assumed per band.
        /// </summary>
        public float[] QFactors { get; init; } = new float[30];

        /// <summary>
        /// Every OwnCompressor parameter the match settled on. <see cref="OwnCompressorSettings.ApplyTo"/>
        /// sets a live effect to it.
        /// </summary>
        public OwnCompressorSettings Compressor { get; init; } = new OwnCompressorSettings();

        /// <summary>
        /// Every OwnDynamicAmp parameter the match settled on, the true-peak ceiling included.
        /// On a cut-only profile the initial gain already gives the shift back.
        /// </summary>
        public OwnDynamicAmpSettings Leveler { get; init; } = new OwnDynamicAmpSettings();

        /// <summary>
        /// Threshold in dB, the same as <see cref="Compressor"/>'s.
        /// </summary>
        public float CompThresholdDb { get; init; }

        /// <summary>
        /// Ratio, the same as <see cref="Compressor"/>'s.
        /// </summary>
        public float CompRatio { get; init; }

        /// <summary>
        /// Loudness the rider should chase, LUFS.
        /// </summary>
        public float TargetLoudness { get; init; }

        /// <summary>
        /// Gain ceiling for the AGC.
        /// </summary>
        public float MaxGain { get; init; }

        /// <summary>
        /// AGC attack in seconds.
        /// </summary>
        public float AmpAttackSeconds { get; init; } = 0.1f;

        /// <summary>
        /// AGC release in seconds.
        /// </summary>
        public float AmpReleaseSeconds { get; init; } = 0.5f;

        /// <summary>
        /// Integrated loudness of the source, LUFS.
        /// </summary>
        public float SourceLoudness { get; init; }

        /// <summary>
        /// Sample peak to RMS of the source, dB.
        /// </summary>
        public float SourceCrestDb { get; init; }

        /// <summary>
        /// Sample peak to RMS of the target, dB.
        /// </summary>
        public float TargetCrestDb { get; init; }

        /// <summary>
        /// True peak to integrated loudness of the source, dB.
        /// </summary>
        public float SourcePeakToLoudnessDb { get; init; }

        /// <summary>
        /// True peak to integrated loudness of the target, dB. The compressor takes the source there.
        /// </summary>
        public float TargetPeakToLoudnessDb { get; init; }

        /// <summary>
        /// How far the curve got pushed down, dB. Zero when cutOnly was off.
        /// </summary>
        public float CutOnlyShiftDb { get; init; }
    }

    /// <summary>
    /// OwnCompressor setup of a match, in the effect's own units. The offline render and a
    /// live chain are set from the same numbers.
    /// </summary>
    public sealed record OwnCompressorSettings
    {
        /// <summary>
        /// Threshold, dBFS.
        /// </summary>
        public float ThresholdDb { get; init; } = -18f;

        /// <summary>
        /// Ratio, x:1.
        /// </summary>
        public float Ratio { get; init; } = 2f;

        /// <summary>
        /// Knee width, dB. Wider for the gentle ratios, so the glue never has an edge.
        /// </summary>
        public float KneeDb { get; init; } = 8f;

        /// <summary>
        /// Attack, ms. Faster the more peak the source has over the target.
        /// </summary>
        public float AttackMs { get; init; } = 20f;

        /// <summary>
        /// Release, ms - the fastest one, with auto release stretching it on sustained material.
        /// </summary>
        public float ReleaseMs { get; init; } = 150f;

        /// <summary>
        /// Programme dependent release.
        /// </summary>
        public bool AutoRelease { get; init; } = true;

        /// <summary>
        /// Look-ahead, ms. The gain is down by the time the transient arrives, so the attack can
        /// stay slow enough to keep the punch.
        /// </summary>
        public float LookaheadMs { get; init; } = 2f;

        /// <summary>
        /// Level detector.
        /// </summary>
        public OwnCompressorDetector Detector { get; init; } = OwnCompressorDetector.Peak;

        /// <summary>
        /// Left/right or mid/side. Mid/side whenever the source is stereo.
        /// </summary>
        public OwnCompressorChannelMode ChannelMode { get; init; } = OwnCompressorChannelMode.LeftRight;

        /// <summary>
        /// Stereo link. Lower when the source is wider than the target, so the side is held on its own.
        /// </summary>
        public float StereoLink { get; init; } = 1f;

        /// <summary>
        /// Key high-pass, Hz. Higher the more low end the source carries.
        /// </summary>
        public float SidechainHighPassHz { get; init; }

        /// <summary>
        /// Makeup, dB. Zero: the rider behind the compressor sets the level.
        /// </summary>
        public float MakeupDb { get; init; }

        /// <summary>
        /// Most gain reduction allowed, dB - the peak to loudness excess plus a little.
        /// </summary>
        public float RangeDb { get; init; } = 6f;

        /// <summary>
        /// Sets a live compressor to these values.
        /// </summary>
        /// <param name="compressor">The effect to set.</param>
        public void ApplyTo(OwnCompressorEffect compressor)
        {
            if (compressor is null) throw new ArgumentNullException(nameof(compressor));

            compressor.Mix = 1f;
            compressor.Threshold = ThresholdDb;
            compressor.Ratio = Ratio;
            compressor.Knee = KneeDb;
            compressor.Attack = AttackMs;
            compressor.Release = ReleaseMs;
            compressor.AutoRelease = AutoRelease;
            compressor.Lookahead = LookaheadMs;
            compressor.Detector = Detector;
            compressor.Topology = OwnCompressorTopology.FeedForward;
            compressor.ChannelMode = ChannelMode;
            compressor.StereoLink = StereoLink;
            compressor.SidechainHighPass = SidechainHighPassHz;
            compressor.Makeup = MakeupDb;
            compressor.AutoMakeup = false;
            compressor.Range = RangeDb;
        }
    }

    /// <summary>
    /// OwnDynamicAmp setup of a match, in the effect's own units.
    /// </summary>
    public sealed record OwnDynamicAmpSettings
    {
        /// <summary>
        /// Programme loudness to chase, LUFS.
        /// </summary>
        public float TargetLoudness { get; init; } = -14f;

        /// <summary>
        /// Memory of the loudness estimate, seconds. Short where the source's loudness range has
        /// to come down to the target's, long where only the level has to move.
        /// </summary>
        public float WindowSeconds { get; init; } = 8f;

        /// <summary>
        /// Most the rider may lift, dB.
        /// </summary>
        public float MaxBoostDb { get; init; } = 12f;

        /// <summary>
        /// Most the rider may pull down, dB.
        /// </summary>
        public float MaxCutDb { get; init; } = 12f;

        /// <summary>
        /// Fastest upward movement, dB per second.
        /// </summary>
        public float RiseRateDbPerSec { get; init; } = 1.5f;

        /// <summary>
        /// Fastest downward movement, dB per second.
        /// </summary>
        public float FallRateDbPerSec { get; init; } = 3f;

        /// <summary>
        /// Dead band around the target, dB.
        /// </summary>
        public float ToleranceDb { get; init; } = 1f;

        /// <summary>
        /// Rounding of the gain curve, ms.
        /// </summary>
        public float SmoothingMs { get; init; } = 300f;

        /// <summary>
        /// Relative gate, LU under the programme loudness. Follows the target's loudness range,
        /// so the quiet parts the target keeps are kept here too.
        /// </summary>
        public float RelativeGateLu { get; init; } = -10f;

        /// <summary>
        /// Absolute freeze threshold, LUFS. Under the source's quiet parts, so fades and pauses
        /// never pump up.
        /// </summary>
        public float FreezeThresholdLufs { get; init; } = -55f;

        /// <summary>
        /// True-peak ceiling, dBTP. The target's own true peak, held between -3 and -1.
        /// </summary>
        public float CeilingDbtp { get; init; } = -1f;

        /// <summary>
        /// Limiter look-ahead, ms.
        /// </summary>
        public float LookaheadMs { get; init; } = 5f;

        /// <summary>
        /// Limiter release, ms.
        /// </summary>
        public float LimiterReleaseMs { get; init; } = 150f;

        /// <summary>
        /// Where the rider starts, dB: the loudness difference the match measured.
        /// </summary>
        public float InitialGainDb { get; init; }

        /// <summary>
        /// Sets a live rider to these values.
        /// </summary>
        /// <param name="leveler">The effect to set.</param>
        public void ApplyTo(OwnDynamicAmpEffect leveler)
        {
            if (leveler is null) throw new ArgumentNullException(nameof(leveler));

            leveler.Mix = 1f;
            leveler.TargetLoudness = TargetLoudness;
            leveler.Window = WindowSeconds;
            leveler.MaxBoost = MaxBoostDb;
            leveler.MaxCut = MaxCutDb;
            leveler.RiseRate = RiseRateDbPerSec;
            leveler.FallRate = FallRateDbPerSec;
            leveler.Tolerance = ToleranceDb;
            leveler.Smoothing = SmoothingMs;
            leveler.RelativeGate = RelativeGateLu;
            leveler.FreezeThreshold = FreezeThresholdLufs;
            leveler.Ceiling = CeilingDbtp;
            leveler.LimiterEnabled = true;
            leveler.Lookahead = LookaheadMs;
            leveler.LimiterRelease = LimiterReleaseMs;
            leveler.InitialGain = InitialGainDb;
        }
    }
}
