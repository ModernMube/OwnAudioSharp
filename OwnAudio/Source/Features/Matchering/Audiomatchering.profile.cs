using Logger;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

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
        /// Constant Q of the native 30 band equalizer - the only Q that ever plays.
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
            var comp = _compSettings(source, target);

            if (preset is not null)
                Log.Info($"AGC from the {preset.Name} preset: {amp.TargetLevel:F1}dB target, max {amp.MaxGain:F2}x");

            return new MatcheringProfile
            {
                WantedCurveDb = wanted,
                BandGainsDb = bandGains,
                QFactors = qFactors,
                CompThresholdDb = comp.Threshold,
                CompRatio = comp.Ratio,
                TargetLoudness = amp.TargetLevel,
                MaxGain = amp.MaxGain,
                AmpAttackSeconds = amp.AttackTime,
                AmpReleaseSeconds = amp.ReleaseTime,
                SourceLoudness = source.Loudness,
                SourceCrestDb = _crestDb(source),
                TargetCrestDb = _crestDb(target),
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
                Loudness = spectrum.Loudness
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
        /// Threshold in dB; CompressorEffect wants it linear, see DbToLinear.
        /// </summary>
        public float CompThresholdDb { get; init; }

        public float CompRatio { get; init; }

        /// <summary>
        /// Loudness the AGC should chase, dBFS.
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

        public float SourceLoudness { get; init; }

        public float SourceCrestDb { get; init; }

        public float TargetCrestDb { get; init; }

        /// <summary>
        /// How far the curve got pushed down, dB. Zero when cutOnly was off.
        /// </summary>
        public float CutOnlyShiftDb { get; init; }
    }
}
