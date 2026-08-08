using OwnaudioNET.Features.Matchering;
using OwnaudioNET.Recording;
using Xunit;
using System;
using System.IO;

namespace Ownaudio.Test.OwnaudioNET.Features
{
    /// <summary>
    /// Calibration checks on the matching analyzer. The band readings have to be
    /// absolute and rate independent, otherwise every delta it computes is biased.
    /// </summary>
    public class MatcheringAnalysisTests : IDisposable
    {
        private const int Bands = 30;
        private const int Band1kHz = 17;
        private const float Duration = 14.0f;

        private readonly string _dir;

        public MatcheringAnalysisTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ownaudio-match-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        /// <summary>
        /// Writes a mono sine to a temp wav and hands back the path.
        /// </summary>
        private string WriteSine(string name, float frequency, float amplitude, int sampleRate)
        {
            int frames = (int)(Duration * sampleRate);
            float[] samples = new float[frames];

            for (int i = 0; i < frames; i++)
                samples[i] = amplitude * MathF.Sin(2.0f * MathF.PI * frequency * i / sampleRate);

            string path = Path.Combine(_dir, name);
            WaveFile.Create(path, samples, sampleRate, 1, 24);

            return path;
        }

        /// <summary>
        /// Same seed at both rates, so the only thing that can move the reading is the
        /// analysis itself.
        /// </summary>
        private string WriteNoise(string name, int sampleRate)
        {
            int frames = (int)(Duration * sampleRate);
            float[] samples = new float[frames];
            var rng = new Random(1234);

            for (int i = 0; i < frames; i++)
                samples[i] = (float)(rng.NextDouble() * 2.0 - 1.0) * 0.3f;

            string path = Path.Combine(_dir, name);
            WaveFile.Create(path, samples, sampleRate, 1, 24);

            return path;
        }

        private static float Db(float linear) => 20f * MathF.Log10(Math.Max(linear, 1e-10f));

        [Fact]
        public void BandLevel_MatchesTheActualSineLevel()
        {
            var analyzer = new AudioAnalyzer();
            string path = WriteSine("sine.wav", 1000f, 0.5f, 48000);

            AudioSpectrum spectrum = analyzer.AnalyzeAudioFile(path);

            float expectedDb = Db(0.5f / MathF.Sqrt(2f));
            float measuredDb = Db(spectrum.FrequencyBands[Band1kHz]);

            Assert.True(Math.Abs(measuredDb - expectedDb) < 1.5f,
                $"1kHz band read {measuredDb:F2} dB, expected about {expectedDb:F2} dB");
        }

        [Fact]
        public void EnergyStaysInItsOwnBand()
        {
            var analyzer = new AudioAnalyzer();
            string path = WriteSine("sine.wav", 1000f, 0.5f, 48000);

            AudioSpectrum spectrum = analyzer.AnalyzeAudioFile(path);
            float centre = Db(spectrum.FrequencyBands[Band1kHz]);

            for (int i = 0; i < Bands; i++)
            {
                if (Math.Abs(i - Band1kHz) <= 1) continue;

                float leak = Db(spectrum.FrequencyBands[i]);
                Assert.True(leak < centre - 20f,
                    $"band {i} leaked to {leak:F1} dB against a {centre:F1} dB centre");
            }
        }

        [Fact]
        public void BroadbandReadingIsIndependentOfSampleRate()
        {
            var analyzer = new AudioAnalyzer();
            string at44 = WriteNoise("a.wav", 44100);
            string at48 = WriteNoise("b.wav", 48000);

            float[] a = analyzer.AnalyzeAudioFile(at44).FrequencyBands;
            float[] b = analyzer.AnalyzeAudioFile(at48).FrequencyBands;

            for (int i = 10; i < Bands - 2; i++)
            {
                float diff = Db(a[i]) - Db(b[i]);
                Assert.True(Math.Abs(diff) < 1.0f,
                    $"band {i}: 44.1k read {Db(a[i]):F2} dB but 48k read {Db(b[i]):F2} dB - the two files are not measured alike");
            }
        }

        [Fact]
        public void BroadbandBandLevelsFollowBandwidth()
        {
            var analyzer = new AudioAnalyzer();
            float[] bands = analyzer.AnalyzeAudioFile(WriteNoise("n.wav", 48000)).FrequencyBands;

            for (int i = 11; i < Bands - 2; i++)
            {
                float octaves = MathF.Log2(BandCentre(i) / BandCentre(i - 1));
                float expected = 10f * MathF.Log10(MathF.Pow(2f, octaves));
                float actual = Db(bands[i]) - Db(bands[i - 1]);

                Assert.True(Math.Abs(actual - expected) < 1.5f,
                    $"band {i} rose {actual:F2} dB over its neighbour, expected about {expected:F2} dB for white noise");
            }
        }

        private static float BandCentre(int band)
        {
            float[] centres = {
                20f, 25f, 31.5f, 40f, 50f, 63f, 80f, 100f, 125f, 160f,
                200f, 250f, 315f, 400f, 500f, 630f, 800f, 1000f, 1250f, 1600f,
                2000f, 2500f, 3150f, 4000f, 5000f, 6300f, 8000f, 10000f, 12500f, 16000f
            };
            return centres[band];
        }

        [Fact]
        public void LowBandsStillRead()
        {
            var analyzer = new AudioAnalyzer();
            string path = WriteSine("low.wav", 25f, 0.5f, 48000);

            AudioSpectrum spectrum = analyzer.AnalyzeAudioFile(path);
            float measuredDb = Db(spectrum.FrequencyBands[1]);

            Assert.True(measuredDb > -20f,
                $"the 25Hz band read {measuredDb:F1} dB - too few bins to measure anything");
        }
    }
}
