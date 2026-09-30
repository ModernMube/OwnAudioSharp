using System;
using FluentAssertions;
using Ownaudio.Safe.Effects;
using OwnaudioNET.Features.Matchering;
using Xunit;

namespace Ownaudio.Test.OwnaudioNET.Features
{
    /// <summary>
    /// The mastering render goes straight to the engine now, so these check the two things
    /// that reach it only because the chain asks for them by param id. Both were silently
    /// dropped while the render went through the managed effects, and neither shows up as a
    /// crash - just a different master.
    /// </summary>
    public class MatcheringChainTests
    {
        private const int Rate = 48000;
        private const int Ch = 2;

        private static float[] _tone(double freq, int frames, float amp = 0.25f)
        {
            float[] buf = new float[frames * Ch];
            for (int f = 0; f < frames; f++)
            {
                float v = amp * MathF.Sin((float)(2.0 * Math.PI * freq * f / Rate));
                buf[f * Ch] = v;
                buf[f * Ch + 1] = v;
            }
            return buf;
        }

        private static double _rmsDb(float[] buf, int fromFrame)
        {
            double sum = 0;
            int n = 0;
            for (int i = fromFrame * Ch; i < buf.Length; i++, n++) sum += buf[i] * (double)buf[i];
            return 20.0 * Math.Log10(Math.Sqrt(sum / n) + 1e-12);
        }

        private static float[] _flatBands(float value)
        {
            float[] a = new float[30];
            Array.Fill(a, value);
            return a;
        }

        private static readonly float[] IsoCentres = {
            20f, 25f, 31.5f, 40f, 50f, 63f, 80f, 100f, 125f, 160f,
            200f, 250f, 315f, 400f, 500f, 630f, 800f, 1000f, 1250f, 1600f,
            2000f, 2500f, 3150f, 4000f, 5000f, 6300f, 8000f, 10000f, 12500f, 16000f
        };

        /// <summary>
        /// The analyzer picks a Q per band (2.5 - 8) and deconvolves the band gains against
        /// exactly those widths. Feed the same gains into a fixed-Q filterbank and the curve
        /// that comes out is not the one it solved for.
        /// </summary>
        [Fact]
        public void Equalizer30_HonoursThePerBandQ()
        {
            float[] gains = _flatBands(0f);
            gains[17] = 12.0f;   // 1 kHz

            double SpillAt1250(float q)
            {
                float[] qs = _flatBands(q);
                using StandaloneEffect eq = NativeMastering.Equalizer30(Rate, Ch, IsoCentres, qs, gains);

                float[] neighbour = _tone(1250.0, 9600);
                double dry = _rmsDb(neighbour, 4800);
                eq.Process(neighbour, 9600);
                return _rmsDb(neighbour, 4800) - dry;
            }

            double wide = SpillAt1250(2.5f);
            double narrow = SpillAt1250(8.0f);

            wide.Should().BeGreaterThan(narrow + 2.0,
                $"a Q of 2.5 has to spill onto the neighbouring band far more than 8.0 does "
                + $"({wide:F2} dB against {narrow:F2} dB)");
        }

        /// <summary>
        /// And the centre goes with it - SetBandGain took a frequency all along, it just had
        /// nowhere to land.
        /// </summary>
        [Fact]
        public void Equalizer30_HonoursTheBandCentre()
        {
            float[] gains = _flatBands(0f);
            gains[0] = 12.0f;

            float[] moved = (float[])IsoCentres.Clone();
            moved[0] = 5000f;

            using StandaloneEffect eq = NativeMastering.Equalizer30(
                Rate, Ch, moved, _flatBands(4.318474f), gains);

            float[] buf = _tone(5000.0, 9600);
            double dry = _rmsDb(buf, 4800);
            eq.Process(buf, 9600);

            (_rmsDb(buf, 4800) - dry).Should().BeGreaterThan(6.0,
                "band 0 retuned to 5 kHz has to lift a 5 kHz tone");
        }

        /// <summary>
        /// The render attenuates the file to make room for the EQ boosts and expects the
        /// rider to open back up from the inverse. Left at unity it may only climb 12 dB a
        /// second, so the head of the master comes out quiet.
        /// </summary>
        [Fact]
        public void OwnDynamicAmp_StartsFromTheSeededGain()
        {
            using StandaloneEffect amp = NativeMastering.OwnDynamicAmp(Rate, Ch, new OwnDynamicAmpSettings
            {
                TargetLoudness = -14f,
                MaxBoostDb = 12f,
                RiseRateDbPerSec = 1.5f,
                InitialGainDb = 6f
            });

            int frames = 2 * NativeMastering.BlockFrames;
            float[] buf = _tone(1000.0, frames, amp: 0.05f);
            double dry = _rmsDb(buf, NativeMastering.BlockFrames);
            NativeMastering.Render(buf, Ch, new[] { amp });

            (_rmsDb(buf, NativeMastering.BlockFrames) - dry).Should().BeApproximately(6.0, 1.0,
                "the first ~20 ms have to come out at the seeded +6 dB, not ramp up to it at 1.5 dB/s");
        }

        /// <summary>
        /// Range is what keeps the matched compressor from squashing past the peak to loudness
        /// excess it was set up for, however far over the threshold the programme goes.
        /// </summary>
        [Fact]
        public void OwnCompressor_StopsAtTheRange()
        {
            using StandaloneEffect comp = NativeMastering.OwnCompressor(Rate, Ch, new OwnCompressorSettings
            {
                ThresholdDb = -40f,
                Ratio = 6f,
                KneeDb = 0f,
                AttackMs = 1f,
                LookaheadMs = 0f,
                RangeDb = 3f
            });

            float[] buf = _tone(1000.0, Rate / 2, amp: 0.5f);
            double dry = _rmsDb(buf, Rate / 4);
            NativeMastering.Render(buf, Ch, new[] { comp });

            (_rmsDb(buf, Rate / 4) - dry).Should().BeApproximately(-3.0, 0.5,
                "30 dB over the threshold at 6:1 would take 25 dB, the range allows 3");
        }

        /// <summary>
        /// The rider's limiter holds the true peak, not just the sample peak: a tone at a
        /// quarter of the rate sampled 45 degrees off its crest never shows its real top.
        /// </summary>
        [Fact]
        public void TruePeakLimiter_HoldsTheInterSamplePeak()
        {
            int frames = Rate / 2;
            float[] buf = new float[frames * Ch];
            for (int f = 0; f < frames; f++)
            {
                float v = 0.99f * MathF.Sin(MathF.PI / 2f * f + MathF.PI / 4f);
                buf[f * Ch] = v;
                buf[f * Ch + 1] = v;
            }

            using StandaloneEffect limiter = NativeMastering.TruePeakLimiter(Rate, Ch, -1f);
            NativeMastering.Render(buf, Ch, new[] { limiter });

            var analyzer = new AudioAnalyzer();
            float[] settled = buf.AsSpan(Rate / 4 * Ch).ToArray();

            analyzer.MeasureLoudness(settled, Rate, Ch).TruePeakDbtp.Should().BeLessThan(-0.7f,
                "the sample peak is 3 dB under the real one, the limiter has to see through that");
        }

        /// <summary>
        /// The latency compensation has to add up every look-ahead in the chain.
        /// </summary>
        [Fact]
        public void CompensateLatency_RealignsTheWholeChain()
        {
            using StandaloneEffect comp = NativeMastering.OwnCompressor(Rate, Ch, new OwnCompressorSettings
            {
                ThresholdDb = 0f, Ratio = 1f, LookaheadMs = 2f, RangeDb = 0f
            });
            using StandaloneEffect amp = NativeMastering.TruePeakLimiter(Rate, Ch, 0f);
            var chain = new[] { comp, amp };

            int frames = 4800;
            float[] buf = new float[frames * Ch];
            buf[1000 * Ch] = 0.5f;
            buf[1000 * Ch + 1] = 0.5f;

            NativeMastering.Render(buf, Ch, chain);
            NativeMastering.CompensateLatency(buf, buf.Length, Ch, chain);

            int loudest = 0;
            for (int f = 0; f < frames; f++)
                if (Math.Abs(buf[f * Ch]) > Math.Abs(buf[loudest * Ch])) loudest = f;

            loudest.Should().Be(1000, "the click has to land back where it was in the source");
        }

        /// <summary>
        /// Render walks the chain in order over the whole buffer; a bypassed-looking chain of
        /// one flat EQ must leave the signal where it was.
        /// </summary>
        [Fact]
        public void Render_WalksTheWholeBufferWithoutColouringAFlatChain()
        {
            using StandaloneEffect eq = NativeMastering.Equalizer30(
                Rate, Ch, IsoCentres, _flatBands(4.318474f), _flatBands(0f));

            float[] buf = _tone(1000.0, 4000);
            float[] before = (float[])buf.Clone();

            int blocks = 0;
            NativeMastering.Render(buf, Ch, new[] { eq }, _ => blocks++);

            blocks.Should().Be(8, "4000 frames in 512-frame blocks is 7 full plus a short one");
            for (int i = 0; i < buf.Length; i++)
                buf[i].Should().BeApproximately(before[i], 1e-4f, "a flat EQ must not colour anything");
        }
    }
}
