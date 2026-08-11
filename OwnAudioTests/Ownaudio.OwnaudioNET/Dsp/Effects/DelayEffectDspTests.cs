using System;
using Ownaudio.OwnaudioNET.Tests.Dsp.Harness;
using OwnaudioNET.Effects;

namespace Ownaudio.OwnaudioNET.Tests.Dsp.Effects;

/// <summary>
/// Timing and level of the delay repeats. A short tone burst goes in and the echo is
/// located by cross-correlation, so the delay time is checked in samples rather than
/// by eye.
/// </summary>
public class DelayEffectDspTests
{
    private const int Ch = EffectHarness.Channels;
    private const int Rate = EffectHarness.SampleRate;
    private const double Tone = 1000.0;
    private const int BurstFrames = Rate / 50;

    //Damping is the feedback filter's tracking coefficient, so 1.0 is the fully open,
    //undamped case — 0.0 would mute the wet path entirely
    private static DelayEffect _delay(int timeMs, float repeat = 0.4f, float mix = 0.5f, float damping = 1.0f, bool pingPong = false)
        => new DelayEffect(timeMs, repeat, mix, damping, Rate, pingPong);

    private static float[] _burst(int totalFrames)
        => SignalGenerator.SineBurst(Tone, -6.0, BurstFrames, totalFrames, Ch, Rate);

    /// <summary>
    /// The first repeat has to land exactly where the Time property says it will.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(250)]
    [InlineData(375)]
    public void FirstEchoArrivesAtTheSetDelayTime(int timeMs)
    {
        using (DelayEffect _fx = _delay(timeMs, repeat: 0.0f))
        {
            int _frames = Rate * 2;
            float[] _in = _burst(_frames);
            float[] _out = EffectHarness.Render(_fx, _in, Ch);

            float[] _dry = SignalMeasure.Channel(_in, Ch, 0);
            float[] _wet = SignalMeasure.Channel(_out, Ch, 0);

            int _expected = timeMs * Rate / 1000;
            int _found = SignalMeasure.DelaySamples(_dry, _wet, BurstFrames * 2, _expected * 2);

            _found.Should().BeCloseTo(_expected, (uint)(Rate / 500),
                $"a {timeMs} ms delay puts the echo {_expected} samples in, but it turned up at {_found}");
        }
    }

    /// <summary>
    /// More feedback means the later repeats are still there.
    /// </summary>
    [Fact]
    public void MoreFeedbackKeepsTheRepeatsGoingLonger()
    {
        double _quiet = _thirdRepeatDb(0.15f);
        double _loud = _thirdRepeatDb(0.75f);

        _loud.Should().BeGreaterThan(_quiet + 6.0,
            $"at 0.75 feedback the third repeat ({_loud:F1} dB) should be well above the 0.15 case ({_quiet:F1} dB)");
    }

    private static double _thirdRepeatDb(float repeat)
    {
        using (DelayEffect _fx = _delay(100, repeat))
        {
            float[] _out = EffectHarness.Render(_fx, _burst(Rate * 2), Ch);

            //Window sitting on the third repeat, 300 ms in
            return SignalMeasure.RmsDbOfFrames(_out, Ch, Rate * 300 / 1000, BurstFrames);
        }
    }

    /// <summary>
    /// Only the feedback is cross-fed, so a left-side burst comes back on the left first
    /// and lands on the right on the second repeat. Same topology as the native delay.
    /// </summary>
    [Fact]
    public void PingPongSendsTheSecondRepeatToTheOtherSide()
    {
        double[] _pinged = _repeatLevels(pingPong: true);
        double[] _plain = _repeatLevels(pingPong: false);

        _pinged[1].Should().BeGreaterThan(_pinged[0] + 20.0,
            "the first repeat stays left, the second crosses to the right");
        _plain[1].Should().BeLessThan(-100.0,
            "without ping-pong a left-only source never reaches the right channel");
    }

    /// <summary>
    /// Right channel level at the first and second repeat of a left-only burst.
    /// </summary>
    private static double[] _repeatLevels(bool pingPong)
    {
        using (DelayEffect _fx = _delay(150, 0.6f, 0.7f, pingPong: pingPong))
        {
            int _frames = Rate * 2;
            float[] _in = SignalGenerator.SineOnChannel(Tone, -6.0, _frames, Ch, Rate, 0);
            Array.Clear(_in, BurstFrames * Ch, (_frames - BurstFrames) * Ch);

            float[] _out = EffectHarness.Render(_fx, _in, Ch);
            float[] _right = SignalMeasure.Channel(_out, Ch, 1);

            int _echo = 150 * Rate / 1000;
            return new double[]
            {
                SignalMeasure.RmsDb(_right.AsSpan(_echo, BurstFrames)),
                SignalMeasure.RmsDb(_right.AsSpan(_echo * 2, BurstFrames))
            };
        }
    }

    /// <summary>
    /// Damping is the tracking coefficient of the feedback low-pass, so a lower value
    /// smooths harder and the repeats lose their top end. This runs backwards from what
    /// the parameter name suggests — see the Damping property.
    /// </summary>
    [Fact]
    public void LowerDampingDullsTheRepeats()
    {
        double _bright = _echoBandDb(1.0f);
        double _dull = _echoBandDb(0.15f);

        _dull.Should().BeLessThan(_bright - 3.0,
            $"a 0.15 coefficient smooths the 6 kHz repeat away ({_dull:F1} dB) next to the open one ({_bright:F1} dB)");
    }

    private static double _echoBandDb(float damping)
    {
        using (DelayEffect _fx = _delay(100, 0.6f, 0.6f, damping))
        {
            float[] _in = SignalGenerator.SineBurst(6000, -6.0, BurstFrames, Rate * 2, Ch, Rate);
            float[] _out = EffectHarness.Render(_fx, _in, Ch);

            int _echo = Rate * 100 / 1000;
            return SignalMeasure.MagnitudeDbAt(SignalMeasure.Channel(_out, Ch, 0).AsSpan(_echo, BurstFrames), 6000, Rate);
        }
    }

    /// <summary>
    /// The documented oddity, pinned down: a zero coefficient freezes the filter and the
    /// wet path goes silent instead of becoming undamped. If anyone ever flips the sense
    /// of this parameter, this test is where it shows up.
    /// </summary>
    [Fact]
    public void ZeroDampingSilencesTheWetPath()
    {
        using (DelayEffect _fx = _delay(100, 0.6f, 1.0f, damping: 0.0f))
        {
            float[] _out = EffectHarness.Render(_fx, _burst(Rate), Ch);

            SignalMeasure.PeakDb(_out).Should().BeLessThan(-100.0,
                "fully wet with a frozen feedback filter, there is nothing left to hear");
        }
    }

    /// <summary>
    /// Nothing should come back before the delay time is up.
    /// </summary>
    [Fact]
    public void NothingArrivesBeforeTheDelayTime()
    {
        using (DelayEffect _fx = _delay(300, 0.5f, 0.6f))
        {
            float[] _out = EffectHarness.Render(_fx, _burst(Rate * 2), Ch);

            int _gapStart = BurstFrames * 2;
            int _gapEnd = 300 * Rate / 1000 - BurstFrames;

            SignalMeasure.RmsDbOfFrames(_out, Ch, _gapStart, _gapEnd - _gapStart)
                .Should().BeLessThan(-80.0, "the gap between the dry burst and the first repeat has to be quiet");
        }
    }

    /// <summary>
    /// Delay times whose ms-to-samples conversion is not a whole number used to walk the
    /// read index one past the end of the line the moment the write index caught up with
    /// it. 150 ms at 48 kHz is 7200.0005 samples, which is exactly that case.
    /// </summary>
    [Theory]
    [InlineData(150)]
    [InlineData(300)]
    [InlineData(70)]
    [InlineData(333)]
    [InlineData(1234)]
    public void FractionalDelayTimesSurviveTheBufferWrap(int timeMs)
    {
        using (DelayEffect _fx = _delay(timeMs, 0.5f, 0.6f))
        {
            float[] _out = EffectHarness.Render(_fx, _burst(Rate * 3), Ch);

            SignalMeasure.AllFinite(_out).Should().BeTrue($"a {timeMs} ms delay has to run clean past its own length");
        }
    }

    /// <summary>
    /// Time round trips through the clamp.
    /// </summary>
    [Fact]
    public void DelayTimeRoundTrips()
    {
        using (DelayEffect _fx = _delay(200))
        {
            _fx.Time = 420;
            _fx.Time.Should().Be(420);

            _fx.Time = 99999;
            _fx.Time.Should().Be(5000, "the delay tops out at 5 s");
        }
    }
}
