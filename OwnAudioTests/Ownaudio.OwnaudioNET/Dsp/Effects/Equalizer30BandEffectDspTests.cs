using System;
using Ownaudio.OwnaudioNET.Tests.Dsp.Harness;
using OwnaudioNET.Effects;

namespace Ownaudio.OwnaudioNET.Tests.Dsp.Effects;

/// <summary>
/// Third-octave EQ response. This is the module that once shipped delivering roughly a
/// third of the gain it was asked for after a constant-Q change, so the gain law gets
/// checked band by band rather than spot-checked.
/// </summary>
public class Equalizer30BandEffectDspTests
{
    private const int Rate = EffectHarness.SampleRate;

    private static readonly float[] Centres =
    {
        20f, 25f, 31.5f, 40f, 50f, 63f, 80f, 100f, 125f, 160f,
        200f, 250f, 315f, 400f, 500f, 630f, 800f, 1000f, 1250f, 1600f,
        2000f, 2500f, 3150f, 4000f, 5000f, 6300f, 8000f, 10000f, 12500f, 16000f
    };

    private static Equalizer30BandEffect _withBand(int band, float gainDb)
    {
        var _eq = new Equalizer30BandEffect(Rate);
        _eq[band] = gainDb;
        return _eq;
    }

    //Low bands need a longer window before a single-bin measurement settles down
    private static int _seconds(int band) => Centres[band] < 200f ? 3 : 1;

    /// <summary>
    /// Every band flat, so every probe frequency comes back untouched.
    /// </summary>
    [Fact]
    public void FlatEqIsTransparent()
    {
        double[] _probes = { 50, 200, 800, 2000, 6300, 14000 };

        foreach (double _f in _probes)
        {
            using (var _eq = new Equalizer30BandEffect(Rate))
            {
                EffectHarness.MeasureGainDb(_eq, _f).Should().BeApproximately(0.0, 0.1,
                    $"a flat 30 band EQ must not touch {_f:F0} Hz");
            }
        }
    }

    /// <summary>
    /// Boost a band, measure its centre, get the number back. Run across the whole
    /// span so one broken corner of the table can't hide.
    /// </summary>
    [Theory]
    [InlineData(6)]
    [InlineData(10)]
    [InlineData(14)]
    [InlineData(17)]
    [InlineData(20)]
    [InlineData(23)]
    [InlineData(26)]
    [InlineData(28)]
    public void BandBoostLandsOnTheRequestedGain(int band)
    {
        using (Equalizer30BandEffect _eq = _withBand(band, 12.0f))
        {
            double _gain = EffectHarness.MeasureGainDb(_eq, Centres[band], -30.0, seconds: _seconds(band));

            _gain.Should().BeApproximately(12.0, 0.6,
                $"band {band} sits at {Centres[band]:F0} Hz and was set to +12 dB");
        }
    }

    /// <summary>
    /// Same going down.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(17)]
    [InlineData(24)]
    public void BandCutLandsOnTheRequestedGain(int band)
    {
        using (Equalizer30BandEffect _eq = _withBand(band, -12.0f))
        {
            double _gain = EffectHarness.MeasureGainDb(_eq, Centres[band], -12.0, seconds: _seconds(band));

            _gain.Should().BeApproximately(-12.0, 0.6,
                $"band {band} at {Centres[band]:F0} Hz was set to -12 dB");
        }
    }

    /// <summary>
    /// The constant-Q regression in one test: requested and measured gain have to move
    /// together. A filter handing back a fixed fraction of the gain shows up straight away.
    /// </summary>
    [Theory]
    [InlineData(3.0)]
    [InlineData(6.0)]
    [InlineData(9.0)]
    [InlineData(12.0)]
    public void MeasuredGainTracksTheRequestedGain(double requestedDb)
    {
        using (Equalizer30BandEffect _eq = _withBand(17, (float)requestedDb))
        {
            EffectHarness.MeasureGainDb(_eq, Centres[17], -30.0).Should().BeApproximately(requestedDb, 0.5);
        }
    }

    /// <summary>
    /// A third-octave band leans on its neighbours a bit, but three bands out it should
    /// be mostly gone.
    /// </summary>
    [Fact]
    public void BoostFallsOffAwayFromTheBand()
    {
        using (Equalizer30BandEffect _eq = _withBand(17, 12.0f))
        {
            double _centre = EffectHarness.MeasureGainDb(_eq, Centres[17], -30.0);
            double _threeUp = EffectHarness.MeasureGainDb(_eq, Centres[20], -30.0);
            double _far = EffectHarness.MeasureGainDb(_eq, Centres[26], -30.0);

            _threeUp.Should().BeLessThan(_centre - 4.0, "three bands up should already be well down");
            Math.Abs(_far).Should().BeLessThan(1.0, "and 8 kHz has nothing to do with a 1 kHz bell");
        }
    }

    /// <summary>
    /// Reading a band back gives what was written.
    /// </summary>
    [Fact]
    public void BandGainRoundTrips()
    {
        using (var _eq = new Equalizer30BandEffect(Rate))
        {
            _eq[12] = 7.5f;
            _eq[12].Should().BeApproximately(7.5f, 0.001f);
        }
    }

    /// <summary>
    /// Thirty biquads in series still have to be linear.
    /// </summary>
    [Fact]
    public void BoostingDoesNotAddDistortion()
    {
        using (Equalizer30BandEffect _eq = _withBand(17, 12.0f))
        {
            int _frames = EffectHarness.SettleFrames + Rate;
            float[] _in = SignalGenerator.Sine(1000, -30.0, _frames, EffectHarness.Channels, Rate);
            float[] _out = EffectHarness.Render(_eq, _in, EffectHarness.Channels);

            SignalMeasure.Thd(EffectHarness.Steady(_out, EffectHarness.Channels), 1000, Rate)
                .Should().BeLessThan(0.001);
        }
    }
}
