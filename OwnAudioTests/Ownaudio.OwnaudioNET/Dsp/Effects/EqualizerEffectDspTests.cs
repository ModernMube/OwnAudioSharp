using System;
using Ownaudio.OwnaudioNET.Tests.Dsp.Harness;
using OwnaudioNET.Effects;

namespace Ownaudio.OwnaudioNET.Tests.Dsp.Effects;

/// <summary>
/// Frequency response of the 10 band EQ, probed one tone at a time. The thing that
/// actually matters is that a band set to +N dB moves its centre frequency by N dB —
/// the 30 band EQ once lost two thirds of that and nobody noticed.
/// </summary>
public class EqualizerEffectDspTests
{
    private const int Rate = EffectHarness.SampleRate;

    private static readonly float[] Centres =
    {
        31.25f, 62.5f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f
    };

    private static EqualizerEffect _flat() => new EqualizerEffect(Rate);

    private static EqualizerEffect _withBand(int band, float gainDb)
    {
        var _eq = new EqualizerEffect(Rate);
        _eq.SetBandGain(band, Centres[band], 1.0f, gainDb);
        return _eq;
    }

    /// <summary>
    /// Nothing boosted, nothing cut, nothing moves.
    /// </summary>
    [Fact]
    public void FlatEqIsTransparent()
    {
        double[] _probes = { 40, 100, 400, 1000, 3000, 9000, 15000 };

        foreach (double _f in _probes)
        {
            using (EqualizerEffect _eq = _flat())
            {
                double _gain = EffectHarness.MeasureGainDb(_eq, _f);
                _gain.Should().BeApproximately(0.0, 0.1, $"a flat EQ must not touch {_f:F0} Hz");
            }
        }
    }

    /// <summary>
    /// The headline check: ask for +12 dB, measure +12 dB at the band centre.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    public void BandBoostLandsOnTheRequestedGain(int band)
    {
        using (EqualizerEffect _eq = _withBand(band, 12.0f))
        {
            double _gain = EffectHarness.MeasureGainDb(_eq, Centres[band], -30.0);

            _gain.Should().BeApproximately(12.0, 0.5,
                $"band {band} at {Centres[band]:F0} Hz was set to +12 dB");
        }
    }

    /// <summary>
    /// And the same going down.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(7)]
    public void BandCutLandsOnTheRequestedGain(int band)
    {
        using (EqualizerEffect _eq = _withBand(band, -12.0f))
        {
            double _gain = EffectHarness.MeasureGainDb(_eq, Centres[band], -12.0);

            _gain.Should().BeApproximately(-12.0, 0.5,
                $"band {band} at {Centres[band]:F0} Hz was set to -12 dB");
        }
    }

    /// <summary>
    /// Requested and measured gain have to track each other one for one. A filter that
    /// scales the gain by some constant passes a single-point check but fails this.
    /// </summary>
    [Theory]
    [InlineData(3.0)]
    [InlineData(6.0)]
    [InlineData(9.0)]
    [InlineData(12.0)]
    public void MeasuredGainTracksTheRequestedGain(double requestedDb)
    {
        using (EqualizerEffect _eq = _withBand(5, (float)requestedDb))
        {
            double _gain = EffectHarness.MeasureGainDb(_eq, Centres[5], -30.0);
            _gain.Should().BeApproximately(requestedDb, 0.4);
        }
    }

    /// <summary>
    /// A boost at 1 kHz must not drag the far ends of the spectrum with it.
    /// </summary>
    [Fact]
    public void BoostStaysLocalToItsBand()
    {
        using (EqualizerEffect _eq = _withBand(5, 12.0f))
        {
            double _low = EffectHarness.MeasureGainDb(_eq, 60.0, -30.0);
            double _high = EffectHarness.MeasureGainDb(_eq, 12000.0, -30.0);

            Math.Abs(_low).Should().BeLessThan(1.0, "60 Hz is four octaves below a 1 kHz bell");
            Math.Abs(_high).Should().BeLessThan(1.0, "12 kHz is far above it");
        }
    }

    /// <summary>
    /// Two bands up at once should add up, not fight or double count.
    /// </summary>
    [Fact]
    public void TwoBandsBoostIndependently()
    {
        using (var _eq = new EqualizerEffect(Rate))
        {
            _eq.SetBandGain(2, Centres[2], 1.0f, 8.0f);
            _eq.SetBandGain(8, Centres[8], 1.0f, -8.0f);

            EffectHarness.MeasureGainDb(_eq, Centres[2], -30.0).Should().BeApproximately(8.0, 0.6);
        }

        using (var _eq = new EqualizerEffect(Rate))
        {
            _eq.SetBandGain(2, Centres[2], 1.0f, 8.0f);
            _eq.SetBandGain(8, Centres[8], 1.0f, -8.0f);

            EffectHarness.MeasureGainDb(_eq, Centres[8], -12.0).Should().BeApproximately(-8.0, 0.6);
        }
    }

    /// <summary>
    /// A bell filter must not smear a sine into harmonics.
    /// </summary>
    [Fact]
    public void BoostingDoesNotAddDistortion()
    {
        using (EqualizerEffect _eq = _withBand(5, 12.0f))
        {
            int _frames = EffectHarness.SettleFrames + Rate;
            float[] _in = SignalGenerator.Sine(1000, -30.0, _frames, EffectHarness.Channels, Rate);
            float[] _out = EffectHarness.Render(_eq, _in, EffectHarness.Channels);

            SignalMeasure.Thd(EffectHarness.Steady(_out, EffectHarness.Channels), 1000, Rate)
                .Should().BeLessThan(0.001, "a biquad is linear, it has no business making harmonics");
        }
    }
}
