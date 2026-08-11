using System;
using Ownaudio.OwnaudioNET.Tests.Dsp.Harness;
using OwnaudioNET.Effects.SmartMaster;

namespace Ownaudio.OwnaudioNET.Tests.Dsp.Effects;

/// <summary>
/// Signal tests for the mastering chain. Nearly every preview release carried a
/// SmartMaster fix, so each stage gets measured on its own instead of trusting the
/// chain as a whole.
/// </summary>
public class SmartMasterEffectDspTests
{
    private const int Ch = EffectHarness.Channels;
    private const int Rate = EffectHarness.SampleRate;

    /// <summary>
    /// Builds an initialized effect with the configuration edit applied.
    /// </summary>
    private static SmartMasterEffect _configured(Action<SmartMasterConfig> edit)
    {
        var _fx = new SmartMasterEffect();
        _fx.Initialize(EffectHarness.Config());

        SmartMasterConfig _cfg = _fx.GetConfiguration();
        edit(_cfg);
        _fx.ApplyConfiguration(_cfg);

        return _fx;
    }

    private static double _gainAt(SmartMasterEffect fx, double freq, double levelDb, int seconds = 2)
    {
        int _frames = EffectHarness.SettleFrames + Rate * seconds;
        float[] _in = SignalGenerator.Sine(freq, levelDb, _frames, Ch, Rate);
        float[] _out = EffectHarness.RenderInto(fx, _in, Ch);

        return SignalMeasure.MagnitudeDbAt(EffectHarness.Steady(_out, Ch), freq, Rate)
             - SignalMeasure.MagnitudeDbAt(EffectHarness.Steady(_in, Ch), freq, Rate);
    }

    /// <summary>
    /// Everything off by default, so a quiet tone should come out the way it went in.
    /// </summary>
    [Fact]
    public void DefaultChainLeavesAQuietToneAlone()
    {
        using (SmartMasterEffect _fx = _configured(_ => { }))
        {
            _gainAt(_fx, 1000, -20.0).Should().BeApproximately(0.0, 0.5,
                "with every stage disabled the chain is a pass-through");
        }
    }

    /// <summary>
    /// The subsonic filter has to actually remove rumble, and leave the music alone.
    /// </summary>
    [Fact]
    public void SubsonicFilterCutsBelowItsCorner()
    {
        double _rumble, _music;

        using (SmartMasterEffect _fx = _configured(c => { c.SubsonicEnabled = true; c.SubsonicFrequency = 35.0f; }))
        {
            _rumble = _gainAt(_fx, 12.0, -20.0, seconds: 4);
        }

        using (SmartMasterEffect _fx = _configured(c => { c.SubsonicEnabled = true; c.SubsonicFrequency = 35.0f; }))
        {
            _music = _gainAt(_fx, 300.0, -20.0);
        }

        _rumble.Should().BeLessThan(-12.0, "12 Hz is well under a 35 Hz corner and should be gone");
        Math.Abs(_music).Should().BeLessThan(1.0, "300 Hz is nowhere near the subsonic filter");
    }

    /// <summary>
    /// Turning the subsonic filter off leaves the low end where it was.
    /// </summary>
    [Fact]
    public void SubsonicFilterOffLeavesTheLowEndAlone()
    {
        using (SmartMasterEffect _fx = _configured(c => c.SubsonicEnabled = false))
        {
            Math.Abs(_gainAt(_fx, 12.0, -20.0, seconds: 4)).Should().BeLessThan(1.0);
        }
    }

    /// <summary>
    /// A graphic EQ band set in the config has to show up in the output at that frequency.
    /// </summary>
    [Fact]
    public void GraphicEqBandBoostReachesTheOutput()
    {
        using (SmartMasterEffect _fx = _configured(c =>
        {
            float[] _gains = c.GraphicEQGains;
            _gains[17] = 9.0f;
            c.GraphicEQGains = _gains;
        }))
        {
            _gainAt(_fx, 1000, -30.0).Should().BeGreaterThan(6.0,
                "band 17 is the 1 kHz third-octave band and was pushed up 9 dB");
        }
    }

    /// <summary>
    /// A parametric bell asked for a cut has to deliver one.
    /// </summary>
    [Fact]
    public void ParametricBandCutReachesTheOutput()
    {
        using (SmartMasterEffect _fx = _configured(c =>
        {
            ParametricBand[] _bands = c.ParametricEQ;
            _bands[0].Shape = ParametricShape.Bell;
            _bands[0].Frequency = 2000f;
            _bands[0].Q = 1.4f;
            _bands[0].GainDb = -10.0f;
            c.ParametricEQ = _bands;
        }))
        {
            _gainAt(_fx, 2000, -20.0).Should().BeLessThan(-7.0, "a -10 dB bell sits on 2 kHz");
        }
    }

    /// <summary>
    /// The output limiter is the last line — a hot input must not come out over its threshold.
    /// </summary>
    [Fact]
    public void OutputLimiterHoldsTheThreshold()
    {
        using (SmartMasterEffect _fx = _configured(c => { c.LimiterThreshold = -6.0f; c.LimiterCeiling = -0.5f; }))
        {
            int _frames = EffectHarness.SettleFrames + Rate;
            float[] _in = SignalGenerator.Sine(500, 0.0, _frames, Ch, Rate);
            float[] _out = EffectHarness.RenderInto(_fx, _in, Ch);

            SignalMeasure.PeakDb(EffectHarness.Steady(_out, Ch))
                .Should().BeLessThan(-5.0, "a full scale tone into a -6 dB limiter threshold has to come down");
        }
    }

    /// <summary>
    /// Garbage upstream gets zeroed rather than passed on, and the counter says so.
    /// </summary>
    [Fact]
    public void NonFiniteInputIsSanitized()
    {
        using (SmartMasterEffect _fx = _configured(_ => { }))
        {
            float[] _in = SignalGenerator.Sine(440, -12.0, 2048, Ch, Rate);
            _in[100] = float.NaN;
            _in[101] = float.PositiveInfinity;

            float[] _out = EffectHarness.RenderInto(_fx, _in, Ch);

            SignalMeasure.AllFinite(_out).Should().BeTrue("the chain must never let NaN out");
            _fx.SanitizedSampleCount.Should().BeGreaterThanOrEqualTo(2);
        }
    }

    /// <summary>
    /// A clean pass leaves the counter alone, otherwise the diagnostic is worthless.
    /// </summary>
    [Fact]
    public void CleanInputLeavesTheSanitizeCounterAtZero()
    {
        using (SmartMasterEffect _fx = _configured(_ => { }))
        {
            float[] _in = SignalGenerator.Sine(440, -12.0, 4096, Ch, Rate);
            EffectHarness.RenderInto(_fx, _in, Ch);

            _fx.SanitizedSampleCount.Should().Be(0);
        }
    }

    /// <summary>
    /// A transparent chain should not be inventing harmonics either.
    /// </summary>
    [Fact]
    public void DefaultChainDoesNotDistort()
    {
        using (SmartMasterEffect _fx = _configured(_ => { }))
        {
            int _frames = EffectHarness.SettleFrames + Rate;
            float[] _in = SignalGenerator.Sine(1000, -20.0, _frames, Ch, Rate);
            float[] _out = EffectHarness.RenderInto(_fx, _in, Ch);

            SignalMeasure.Thd(EffectHarness.Steady(_out, Ch), 1000, Rate).Should().BeLessThan(0.01);
        }
    }
}
