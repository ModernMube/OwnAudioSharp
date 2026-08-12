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
