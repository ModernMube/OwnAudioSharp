using OwnaudioNET.Effects;
using OwnaudioNET.Features.Matchering;
using OwnaudioNET.Recording;
using Xunit;
using System;
using System.IO;
using System.Numerics;

namespace Ownaudio.Test.OwnaudioNET.Features
{
    /// <summary>
    /// The settings-returning side of the matcher. Nothing here writes a file.
    /// </summary>
    public class MatcheringProfileTests : IDisposable
    {
        private const int Bands = 30;
        private const float Duration = 14.0f;

        private static readonly float[] _centres = {
            20f, 25f, 31.5f, 40f, 50f, 63f, 80f, 100f, 125f, 160f,
            200f, 250f, 315f, 400f, 500f, 630f, 800f, 1000f, 1250f, 1600f,
            2000f, 2500f, 3150f, 4000f, 5000f, 6300f, 8000f, 10000f, 12500f, 16000f
        };

        private readonly string _dir;

        public MatcheringProfileTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ownaudio-profile-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private static float[] Noise(int sampleRate, int channels)
        {
            int frames = (int)(Duration * sampleRate);
            float[] samples = new float[frames * channels];
            var rng = new Random(4711);

            for (int i = 0; i < frames; i++)
            {
                float v = (float)(rng.NextDouble() * 2.0 - 1.0) * 0.3f;
                for (int c = 0; c < channels; c++) samples[i * channels + c] = v;
            }

            return samples;
        }

        /// <summary>
        /// A spectrum straight from per band dB levels, so the profile tests skip the FFT.
        /// </summary>
        private static AudioSpectrum SpectrumFrom(float[] bandDb, float rms = 0.1f, float peak = 0.5f)
        {
            float[] bands = new float[Bands];
            for (int i = 0; i < Bands; i++) bands[i] = MathF.Pow(10f, bandDb[i] / 20f);

            return new AudioSpectrum
            {
                FrequencyBands = bands,
                RMSLevel = rms,
                PeakLevel = peak,
                Loudness = 20f * MathF.Log10(rms),
                DynamicRange = 20f * MathF.Log10(peak / rms)
            };
        }

        private static float[] Flat(float db)
        {
            float[] curve = new float[Bands];
            Array.Fill(curve, db);
            return curve;
        }

        [Fact]
        public void BufferAnalysisMatchesTheFileOne()
        {
            var analyzer = new AudioAnalyzer();
            float[] samples = Noise(48000, 2);

            string path = Path.Combine(_dir, "noise.wav");
            WaveFile.Create(path, samples, 48000, 2, 24);

            float[] fromFile = analyzer.AnalyzeAudioFile(path).FrequencyBands;
            float[] fromBuffer = analyzer.AnalyzeAudioBuffer(samples, 48000, 2).FrequencyBands;

            for (int i = 2; i < Bands; i++)
            {
                float diff = Db(fromFile[i]) - Db(fromBuffer[i]);
                Assert.True(Math.Abs(diff) < 0.5f,
                    $"band {i}: the file read {Db(fromFile[i]):F2} dB, the buffer {Db(fromBuffer[i]):F2} dB");
            }
        }

        [Fact]
        public void EmptyBufferIsRejected()
        {
            var analyzer = new AudioAnalyzer();
            Assert.Throws<ArgumentException>(() => analyzer.AnalyzeAudioBuffer(Array.Empty<float>(), 48000, 2));
        }

        [Fact]
        public void CutOnlyCurveNeverBoosts()
        {
            var analyzer = new AudioAnalyzer();

            var source = SpectrumFrom(Flat(-30f));
            var target = SpectrumFrom(Tilt());

            var profile = analyzer.CalculateProfile(source, target, 48000);

            Assert.True(profile.CutOnlyShiftDb > 0f, "nothing got pushed down at all");

            foreach (float g in profile.WantedCurveDb)
                Assert.True(g <= 0.01f, $"a band still wants {g:F2} dB of boost");
        }

        [Fact]
        public void CutOnlyOnlyMovesTheLevel()
        {
            var analyzer = new AudioAnalyzer();

            var source = SpectrumFrom(Flat(-30f));
            var target = SpectrumFrom(Tilt());

            float[] shifted = analyzer.CalculateProfile(source, target, 48000, cutOnly: true).WantedCurveDb;
            float[] centred = analyzer.CalculateProfile(source, target, 48000, cutOnly: false).WantedCurveDb;

            for (int i = 1; i < Bands; i++)
            {
                float a = shifted[i] - shifted[i - 1];
                float b = centred[i] - centred[i - 1];

                Assert.True(Math.Abs(a - b) < 0.001f, $"band {i}: the shift changed the shape, {a:F3} vs {b:F3} dB");
            }
        }

        [Fact]
        public void CentredCurveIsNotShifted()
        {
            var analyzer = new AudioAnalyzer();

            var profile = analyzer.CalculateProfile(SpectrumFrom(Flat(-30f)), SpectrumFrom(Tilt()), 48000, cutOnly: false);

            Assert.Equal(0f, profile.CutOnlyShiftDb);
        }

        [Fact]
        public void DefaultQIsTheOneTheEngineActuallyPlays()
        {
            var analyzer = new AudioAnalyzer();
            var profile = analyzer.CalculateProfile(SpectrumFrom(Flat(-30f)), SpectrumFrom(Tilt()), 48000);

            foreach (float q in profile.QFactors)
                Assert.Equal(AudioAnalyzer.NativeBandQ, q);
        }

        [Fact]
        public void ZeroFixedQFallsBackToThePerBandQs()
        {
            var analyzer = new AudioAnalyzer();
            var profile = analyzer.CalculateProfile(SpectrumFrom(Flat(-30f)), SpectrumFrom(Tilt()), 48000, fixedQ: 0f);

            bool varied = false;
            foreach (float q in profile.QFactors)
            {
                Assert.InRange(q, 2.5f, 8.0f);
                if (Math.Abs(q - profile.QFactors[0]) > 0.01f) varied = true;
            }

            Assert.True(varied, "every band came back with the same Q, the per band path did not run");
        }

        /// <summary>
        /// The solved gains, played through the real bank, must add up to the wanted curve.
        /// </summary>
        [Fact]
        public void SolvedGainsRealizeTheWantedCurve()
        {
            var analyzer = new AudioAnalyzer();
            var profile = analyzer.CalculateProfile(SpectrumFrom(Flat(-30f)), SpectrumFrom(Tilt()), 48000);

            for (int j = 0; j < Bands; j++)
            {
                float realized = 0f;
                for (int i = 0; i < Bands; i++)
                    realized += profile.BandGainsDb[i] * BellDb(_centres[i], AudioAnalyzer.NativeBandQ, _centres[j], 48000);

                Assert.True(Math.Abs(realized - profile.WantedCurveDb[j]) < 1.5f,
                    $"band {j}: the bank realizes {realized:F2} dB where the curve wants {profile.WantedCurveDb[j]:F2} dB");
            }
        }

        [Fact]
        public void CompressorSettingsStayInRange()
        {
            var analyzer = new AudioAnalyzer();
            var profile = analyzer.CalculateProfile(SpectrumFrom(Flat(-30f)), SpectrumFrom(Tilt()), 48000);

            Assert.InRange(profile.CompThresholdDb, -40f, -0.5f);
            Assert.InRange(profile.CompRatio, 1.2f, 6.0f);
            Assert.True(profile.SourceCrestDb > 0f);
            Assert.True(profile.TargetCrestDb > 0f);
        }

        [Fact]
        public void PresetTargetIsBuiltAndCached()
        {
            var analyzer = new AudioAnalyzer();

            var first = analyzer.GetPresetTargetSpectrum(PlaybackSystem.ClubPA);
            var second = analyzer.GetPresetTargetSpectrum(PlaybackSystem.ClubPA);

            Assert.NotSame(first, second);
            Assert.Equal(Bands, first.FrequencyBands.Length);
            Assert.True(first.RMSLevel > 0f);

            for (int i = 0; i < Bands; i++)
                Assert.Equal(first.FrequencyBands[i], second.FrequencyBands[i]);
        }

        [Fact]
        public void DifferentPresetsAskForDifferentThings()
        {
            var analyzer = new AudioAnalyzer();

            float[] club = analyzer.GetPresetTargetSpectrum(PlaybackSystem.ClubPA).FrequencyBands;
            float[] concert = analyzer.GetPresetTargetSpectrum(PlaybackSystem.ConcertPA).FrequencyBands;

            float lowDiff = Db(club[1]) - Db(concert[1]);

            Assert.True(lowDiff > 1.0f,
                $"the club preset only has {lowDiff:F2} dB more low end than the concert one - the curves are not getting baked in");
        }

        /// <summary>
        /// The declared preset curve has to be what the baked target actually measures -
        /// that is the whole point of a preset. Compared between two presets so whatever
        /// tilt the base sample has of its own cancels out.
        /// </summary>
        [Fact]
        public void PresetCurveLandsInTheTarget()
        {
            var analyzer = new AudioAnalyzer();
            var presets = AudioAnalyzer.GetAvailablePresets();

            float[] club = analyzer.GetPresetTargetSpectrum(PlaybackSystem.ClubPA).FrequencyBands;
            float[] studio = analyzer.GetPresetTargetSpectrum(PlaybackSystem.StudioMonitors).FrequencyBands;

            float[] measured = new float[Bands];
            float[] declared = new float[Bands];
            float measuredOffset = 0f, declaredOffset = 0f;

            for (int i = 0; i < Bands; i++)
            {
                measured[i] = Db(club[i]) - Db(studio[i]);
                declared[i] = presets[PlaybackSystem.ClubPA].FrequencyResponse[i]
                            - presets[PlaybackSystem.StudioMonitors].FrequencyResponse[i];

                measuredOffset += measured[i];
                declaredOffset += declared[i];
            }

            measuredOffset /= Bands;
            declaredOffset /= Bands;

            for (int i = 0; i < Bands; i++)
            {
                float err = (measured[i] - measuredOffset) - (declared[i] - declaredOffset);

                Assert.True(Math.Abs(err) < 2.0f,
                    $"{_centres[i]}Hz: the baked target is {err:F2} dB off the declared preset difference");
            }
        }

        /// <summary>
        /// The baked target has to carry the preset's own loudness, otherwise the AGC
        /// downstream has nothing to chase.
        /// </summary>
        [Theory]
        [InlineData(PlaybackSystem.ClubPA)]
        [InlineData(PlaybackSystem.StudioMonitors)]
        [InlineData(PlaybackSystem.RadioBroadcast)]
        public void PresetTargetCarriesItsLoudness(PlaybackSystem system)
        {
            var analyzer = new AudioAnalyzer();
            float wanted = AudioAnalyzer.GetAvailablePresets()[system].TargetLoudness;

            float measured = analyzer.GetPresetTargetSpectrum(system).LoudnessStats!.IntegratedLufs;

            Assert.True(Math.Abs(measured - wanted) < 1.5f,
                $"{system}: baked to {measured:F1} LUFS where the preset asks for {wanted:F1} LUFS");
        }

        /// <summary>
        /// The preset overload stamps the AGC block from the preset itself, not from the
        /// measured target.
        /// </summary>
        [Fact]
        public void PresetProfileTakesItsAgcFromThePreset()
        {
            var analyzer = new AudioAnalyzer();
            var amp = AudioAnalyzer.GetAvailablePresets()[PlaybackSystem.ClubPA].DynamicAmp;

            var profile = analyzer.CalculateProfile(SpectrumFrom(Flat(-30f)), PlaybackSystem.ClubPA, 48000);

            Assert.Equal(amp.TargetLevel, profile.TargetLoudness);
            Assert.Equal(amp.MaxGain, profile.MaxGain);
            Assert.Equal(amp.AttackTime, profile.AmpAttackSeconds);
            Assert.Equal(amp.ReleaseTime, profile.AmpReleaseSeconds);
        }

        /// <summary>
        /// BS.1770's own calibration point: a full scale 997 Hz sine on one channel reads
        /// -3.01 LUFS, at either rate - the same number the rider reads.
        /// </summary>
        [Theory]
        [InlineData(48000)]
        [InlineData(44100)]
        public void FullScaleSineReadsMinus3Lufs(int sampleRate)
        {
            float[] sine = new float[sampleRate * 5];
            for (int i = 0; i < sine.Length; i++)
                sine[i] = MathF.Sin(2f * MathF.PI * 997f * i / sampleRate);

            LoudnessInfo info = new AudioAnalyzer().MeasureLoudness(sine, sampleRate, 1);

            Assert.InRange(info.IntegratedLufs, -3.11f, -2.91f);
            Assert.InRange(info.TruePeakDbtp, -0.1f, 0.1f);
            Assert.Equal(-60f, info.SideToMidDb);
        }

        /// <summary>
        /// A tone at a quarter of the rate sampled 45 degrees off its crest: every sample sits
        /// 3 dB under the real peak, the true peak has to find it.
        /// </summary>
        [Fact]
        public void TruePeakSeesBetweenTheSamples()
        {
            float[] tone = new float[48000];
            for (int i = 0; i < tone.Length; i++)
                tone[i] = 0.5f * MathF.Sin(MathF.PI / 2f * i + MathF.PI / 4f);

            LoudnessInfo info = new AudioAnalyzer().MeasureLoudness(tone, 48000, 1);

            Assert.True(info.TruePeakDbtp > -6.02f - 0.6f,
                $"true peak read {info.TruePeakDbtp:F2} dBTP, the sample peak alone is {Db(0.5f * 0.7071f):F2}");
        }

        /// <summary>
        /// Ten seconds 10 dB apart: the loudness range is the gap, the floor is the quiet half.
        /// </summary>
        [Fact]
        public void LoudnessRangeReadsTheGap()
        {
            const int rate = 48000;
            float[] noise = new float[rate * 40];
            var rng = new Random(99);

            for (int i = 0; i < noise.Length; i++)
            {
                float level = (i / (rate * 10)) % 2 == 0 ? 0.3f : 0.3f * MathF.Pow(10f, -10f / 20f);
                noise[i] = (float)(rng.NextDouble() * 2.0 - 1.0) * level;
            }

            LoudnessInfo info = new AudioAnalyzer().MeasureLoudness(noise, rate, 1);

            Assert.InRange(info.LoudnessRangeLu, 8.5f, 10.5f);
            Assert.True(info.NoiseFloorLufs < info.IntegratedLufs - 5f,
                $"floor {info.NoiseFloorLufs:F1} LUFS against {info.IntegratedLufs:F1} LUFS integrated");
        }

        [Fact]
        public void BufferAnalysisCarriesTheLoudness()
        {
            var spectrum = new AudioAnalyzer().AnalyzeAudioBuffer(Noise(48000, 2), 48000, 2);

            Assert.NotNull(spectrum.LoudnessStats);
            Assert.InRange(spectrum.LoudnessStats!.IntegratedLufs, -30f, -5f);
            Assert.Equal(-60f, spectrum.LoudnessStats.SideToMidDb);
        }

        /// <summary>
        /// A source with more peak over its loudness than the target gets compressed by that
        /// much and no more: the range is the excess, and the ratio takes it off the peak.
        /// </summary>
        [Fact]
        public void CompressorTakesThePeakToLoudnessExcess()
        {
            var analyzer = new AudioAnalyzer();

            var source = WithLoudness(SpectrumFrom(Flat(-30f)), lufs: -20f, truePeak: -2f, range: 8f);
            var target = WithLoudness(SpectrumFrom(Flat(-30f)), lufs: -10f, truePeak: -1f, range: 5f);

            var profile = analyzer.CalculateProfile(source, target, 48000, cutOnly: false);
            OwnCompressorSettings comp = profile.Compressor;

            float excess = (-2f - -20f) - (-1f - -10f);
            float over = -2f - comp.ThresholdDb;
            float reduction = over * (1f - 1f / comp.Ratio);

            Assert.Equal(excess + 2f, comp.RangeDb, 3);
            Assert.True(comp.ThresholdDb > -20f, $"threshold {comp.ThresholdDb:F1} dB sits under the source's loudness");
            Assert.True(Math.Abs(reduction - Math.Min(excess, over * 0.85f)) < 0.01f,
                $"the ratio takes {reduction:F2} dB off the peak where the match asks {excess:F2}");
            Assert.Equal(profile.CompThresholdDb, comp.ThresholdDb);
            Assert.Equal(profile.CompRatio, comp.Ratio);
        }

        [Fact]
        public void DenserSourceOnlyGetsItsTopPeaksCaught()
        {
            var analyzer = new AudioAnalyzer();

            var source = WithLoudness(SpectrumFrom(Flat(-30f)), lufs: -9f, truePeak: -1f, range: 4f);
            var target = WithLoudness(SpectrumFrom(Flat(-30f)), lufs: -16f, truePeak: -1f, range: 8f);

            OwnCompressorSettings comp = analyzer.CalculateProfile(source, target, 48000).Compressor;

            Assert.Equal(1.5f, comp.Ratio);
            Assert.Equal(2f, comp.RangeDb);
            Assert.Equal(-5f, comp.ThresholdDb);
        }

        /// <summary>
        /// The rider goes for the target's integrated loudness, starts at the difference, and
        /// on a cut-only curve gives the shift back on top of it.
        /// </summary>
        [Fact]
        public void LevelerStartsAtTheLoudnessDifference()
        {
            var analyzer = new AudioAnalyzer();

            var source = WithLoudness(SpectrumFrom(Flat(-30f)), lufs: -20f, truePeak: -3f, range: 6f);
            var target = WithLoudness(SpectrumFrom(Tilt()), lufs: -11f, truePeak: -0.5f, range: 5f);

            var centred = analyzer.CalculateProfile(source, target, 48000, cutOnly: false);
            var cutOnly = analyzer.CalculateProfile(source, target, 48000, cutOnly: true);

            Assert.Equal(-11f, centred.Leveler.TargetLoudness);
            Assert.Equal(-11f, centred.TargetLoudness);
            Assert.Equal(9f, centred.Leveler.InitialGainDb, 3);
            Assert.Equal(-1f, centred.Leveler.CeilingDbtp);
            Assert.True(centred.Leveler.MaxBoostDb >= centred.Leveler.InitialGainDb);

            Assert.Equal(9f + cutOnly.CutOnlyShiftDb, cutOnly.Leveler.InitialGainDb, 3);
            Assert.True(cutOnly.Leveler.MaxBoostDb >= cutOnly.Leveler.InitialGainDb);
        }

        /// <summary>
        /// More loudness range than the target: a shorter memory and faster rates, so the rider
        /// takes the extra range out. Less: a long memory that only sets the level.
        /// </summary>
        [Fact]
        public void LevelerWindowFollowsTheLoudnessRange()
        {
            var analyzer = new AudioAnalyzer();
            var target = WithLoudness(SpectrumFrom(Flat(-30f)), lufs: -14f, truePeak: -1f, range: 4f);

            var wide = analyzer.CalculateProfile(
                WithLoudness(SpectrumFrom(Flat(-30f)), lufs: -18f, truePeak: -2f, range: 12f), target, 48000).Leveler;
            var tight = analyzer.CalculateProfile(
                WithLoudness(SpectrumFrom(Flat(-30f)), lufs: -18f, truePeak: -2f, range: 3f), target, 48000).Leveler;

            Assert.True(wide.WindowSeconds < tight.WindowSeconds,
                $"window {wide.WindowSeconds:F1}s on the dynamic source against {tight.WindowSeconds:F1}s on the tight one");
            Assert.True(wide.RiseRateDbPerSec > tight.RiseRateDbPerSec);
            Assert.True(wide.ToleranceDb < tight.ToleranceDb);
        }

        [Fact]
        public void BassHeavySourceRaisesTheKeyHighPass()
        {
            var analyzer = new AudioAnalyzer();
            var target = SpectrumFrom(Flat(-30f));

            float[] heavy = Flat(-40f);
            for (int i = 0; i < 7; i++) heavy[i] = -20f;

            float flatKey = analyzer.CalculateProfile(SpectrumFrom(Flat(-30f)), target, 48000).Compressor.SidechainHighPassHz;
            float heavyKey = analyzer.CalculateProfile(SpectrumFrom(heavy), target, 48000).Compressor.SidechainHighPassHz;

            Assert.True(heavyKey > flatKey + 30f, $"key high-pass {heavyKey:F0} Hz on the bass heavy source, {flatKey:F0} Hz on the flat one");
        }

        /// <summary>
        /// Stereo sources go mid/side, and a source wider than the target gets a looser link so
        /// its side is held on its own.
        /// </summary>
        [Fact]
        public void WideSourceLoosensTheStereoLink()
        {
            var analyzer = new AudioAnalyzer();
            var target = WithLoudness(SpectrumFrom(Flat(-30f)), lufs: -14f, truePeak: -1f, range: 6f, sideToMid: -12f);

            var wide = analyzer.CalculateProfile(
                WithLoudness(SpectrumFrom(Flat(-30f)), lufs: -14f, truePeak: -1f, range: 6f, sideToMid: -4f), target, 48000).Compressor;
            var mono = analyzer.CalculateProfile(
                WithLoudness(SpectrumFrom(Flat(-30f)), lufs: -14f, truePeak: -1f, range: 6f, sideToMid: -60f), target, 48000).Compressor;

            Assert.Equal(OwnCompressorChannelMode.MidSide, wide.ChannelMode);
            Assert.True(wide.StereoLink < 0.8f);
            Assert.Equal(OwnCompressorChannelMode.LeftRight, mono.ChannelMode);
            Assert.Equal(1f, mono.StereoLink);
        }

        [Fact]
        public void SettingsReachTheLiveEffects()
        {
            var analyzer = new AudioAnalyzer();
            var profile = analyzer.CalculateProfile(
                WithLoudness(SpectrumFrom(Flat(-30f)), lufs: -20f, truePeak: -2f, range: 8f, sideToMid: -8f),
                WithLoudness(SpectrumFrom(Tilt()), lufs: -10f, truePeak: -1f, range: 5f, sideToMid: -10f), 48000);

            using var compressor = new OwnCompressorEffect();
            using var leveler = new OwnDynamicAmpEffect();

            profile.Compressor.ApplyTo(compressor);
            profile.Leveler.ApplyTo(leveler);

            Assert.Equal(profile.Compressor.ThresholdDb, compressor.Threshold);
            Assert.Equal(profile.Compressor.Ratio, compressor.Ratio);
            Assert.Equal(profile.Compressor.RangeDb, compressor.Range);
            Assert.Equal(profile.Compressor.ChannelMode, compressor.ChannelMode);
            Assert.Equal(profile.Compressor.SidechainHighPassHz, compressor.SidechainHighPass);

            Assert.Equal(profile.Leveler.TargetLoudness, leveler.TargetLoudness);
            Assert.Equal(profile.Leveler.WindowSeconds, leveler.Window);
            Assert.Equal(profile.Leveler.CeilingDbtp, leveler.Ceiling);
            Assert.Equal(profile.Leveler.InitialGainDb, leveler.InitialGain);
        }

        /// <summary>
        /// The whole render: a quiet, peaky source matched to a loud, dense target has to come
        /// out at the target's integrated loudness with its true peak under the rider's ceiling.
        /// </summary>
        [Fact]
        public void RenderLandsOnTheTargetLoudnessUnderTheCeiling()
        {
            const int rate = 48000;
            const int seconds = 24;
            var rng = new Random(7);

            float[] source = new float[rate * seconds * 2];
            float[] target = new float[rate * seconds * 2];

            float sLow = 0f, tLow = 0f;

            for (int f = 0; f < rate * seconds; f++)
            {
                sLow = 0.9f * sLow + 0.1f * (float)(rng.NextDouble() * 2.0 - 1.0);
                tLow = 0.9f * tLow + 0.1f * (float)(rng.NextDouble() * 2.0 - 1.0);

                float hit = f % (rate / 2) < 480 ? 6f : 1f;
                float s = sLow * 0.25f * hit;
                float t = tLow * 1.5f;

                source[f * 2] = s; source[f * 2 + 1] = s;
                target[f * 2] = t; target[f * 2 + 1] = t;
            }

            string sourcePath = Path.Combine(_dir, "source.wav");
            string targetPath = Path.Combine(_dir, "target.wav");
            string outputPath = Path.Combine(_dir, "output.wav");

            WaveFile.Create(sourcePath, source, rate, 2, 24);
            WaveFile.Create(targetPath, target, rate, 2, 24);

            var analyzer = new AudioAnalyzer();
            analyzer.ProcessEQMatching(sourcePath, targetPath, outputPath);

            LoudnessInfo wanted = analyzer.AnalyzeAudioFile(targetPath).LoudnessStats!;
            LoudnessInfo got = analyzer.AnalyzeAudioFile(outputPath).LoudnessStats!;
            float ceiling = Math.Clamp(wanted.TruePeakDbtp, -3f, -1f);

            Assert.True(Math.Abs(got.IntegratedLufs - wanted.IntegratedLufs) < 1.0f,
                $"rendered to {got.IntegratedLufs:F1} LUFS where the target is {wanted.IntegratedLufs:F1} LUFS");
            Assert.True(got.TruePeakDbtp <= ceiling + 0.3f,
                $"true peak {got.TruePeakDbtp:F2} dBTP over the {ceiling:F1} dBTP ceiling");
        }

        /// <summary>
        /// The older five-field compressor and AGC blocks and the new effect setups describe the
        /// same system, so a caller on either one gets the same numbers.
        /// </summary>
        [Fact]
        public void PresetBlocksAgreeWithEachOther()
        {
            foreach (var (system, preset) in AudioAnalyzer.GetAvailablePresets())
            {
                Assert.Equal(preset.Compression.Threshold, preset.Compressor.ThresholdDb);
                Assert.Equal(preset.Compression.Ratio, preset.Compressor.Ratio);
                Assert.Equal(preset.Compression.AttackTime, preset.Compressor.AttackMs);
                Assert.Equal(preset.Compression.ReleaseTime, preset.Compressor.ReleaseMs);
                Assert.Equal(preset.Compression.MakeupGain, preset.Compressor.MakeupDb);

                Assert.Equal(preset.TargetLoudness, preset.Leveler.TargetLoudness);
                Assert.Equal(preset.DynamicAmp.TargetLevel, preset.Leveler.TargetLoudness);

                Assert.InRange(preset.Leveler.CeilingDbtp, -3f, 0f);
                Assert.InRange(preset.Compressor.RangeDb, 1f, 24f);
                Assert.True(preset.Leveler.RelativeGateLu < 0f, $"{system}: the relative gate has to sit under the programme");
            }
        }

        /// <summary>
        /// Against a preset the system's character wins where it is a property of the playback
        /// system, and the measurement stays where it is a property of the mix.
        /// </summary>
        [Fact]
        public void PresetProfileTakesTheSystemCharacter()
        {
            var analyzer = new AudioAnalyzer();
            var preset = AudioAnalyzer.GetAvailablePresets()[PlaybackSystem.ClubPA];
            var source = WithLoudness(SpectrumFrom(Flat(-30f)), lufs: -20f, truePeak: -2f, range: 8f, sideToMid: -10f);

            var profile = analyzer.CalculateProfile(source, PlaybackSystem.ClubPA, 48000, cutOnly: false);
            var target = analyzer.GetPresetTargetSpectrum(PlaybackSystem.ClubPA);
            var measured = analyzer.CalculateProfile(source, target, 48000, cutOnly: false);

            Assert.Equal(preset.Compressor.KneeDb, profile.Compressor.KneeDb);
            Assert.Equal(preset.Compressor.AttackMs, profile.Compressor.AttackMs);
            Assert.Equal(preset.Compressor.SidechainHighPassHz, profile.Compressor.SidechainHighPassHz);
            Assert.Equal(preset.Compressor.Detector, profile.Compressor.Detector);
            Assert.Equal(measured.Compressor.ThresholdDb, profile.Compressor.ThresholdDb);
            Assert.Equal(measured.Compressor.RangeDb, profile.Compressor.RangeDb);
            Assert.Equal(measured.Compressor.ChannelMode, profile.Compressor.ChannelMode);

            Assert.Equal(preset.Leveler.TargetLoudness, profile.Leveler.TargetLoudness);
            Assert.Equal(preset.Leveler.WindowSeconds, profile.Leveler.WindowSeconds);
            Assert.Equal(preset.Leveler.ToleranceDb, profile.Leveler.ToleranceDb);
            Assert.Equal(preset.Leveler.CeilingDbtp, profile.Leveler.CeilingDbtp);
            Assert.Equal(preset.Leveler.RelativeGateLu, profile.Leveler.RelativeGateLu);
            Assert.Equal(preset.Leveler.TargetLoudness - -20f, profile.Leveler.InitialGainDb, 3);
        }

        [Fact]
        public void BroadcastRidesHarderThanTheStudio()
        {
            var analyzer = new AudioAnalyzer();
            var source = WithLoudness(SpectrumFrom(Flat(-30f)), lufs: -20f, truePeak: -2f, range: 8f);

            var radio = analyzer.CalculateProfile(source, PlaybackSystem.RadioBroadcast, 48000).Leveler;
            var studio = analyzer.CalculateProfile(source, PlaybackSystem.StudioMonitors, 48000).Leveler;

            Assert.True(radio.WindowSeconds < studio.WindowSeconds);
            Assert.True(radio.RiseRateDbPerSec > studio.RiseRateDbPerSec);
            Assert.True(radio.RelativeGateLu < studio.RelativeGateLu, "the radio levels its quiet parts, the studio keeps them");
        }

        /// <summary>
        /// The baked target is held under the preset's own true-peak ceiling.
        /// </summary>
        [Theory]
        [InlineData(PlaybackSystem.ClubPA)]
        [InlineData(PlaybackSystem.Television)]
        public void PresetTargetStaysUnderItsCeiling(PlaybackSystem system)
        {
            float ceiling = AudioAnalyzer.GetAvailablePresets()[system].Leveler.CeilingDbtp;
            float peak = new AudioAnalyzer().GetPresetTargetSpectrum(system).LoudnessStats!.TruePeakDbtp;

            Assert.True(peak <= ceiling + 0.3f, $"{system}: baked to {peak:F2} dBTP over its {ceiling:F1} dBTP ceiling");
        }

        private static AudioSpectrum WithLoudness(AudioSpectrum spectrum, float lufs, float truePeak, float range,
            float sideToMid = -60f)
        {
            spectrum.LoudnessStats = new LoudnessInfo
            {
                IntegratedLufs = lufs,
                TruePeakDbtp = truePeak,
                LoudnessRangeLu = range,
                NoiseFloorLufs = lufs - range - 10f,
                SideToMidDb = sideToMid
            };

            return spectrum;
        }

        private static float[] Tilt()
        {
            float[] curve = new float[Bands];
            for (int i = 0; i < Bands; i++) curve[i] = -30f + (i - Bands / 2f) * 0.25f;

            return curve;
        }

        private static float Db(float linear) => 20f * MathF.Log10(Math.Max(linear, 1e-10f));

        /// <summary>
        /// RBJ peaking magnitude per dB of gain, the model the deconvolution uses.
        /// </summary>
        private static float BellDb(float centreFreq, float q, float atFreq, int sampleRate)
        {
            double a = Math.Pow(10.0, 1.0 / 40.0);
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
    }
}
