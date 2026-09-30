using System;
using Ownaudio.OwnaudioNET.Tests.Dsp.Harness;
using OwnaudioNET.Effects;

namespace Ownaudio.OwnaudioNET.Tests.Dsp.Effects;

/// <summary>
/// The tape delay with its loop filters wide open, so the repeats can be timed and
/// measured against plain numbers: where the echo lands, how much each repeat loses,
/// and that freeze keeps the loop spinning.
/// </summary>
public class OwnDelayEffectDspTests
{
    private const int Ch = EffectHarness.Channels;
    private const int Rate = EffectHarness.SampleRate;
    private const double Tone = 1000.0;
    private const int BurstFrames = Rate / 50;

    private static OwnDelayEffect _delay(float timeMs, float feedback)
    {
        var _fx = new OwnDelayEffect();
        _fx.TimeLeft = timeMs;
        _fx.TimeRight = timeMs;
        _fx.Feedback = feedback;
        _fx.Mix = 1f;
        _fx.LowCut = 20f;
        _fx.HighCut = 20000f;
        return _fx;
    }

    private static float[] _burst(int totalFrames)
        => SignalGenerator.SineBurst(Tone, -6.0, BurstFrames, totalFrames, Ch, Rate);

    /// <summary>
    /// The first repeat lands on the sample the time says, diffusers and all.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(250)]
    [InlineData(375)]
    public void FirstEchoArrivesAtTheSetTime(int timeMs)
    {
        using (OwnDelayEffect _fx = _delay(timeMs, 0f))
        {
            float[] _in = _burst(Rate);
            float[] _out = EffectHarness.Render(_fx, _in, Ch);

            int _expected = timeMs * Rate / 1000;
            int _found = SignalMeasure.DelaySamples(
                SignalMeasure.Channel(_in, Ch, 0), SignalMeasure.Channel(_out, Ch, 0), BurstFrames * 2, _expected * 2);

            _found.Should().BeCloseTo(_expected, 2u,
                $"a {timeMs} ms delay puts the echo {_expected} samples in, it turned up at {_found}");
        }
    }

    /// <summary>
    /// Feedback 0.5 takes 6 dB off every repeat.
    /// </summary>
    [Fact]
    public void FeedbackHalvesEveryRepeat()
    {
        using (OwnDelayEffect _fx = _delay(100, 0.5f))
        {
            float[] _out = EffectHarness.Render(_fx, _burst(Rate), Ch);

            double _first = SignalMeasure.RmsDbOfFrames(_out, Ch, Rate / 10, BurstFrames);
            double _second = SignalMeasure.RmsDbOfFrames(_out, Ch, Rate / 5, BurstFrames);

            (_second - _first).Should().BeApproximately(-6.02, 0.5,
                $"the second repeat ({_second:F2} dB) should sit 6 dB under the first ({_first:F2} dB)");
        }
    }

    /// <summary>
    /// Freeze mutes the input and pins the loop gain at 1, so what is in there keeps playing.
    /// Kept quiet on purpose: the tape clip is always in the loop and would slowly eat a hot one.
    /// </summary>
    [Fact]
    public void FreezeHoldsTheLoop()
    {
        using (OwnDelayEffect _fx = _delay(30, 0f))
        {
            EffectHarness.Render(_fx, SignalGenerator.Sine(Tone, -24.0, Rate / 5, Ch, Rate), Ch);

            _fx.Freeze = true;
            float[] _out = EffectHarness.RenderInto(_fx, SignalGenerator.Silence(Rate * 2, Ch), Ch);

            double _early = SignalMeasure.RmsDbOfFrames(_out, Ch, Rate / 10, Rate / 5);
            double _late = SignalMeasure.RmsDbOfFrames(_out, Ch, Rate * 9 / 5, Rate / 5);

            _late.Should().BeGreaterThan(_early - 1.0,
                $"a frozen loop lost {_early - _late:F1} dB in under two seconds");
        }
    }

    /// <summary>
    /// Tempo sync: 120 BPM gives a 375 ms dotted eighth and a 500 ms quarter.
    /// </summary>
    [Fact]
    public void TempoSyncSetsTheNoteTimes()
    {
        using (var _fx = new OwnDelayEffect())
        {
            _fx.SyncToTempo(120, OwnDelayNoteValue.DottedEighth, OwnDelayNoteValue.Quarter);

            _fx.TimeLeft.Should().BeApproximately(375f, 1e-3f);
            _fx.TimeRight.Should().BeApproximately(500f, 1e-3f);
        }
    }
}
