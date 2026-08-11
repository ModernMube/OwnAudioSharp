using System;
using Ownaudio.OwnaudioNET.Tests.Dsp.Harness;
using OwnaudioNET.Effects;

namespace Ownaudio.OwnaudioNET.Tests.Dsp.Effects;

/// <summary>
/// Static transfer curve of the compressor, measured tone by tone. Below the threshold
/// the curve is 1:1, above it the slope is 1/ratio — that is the whole contract, and it
/// is easy to get subtly wrong.
/// </summary>
public class CompressorEffectDspTests
{
    private const int Ch = EffectHarness.Channels;
    private const int Rate = EffectHarness.SampleRate;
    private const double Tone = 1000.0;
    private const float ThresholdDb = -20.0f;

    private static CompressorEffect _hardKnee(float ratio, float makeupDb = 0.0f)
    {
        var _fx = new CompressorEffect(CompressorPreset.Default, Rate);
        _fx.Threshold = ThresholdDb;
        _fx.Ratio = ratio;
        _fx.KneeWidth = 0.0f;
        _fx.AttackTime = 5.0f;
        _fx.ReleaseTime = 200.0f;
        _fx.MakeupGain = makeupDb;

        return _fx;
    }

    private static double _outputPeakDb(CompressorEffect fx, double inputDb)
    {
        int _frames = EffectHarness.SettleFrames + Rate;
        float[] _in = SignalGenerator.Sine(Tone, inputDb, _frames, Ch, Rate);
        float[] _out = EffectHarness.Render(fx, _in, Ch);

        return SignalMeasure.PeakDb(EffectHarness.Steady(_out, Ch));
    }

    /// <summary>
    /// Under the threshold the compressor is a wire.
    /// </summary>
    [Theory]
    [InlineData(-40.0)]
    [InlineData(-30.0)]
    [InlineData(-24.0)]
    public void BelowThresholdNothingHappens(double inputDb)
    {
        using (CompressorEffect _fx = _hardKnee(4.0f))
        {
            _outputPeakDb(_fx, inputDb).Should().BeApproximately(inputDb, 0.3,
                $"{inputDb:F0} dBFS is under the {ThresholdDb:F0} dB threshold");
        }
    }

    /// <summary>
    /// Above the threshold every dB of input turns into 1/ratio dB of output.
    /// </summary>
    [Theory]
    [InlineData(2.0f, 12.0)]
    [InlineData(4.0f, 12.0)]
    [InlineData(8.0f, 12.0)]
    [InlineData(4.0f, 6.0)]
    public void AboveThresholdTheSlopeIsOneOverRatio(float ratio, double overshootDb)
    {
        using (CompressorEffect _fx = _hardKnee(ratio))
        {
            double _in = ThresholdDb + overshootDb;
            double _expected = ThresholdDb + overshootDb / ratio;

            _outputPeakDb(_fx, _in).Should().BeApproximately(_expected, 0.8,
                $"{overshootDb:F0} dB over the threshold at {ratio:F0}:1 should come out {overshootDb / ratio:F1} dB over it");
        }
    }

    /// <summary>
    /// Makeup gain sits on top of whatever the curve produced.
    /// </summary>
    [Theory]
    [InlineData(0.0f)]
    [InlineData(3.0f)]
    [InlineData(6.0f)]
    public void MakeupGainIsAddedOnTop(float makeupDb)
    {
        double _plain, _lifted;

        using (CompressorEffect _fx = _hardKnee(4.0f)) { _plain = _outputPeakDb(_fx, -8.0); }
        using (CompressorEffect _fx = _hardKnee(4.0f, makeupDb)) { _lifted = _outputPeakDb(_fx, -8.0); }

        (_lifted - _plain).Should().BeApproximately(makeupDb, 0.3);
    }

    /// <summary>
    /// A higher ratio always squeezes harder, never less.
    /// </summary>
    [Fact]
    public void HigherRatioMeansMoreGainReduction()
    {
        float[] _ratios = { 2f, 4f, 8f, 16f };
        double _previous = -1.0;

        foreach (float _r in _ratios)
        {
            using (CompressorEffect _fx = _hardKnee(_r))
            {
                double _reduction = -8.0 - _outputPeakDb(_fx, -8.0);

                _reduction.Should().BeGreaterThan(_previous,
                    $"{_r:F0}:1 has to reduce more than the ratio before it");
                _previous = _reduction;
            }
        }
    }

    /// <summary>
    /// A soft knee starts working before the threshold, so right at it there is already
    /// a little reduction that the hard knee does not have.
    /// </summary>
    [Fact]
    public void SoftKneeStartsEarlierThanHardKnee()
    {
        double _hard, _soft;

        using (CompressorEffect _fx = _hardKnee(4.0f)) { _hard = _outputPeakDb(_fx, ThresholdDb - 2.0); }

        using (CompressorEffect _fx = _hardKnee(4.0f))
        {
            _fx.KneeWidth = 12.0f;
            _soft = _outputPeakDb(_fx, ThresholdDb - 2.0);
        }

        _soft.Should().BeLessThan(_hard - 0.3,
            "2 dB under the threshold a 12 dB knee is already pulling, a hard knee is not");
    }

    /// <summary>
    /// A fast attack has the gain down sooner than a slow one. Measured on the level
    /// right after a step, where the two settings differ most.
    /// </summary>
    [Fact]
    public void FasterAttackClampsTheTransientSooner()
    {
        double _fast = _levelJustAfterStep(1.0f);
        double _slow = _levelJustAfterStep(100.0f);

        _fast.Should().BeLessThan(_slow - 1.0, "a 1 ms attack should be well ahead of a 100 ms one");
    }

    private static double _levelJustAfterStep(float attackMs)
    {
        using (CompressorEffect _fx = _hardKnee(8.0f))
        {
            _fx.AttackTime = attackMs;

            int _step = Rate / 2;
            float[] _in = SignalGenerator.SineStep(Tone, -40.0, -2.0, _step, Rate, Ch, Rate);
            float[] _out = EffectHarness.Render(_fx, _in, Ch);

            //A 10 ms slice straight after the jump — long enough to measure, short enough
            //that a slow attack has not caught up yet
            return SignalMeasure.RmsDbOfFrames(_out, Ch, _step, Rate / 100);
        }
    }

    /// <summary>
    /// Compressing a steady tone should not turn it into a different waveform.
    /// </summary>
    [Fact]
    public void SteadyToneStaysClean()
    {
        using (CompressorEffect _fx = _hardKnee(4.0f))
        {
            int _frames = EffectHarness.SettleFrames + Rate;
            float[] _in = SignalGenerator.Sine(Tone, -8.0, _frames, Ch, Rate);
            float[] _out = EffectHarness.Render(_fx, _in, Ch);

            SignalMeasure.Thd(EffectHarness.Steady(_out, Ch), Tone, Rate)
                .Should().BeLessThan(0.05, "a settled compressor riding a sine barely distorts");
        }
    }
}
