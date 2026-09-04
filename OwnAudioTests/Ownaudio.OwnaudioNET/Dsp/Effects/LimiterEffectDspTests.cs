using System;
using Ownaudio.OwnaudioNET.Tests.Dsp.Harness;
using OwnaudioNET.Effects;

namespace Ownaudio.OwnaudioNET.Tests.Dsp.Effects;

/// <summary>
/// Level tests for the lookahead limiter. The gain law is simple on paper — pull the peak
/// down to the threshold, then clip anything left over at the ceiling — and every one of
/// these measures exactly that on a sine.
/// </summary>
public class LimiterEffectDspTests
{
    private const int Ch = EffectHarness.Channels;
    private const int Rate = EffectHarness.SampleRate;
    private const double Tone = 1000.0;

    private static LimiterEffect _limiter(float thresholdDb, float ceilingDb, float releaseMs = 50f, float lookaheadMs = 5f)
        => new LimiterEffect(Rate, thresholdDb, ceilingDb, releaseMs, lookaheadMs);

    private static double _peakOfSteadyTone(LimiterEffect fx, double inputDb, int seconds = 1)
    {
        int _frames = EffectHarness.SettleFrames + Rate * seconds;
        float[] _in = SignalGenerator.Sine(Tone, inputDb, _frames, Ch, Rate);
        float[] _out = EffectHarness.Render(fx, _in, Ch);

        return SignalMeasure.PeakDb(EffectHarness.Steady(_out, Ch));
    }

    /// <summary>
    /// The one that shipped broken in 4.0.1 — anything under the threshold has to come out
    /// at exactly the level it went in, not 12 dB below it.
    /// </summary>
    [Theory]
    [InlineData(-20.0)]
    [InlineData(-12.0)]
    [InlineData(-8.0)]
    public void QuietSignalPassesAtUnityGain(double inputDb)
    {
        using (LimiterEffect _fx = _limiter(-6.0f, -0.1f))
        {
            double _peak = _peakOfSteadyTone(_fx, inputDb);

            _peak.Should().BeApproximately(inputDb, 0.1,
                $"a {inputDb:F0} dBFS tone is below the -6 dB threshold and must pass untouched");
        }
    }

    /// <summary>
    /// Above the threshold the gain pulls the peak down to the threshold, and stops there.
    /// </summary>
    [Theory]
    [InlineData(-3.0, -6.0)]
    [InlineData(0.0, -6.0)]
    [InlineData(0.0, -12.0)]
    public void LoudSignalIsHeldAtTheThreshold(double inputDb, float thresholdDb)
    {
        using (LimiterEffect _fx = _limiter(thresholdDb, -0.1f))
        {
            double _peak = _peakOfSteadyTone(_fx, inputDb);

            _peak.Should().BeApproximately(thresholdDb, 0.4,
                $"a {inputDb:F0} dBFS tone into a {thresholdDb:F0} dB threshold should settle at the threshold");
        }
    }

    /// <summary>
    /// A ceiling tighter than the threshold takes over as a hard clip. The ceiling only
    /// goes down to -2 dB, so that is as low as this can be pushed.
    /// </summary>
    [Fact]
    public void CeilingBelowThresholdClampsTheOutput()
    {
        using (LimiterEffect _fx = _limiter(-0.5f, -2.0f))
        {
            double _peak = _peakOfSteadyTone(_fx, 0.0);

            _peak.Should().BeApproximately(-2.0, 0.15, "nothing may get past the -2 dB ceiling");
        }
    }

    /// <summary>
    /// Ceiling is a -2..0 dB control; asking for more just gets clamped. Worth pinning
    /// down so a caller expecting a -6 dB ceiling finds out here rather than in a mix.
    /// </summary>
    [Fact]
    public void CeilingIsClampedToItsDocumentedRange()
    {
        using (LimiterEffect _fx = _limiter(-6.0f, -0.1f))
        {
            _fx.Ceiling = -6.0f;
            _fx.Ceiling.Should().BeApproximately(-2.0f, 0.01f);

            _fx.Ceiling = 3.0f;
            _fx.Ceiling.Should().BeApproximately(0.0f, 0.01f);
        }
    }

    /// <summary>
    /// More level in means more gain reduction, never less.
    /// </summary>
    [Fact]
    public void GainReductionGrowsWithInputLevel()
    {
        double[] _inputs = { -6.0, -3.0, -1.0, 0.0 };
        double _previous = double.NegativeInfinity;

        foreach (double _in in _inputs)
        {
            using (LimiterEffect _fx = _limiter(-12.0f, -0.1f))
            {
                double _reduction = _in - _peakOfSteadyTone(_fx, _in);

                _reduction.Should().BeGreaterThan(_previous - 0.05,
                    $"gain reduction must not drop when the input rises to {_in:F0} dBFS");
                _previous = _reduction;
            }
        }
    }

    /// <summary>
    /// The whole point of lookahead: a jump from quiet to full scale must not produce a
    /// single sample over the threshold, not even on the first frame of the loud part.
    /// </summary>
    [Fact]
    public void LookaheadCatchesASuddenJumpWithoutOvershoot()
    {
        using (LimiterEffect _fx = _limiter(-6.0f, -0.1f, releaseMs: 50f, lookaheadMs: 8f))
        {
            int _frames = Rate * 2;
            float[] _in = SignalGenerator.SineStep(Tone, -24.0, 0.0, Rate, _frames, Ch, Rate);
            float[] _out = EffectHarness.Render(_fx, _in, Ch);

            double _peak = SignalMeasure.PeakDb(_out);

            _peak.Should().BeLessThan(-5.5,
                "an 8 ms lookahead has to have the gain down before the transient arrives");
        }
    }

    /// <summary>
    /// Reported latency has to be the lookahead in samples, otherwise delay compensation
    /// pulls the track out of line.
    /// </summary>
    [Theory]
    [InlineData(5.0f)]
    [InlineData(8.0f)]
    public void ReportedLatencyMatchesTheLookahead(float lookaheadMs)
    {
        using (LimiterEffect _fx = _limiter(-6.0f, -0.1f, lookaheadMs: lookaheadMs))
        {
            _fx.Initialize(EffectHarness.Config());

            int _expected = (int)(lookaheadMs * Rate / 1000.0);
            _fx.LatencySamples.Should().BeCloseTo(_expected, 2);
        }
    }

    /// <summary>
    /// And the reported latency has to be the delay the samples actually see. Matchering
    /// renders through this limiter and then slides the whole file back by LatencySamples;
    /// if the two disagree the master comes out offset.
    /// </summary>
    [Theory]
    [InlineData(5.0f)]
    [InlineData(8.0f)]
    public void ReportedLatencyIsTheDelayTheSignalActuallySees(float lookaheadMs)
    {
        using (LimiterEffect _fx = _limiter(-6.0f, -0.1f, lookaheadMs: lookaheadMs))
        {
            _fx.Initialize(EffectHarness.Config());

            //-20 dB is well under the threshold, so nothing moves the impulse but the delay line
            float[] _in = SignalGenerator.Impulse(Rate / 2, Ch, levelDb: -20.0);
            float[] _out = EffectHarness.Render(_fx, _in, Ch);

            int _peakFrame = 0;
            float _peak = 0f;
            for (int f = 0; f < _out.Length / Ch; f++)
            {
                float _abs = Math.Abs(_out[f * Ch]);
                if (_abs > _peak) { _peak = _abs; _peakFrame = f; }
            }

            _peak.Should().BeGreaterThan(0.05f, "a quiet impulse has to survive the limiter");
            _peakFrame.Should().BeCloseTo(_fx.LatencySamples, 2,
                $"the impulse came out at frame {_peakFrame}, LatencySamples says {_fx.LatencySamples}");
        }
    }

    /// <summary>
    /// Limiting a sine should stay reasonably clean — this is a limiter, not a clipper.
    /// </summary>
    [Fact]
    public void ModerateLimitingStaysLowDistortion()
    {
        using (LimiterEffect _fx = _limiter(-6.0f, -0.1f))
        {
            int _frames = EffectHarness.SettleFrames + Rate;
            float[] _in = SignalGenerator.Sine(Tone, -3.0, _frames, Ch, Rate);
            float[] _out = EffectHarness.Render(_fx, _in, Ch);

            double _thd = SignalMeasure.Thd(EffectHarness.Steady(_out, Ch), Tone, Rate);

            _thd.Should().BeLessThan(0.02, "3 dB of gain reduction on a steady tone should be nearly transparent");
        }
    }
}
