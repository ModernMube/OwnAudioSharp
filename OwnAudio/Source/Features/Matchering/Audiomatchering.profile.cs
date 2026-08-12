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
        /// Constant Q of the native 30 band equalizer, 1 / (2*sinh(ln2/6)). The engine has
        /// no per band Q parameter, so this is the only Q that ever actually plays.
        /// </summary>
        public const float NativeBandQ = 4.318474f;

        private readonly ConcurrentDictionary<(PlaybackSystem System, bool EqOnly), AudioSpectrum> _presetTargets =
            new ConcurrentDictionary<(PlaybackSystem, bool), AudioSpectrum>();

        #endregion

        #region Buffer Analysis

        /// <summary>
        /// What AnalyzeAudioFile does, on interleaved samples we already have. No FileSource
        /// here, so no analyzer lock and no decode round trip either.
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
        /// Runs the whole match but stops before the render and hands back the numbers.
        /// fixedQ is the Q the deconvolution assumes on every band - pass 0 to let the
        /// analyzer pick per band Qs like the offline path does. cutOnly drops the curve so
        /// no band is boosted, which is what a chain without a gain stage in front wants.
        /// </summary>
        public MatcheringProfile CalculateProfile(AudioSpectrum source, AudioSpectrum target, int sampleRate,
            float fixedQ = NativeBandQ, bool cutOnly = true)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (target is null) throw new ArgumentNullException(nameof(target));

            float[] wanted = _calcEqAdjustments(source, target);
            float shift = cutOnly ? _cutOnlyShift(wanted) : 0f;

            if (shift > 0f)
            {
                for (int i = 0; i < wanted.Length; i++) wanted[i] -= shift;
                Log.Info($"Cut-only curve: pushed down by {shift:F1} dB, the AGC behind it takes the level back");
            }

            float[] qFactors = fixedQ > 0f ? _flatQ(fixedQ) : _optimalQFactors(wanted, source, target);
            float[] bandGains = _deconvolveToBandGains(wanted, qFactors, sampleRate);

            DynamicAmpSettings amp = _ampSettings(source, target);
            var comp = _compSettings(source, target);

            return new MatcheringProfile
            {
                WantedCurveDb = wanted,
                BandGainsDb = bandGains,
                QFactors = qFactors,
                CompThresholdDb = comp.Threshold,
                CompRatio = comp.Ratio,
                TargetLoudness = amp.TargetLevel,
                MaxGain = amp.MaxGain,
                SourceLoudness = source.Loudness,
                SourceCrestDb = _crestDb(source),
                TargetCrestDb = _crestDb(target),
                CutOnlyShiftDb = shift
            };
        }

        /// <summary>
        /// How far the curve may be dropped so the loudest band lands on 0 dB. Stops short
        /// if that would push the deepest cut past the clamp - a curve that hits the rail
        /// is no longer the curve we measured, it's a flattened version of it.
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

        /// <summary>
        /// One Q for every band.
        /// </summary>
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
        /// The spectrum a preset is asking for, without touching the disk: the preset curve
        /// baked into the embedded base sample, then analyzed. Cached per system, since the
        /// base sample and the curves never move.
        /// </summary>
        /// <param name="system"></param>
        /// <param name="eqOnlyMode">Leaves the preset compressor out of the bake.</param>
        public AudioSpectrum GetPresetTargetSpectrum(PlaybackSystem system, bool eqOnlyMode = true)
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
        /// The embedded base sample as floats. The blob is a raw float dump at 48k stereo -
        /// same thing _loadBaseSample writes out to a wav, just without the detour.
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
        /// Cached spectra go out as copies - AudioSpectrum has public setters and nothing
        /// stops a caller from writing into the one we keep.
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
    /// Everything a real time matchering chain has to be set to for one match. No audio in
    /// it, so it serializes into a project file as is.
    /// </summary>
    public sealed class MatcheringProfile
    {
        /// <summary>
        /// The 30 band curve we want to hear, dB. This is the one worth drawing.
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
        /// Compressor threshold in dB - the effect wants it linear, run it through
        /// CompressorEffect.DbToLinear first.
        /// </summary>
        public float CompThresholdDb { get; init; }

        /// <summary>
        /// Ratio, x:1.
        /// </summary>
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
        /// Measured source loudness, dBFS.
        /// </summary>
        public float SourceLoudness { get; init; }

        /// <summary>
        /// Source crest factor in dB.
        /// </summary>
        public float SourceCrestDb { get; init; }

        /// <summary>
        /// Target crest factor in dB.
        /// </summary>
        public float TargetCrestDb { get; init; }

        /// <summary>
        /// How far the curve got pushed down to keep it cut only, dB. Zero when cut only
        /// was off or the curve had nothing to boost anyway.
        /// </summary>
        public float CutOnlyShiftDb { get; init; }
    }
}
