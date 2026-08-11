using System;
using Ownaudio.OwnaudioNET.Tests.Dsp.Harness;
using OwnaudioNET.Effects;

namespace Ownaudio.OwnaudioNET.Tests.Dsp.Effects;

/// <summary>
/// Reverb behaviour measured on a tone burst: there has to be a tail after the input
/// stops, it has to get longer with room size, and the two channels must not be copies
/// of each other.
/// </summary>
public class ReverbEffectDspTests
{
    private const int Ch = EffectHarness.Channels;
    private const int Rate = EffectHarness.SampleRate;
    private const int BurstFrames = Rate / 10;

    private static ReverbEffect _reverb(float size, float damp = 0.5f, float wet = 0.8f, float dry = 0.2f)
        => new ReverbEffect(size, damp, wet, dry, 1.0f, 1.0f, 1.0f);

    private static float[] _burstThenSilence(int totalFrames)
        => SignalGenerator.SineBurst(700, -6.0, BurstFrames, totalFrames, Ch, Rate);

    /// <summary>
    /// The tail keeps going well after the input has stopped.
    /// </summary>
    [Fact]
    public void TailContinuesAfterTheInputStops()
    {
        using (ReverbEffect _fx = _reverb(0.8f))
        {
            float[] _out = EffectHarness.Render(_fx, _burstThenSilence(Rate * 2), Ch);

            double _tail = SignalMeasure.RmsDbOfFrames(_out, Ch, BurstFrames + Rate / 20, Rate / 10);
            _tail.Should().BeGreaterThan(-60.0, "a large room should still be ringing 50 ms after the burst");
        }
    }

    /// <summary>
    /// The tail decays instead of hanging around or building up.
    /// </summary>
    [Fact]
    public void TailDecaysOverTime()
    {
        using (ReverbEffect _fx = _reverb(0.8f))
        {
            float[] _out = EffectHarness.Render(_fx, _burstThenSilence(Rate * 3), Ch);

            double _early = SignalMeasure.RmsDbOfFrames(_out, Ch, BurstFrames, Rate / 10);
            double _late = SignalMeasure.RmsDbOfFrames(_out, Ch, BurstFrames + Rate, Rate / 10);

            _late.Should().BeLessThan(_early - 10.0, "a second later the tail has to be well down");
        }
    }

    /// <summary>
    /// A bigger room rings for longer.
    /// </summary>
    [Fact]
    public void BiggerRoomRingsLonger()
    {
        double _small = _tailLevelAt(0.2f);
        double _large = _tailLevelAt(0.95f);

        _large.Should().BeGreaterThan(_small + 3.0,
            $"a large room ({_large:F1} dB) has to outlast a small one ({_small:F1} dB)");
    }

    private static double _tailLevelAt(float size)
    {
        using (ReverbEffect _fx = _reverb(size))
        {
            float[] _out = EffectHarness.Render(_fx, _burstThenSilence(Rate * 2), Ch);
            return SignalMeasure.RmsDbOfFrames(_out, Ch, BurstFrames + Rate / 4, Rate / 10);
        }
    }

    /// <summary>
    /// A mono source has to come out with a spread tail, not two identical channels.
    /// </summary>
    [Fact]
    public void MonoInputComesOutDecorrelated()
    {
        using (ReverbEffect _fx = _reverb(0.8f))
        {
            float[] _out = EffectHarness.Render(_fx, _burstThenSilence(Rate * 2), Ch);

            //Measured on the tail only, where the dry copy is gone
            int _from = (BurstFrames + Rate / 10) * Ch;
            double _corr = SignalMeasure.StereoCorrelation(_out.AsSpan(_from), Ch);

            Math.Abs(_corr).Should().BeLessThan(0.9, "the reverb tail should not be the same on both sides");
        }
    }

    /// <summary>
    /// Wet at zero and dry at one is a bypass.
    /// </summary>
    [Fact]
    public void FullyDrySettingPassesTheInputThrough()
    {
        using (ReverbEffect _fx = _reverb(0.8f, wet: 0.0f, dry: 1.0f))
        {
            float[] _in = _burstThenSilence(Rate);
            float[] _out = EffectHarness.Render(_fx, _in, Ch);

            SignalMeasure.MaxDiff(_in, _out).Should().BeLessThan(1e-4,
                "no wet signal and unity dry means nothing should change");
        }
    }

    /// <summary>
    /// More wet means more output energy from the same input.
    /// </summary>
    [Fact]
    public void MoreWetMeansMoreTail()
    {
        double _quiet = _tailWithWet(0.2f);
        double _loud = _tailWithWet(1.0f);

        _loud.Should().BeGreaterThan(_quiet + 5.0);
    }

    private static double _tailWithWet(float wet)
    {
        using (ReverbEffect _fx = _reverb(0.7f, wet: wet, dry: 0.0f))
        {
            float[] _out = EffectHarness.Render(_fx, _burstThenSilence(Rate * 2), Ch);
            return SignalMeasure.RmsDbOfFrames(_out, Ch, BurstFrames + Rate / 20, Rate / 10);
        }
    }

    /// <summary>
    /// Damping takes the top end off the tail.
    /// </summary>
    [Fact]
    public void DampingDullsTheTail()
    {
        double _bright = _tailBandDb(0.05f);
        double _dark = _tailBandDb(0.95f);

        _dark.Should().BeLessThan(_bright - 2.0,
            $"a damped tail ({_dark:F1} dB at 6 kHz) has to be darker than an open one ({_bright:F1} dB)");
    }

    private static double _tailBandDb(float damping)
    {
        using (ReverbEffect _fx = _reverb(0.8f, damp: damping, wet: 1.0f, dry: 0.0f))
        {
            float[] _in = SignalGenerator.SineBurst(6000, -6.0, BurstFrames, Rate * 2, Ch, Rate);
            float[] _out = EffectHarness.Render(_fx, _in, Ch);

            float[] _tail = SignalMeasure.Channel(_out, Ch, 0);
            return SignalMeasure.MagnitudeDbAt(_tail.AsSpan(BurstFrames + Rate / 20, Rate / 10), 6000, Rate);
        }
    }
}
