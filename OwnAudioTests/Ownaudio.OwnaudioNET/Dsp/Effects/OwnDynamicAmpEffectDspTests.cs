using System;
using Ownaudio.OwnaudioNET.Tests.Dsp.Harness;
using OwnaudioNET.Effects;

namespace Ownaudio.OwnaudioNET.Tests.Dsp.Effects;

/// <summary>
/// The loudness based rider. A stereo 997 Hz sine reads its peak level in LUFS under
/// BS.1770, so the expected gains can be written down straight from the input level.
/// </summary>
public class OwnDynamicAmpEffectDspTests
{
    private const int Ch = EffectHarness.Channels;
    private const int Rate = EffectHarness.SampleRate;
    private const double Tone = 997.0;

    /// <summary>
    /// Runs a long tone through the effect and hands back the rendered buffer.
    /// </summary>
    private static float[] _render(OwnDynamicAmpEffect fx, double inputDb, int seconds)
    {
        float[] _in = SignalGenerator.Sine(Tone, inputDb, Rate * seconds, Ch, Rate);
        return EffectHarness.Render(fx, _in, Ch);
    }

    /// <summary>
    /// A quiet source gets lifted onto the target loudness.
    /// </summary>
    [Fact]
    public void BringsAQuietSourceUpToTheTarget()
    {
        using (var _fx = new OwnDynamicAmpEffect(-14f))
        {
            _fx.MaxBoost = 20f;
            float[] _out = _render(_fx, -30.0, 40);

            double _settled = SignalMeasure.RmsDbOfFrames(_out, Ch, Rate * 39, Rate) + 3.01;
            _settled.Should().BeApproximately(-14.0, 1.2, "a -30 LUFS tone has to land on the -14 LUFS target");
            _fx.ProgramLoudness.Should().BeApproximately(-30f, 0.2f, "the meter reads the input loudness");
        }
    }

    /// <summary>
    /// A hot source gets pulled down to the target.
    /// </summary>
    [Fact]
    public void PullsAHotSourceDownToTheTarget()
    {
        using (var _fx = new OwnDynamicAmpEffect(-14f))
        {
            _render(_fx, -4.0, 30);
            _fx.CurrentGainDb.Should().BeApproximately(-10f, 1.2f, "-4 LUFS has to come down by about 10 dB");
        }
    }

    /// <summary>
    /// MaxBoost is a hard ceiling for the lift.
    /// </summary>
    [Fact]
    public void StopsAtMaxBoost()
    {
        using (var _fx = new OwnDynamicAmpEffect(-14f))
        {
            _fx.MaxBoost = 6f;
            _render(_fx, -40.0, 30);
            _fx.CurrentGainDb.Should().BeLessThanOrEqualTo(6.01f, "MaxBoost is a hard limit");
        }
    }

    /// <summary>
    /// Material already inside the tolerance window comes out untouched, only delayed.
    /// </summary>
    [Fact]
    public void LeavesMaterialInsideTheToleranceAlone()
    {
        using (var _fx = new OwnDynamicAmpEffect(-14f))
        {
            _fx.Tolerance = 1f;
            float[] _in = SignalGenerator.Sine(Tone, -14.5, Rate * 6, Ch, Rate);
            float[] _out = EffectHarness.Render(_fx, _in, Ch);

            int _lag = _fx.LatencySamples * Ch;
            SignalMeasure.MaxDiff(_out.AsSpan(_lag), _in.AsSpan(0, _in.Length - _lag))
                .Should().Be(0.0, "inside the tolerance the rider is a delayed wire");
        }
    }

    /// <summary>
    /// The output never passes the true-peak ceiling, even on a full scale tone.
    /// </summary>
    [Fact]
    public void KeepsThePeakUnderTheCeiling()
    {
        using (var _fx = new OwnDynamicAmpEffect(-5f, -1f))
        {
            float[] _out = _render(_fx, 0.0, 4);
            SignalMeasure.PeakDb(_out).Should().BeLessThanOrEqualTo(-0.99, "the ceiling is -1 dBTP");
        }
    }

    /// <summary>
    /// The look-ahead is reported as latency.
    /// </summary>
    [Fact]
    public void ReportsTheLookaheadAsLatency()
    {
        using (var _fx = new OwnDynamicAmpEffect())
        {
            _fx.Lookahead = 5f;
            _fx.Initialize(EffectHarness.Config());
            _fx.LatencySamples.Should().Be(240, "5 ms at 48 kHz");
        }
    }

    /// <summary>
    /// Every preset renders finite audio inside full scale.
    /// </summary>
    [Theory]
    [InlineData(OwnDynamicAmpPreset.Default)]
    [InlineData(OwnDynamicAmpPreset.Speech)]
    [InlineData(OwnDynamicAmpPreset.Music)]
    [InlineData(OwnDynamicAmpPreset.Broadcast)]
    [InlineData(OwnDynamicAmpPreset.Mastering)]
    [InlineData(OwnDynamicAmpPreset.Live)]
    [InlineData(OwnDynamicAmpPreset.Transparent)]
    public void EveryPresetStaysFiniteAndInRange(OwnDynamicAmpPreset preset)
    {
        using (var _fx = new OwnDynamicAmpEffect(preset))
        {
            float[] _in = SignalGenerator.Noise(-10.0, Rate * 3, Ch);
            float[] _out = EffectHarness.Render(_fx, _in, Ch);

            SignalMeasure.AllFinite(_out).Should().BeTrue();
            SignalMeasure.PeakDb(_out).Should().BeLessThanOrEqualTo(0.0);
        }
    }
}
