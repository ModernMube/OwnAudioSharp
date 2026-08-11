using System;
using Ownaudio.OwnaudioNET.Tests.Dsp.Harness;
using OwnaudioNET.Effects;
using OwnaudioNET.Interfaces;

namespace Ownaudio.OwnaudioNET.Tests.Dsp.Effects;

/// <summary>
/// Distortion, overdrive and the harmonic enhancer. All three make harmonics on purpose,
/// so the tests measure THD and the harmonic pattern rather than levels.
/// </summary>
public class SaturationEffectsDspTests
{
    private const int Ch = EffectHarness.Channels;
    private const int Rate = EffectHarness.SampleRate;
    private const double Tone = 500.0;

    private static float[] _steadyTone(IEffectProcessor fx, double levelDb = -12.0)
    {
        int _frames = EffectHarness.SettleFrames + Rate;
        float[] _in = SignalGenerator.Sine(Tone, levelDb, _frames, Ch, Rate);

        return EffectHarness.Steady(EffectHarness.Render(fx, _in, Ch), Ch);
    }

    private static double _harmonicDb(float[] mono, int harmonic)
        => SignalMeasure.MagnitudeDbAt(mono, Tone * harmonic, Rate) - SignalMeasure.MagnitudeDbAt(mono, Tone, Rate);

    /// <summary>
    /// Distortion has to actually distort.
    /// </summary>
    [Fact]
    public void DistortionAddsHarmonics()
    {
        using (var _fx = new DistortionEffect(6.0f, 1.0f, 0.5f))
        {
            SignalMeasure.Thd(_steadyTone(_fx), Tone, Rate)
                .Should().BeGreaterThan(0.05, "a driven soft clipper should be well past 5% THD");
        }
    }

    /// <summary>
    /// And more drive has to mean more of it.
    /// </summary>
    [Fact]
    public void MoreDriveMeansMoreDistortion()
    {
        float[] _drives = { 1.0f, 3.0f, 8.0f, 20.0f };
        double _previous = -1.0;

        foreach (float _d in _drives)
        {
            using (var _fx = new DistortionEffect(_d, 1.0f, 0.5f))
            {
                double _thd = SignalMeasure.Thd(_steadyTone(_fx), Tone, Rate);

                _thd.Should().BeGreaterThan(_previous, $"drive {_d:F0} has to distort more than the step before it");
                _previous = _thd;
            }
        }
    }

    /// <summary>
    /// However hard it is pushed the clipper saturates instead of running away. The soft
    /// clip curve asymptotes at 2.0, i.e. +6 dBFS, and OutputGain is what brings that back
    /// — worth knowing before putting this last in a chain.
    /// </summary>
    [Fact]
    public void DistortionSaturatesInsteadOfRunningAway()
    {
        using (var _fx = new DistortionEffect(50.0f, 1.0f, 1.0f))
        {
            SignalMeasure.PeakDb(_steadyTone(_fx, -1.0)).Should().BeLessThan(6.1,
                "the soft clip curve tops out at 2.0 no matter the drive");
        }

        using (var _fx = new DistortionEffect(50.0f, 1.0f, 0.4f))
        {
            SignalMeasure.PeakDb(_steadyTone(_fx, -1.0)).Should().BeLessThan(-1.9,
                "OutputGain scales that ceiling straight down");
        }
    }

    /// <summary>
    /// Symmetric clipping folds both halves the same way, so odd harmonics dominate.
    /// </summary>
    [Fact]
    public void DistortionFavoursOddHarmonics()
    {
        using (var _fx = new DistortionEffect(10.0f, 1.0f, 0.5f))
        {
            float[] _out = _steadyTone(_fx);

            _harmonicDb(_out, 3).Should().BeGreaterThan(_harmonicDb(_out, 2) + 6.0,
                "a symmetric transfer curve makes third harmonic, not second");
        }
    }

    /// <summary>
    /// Overdrive is the asymmetric one, so the even harmonics show up too.
    /// </summary>
    [Fact]
    public void OverdriveProducesEvenHarmonics()
    {
        using (var _fx = new OverdriveEffect(8.0f, 0.5f, 1.0f, 0.7f))
        {
            float[] _out = _steadyTone(_fx);

            _harmonicDb(_out, 2).Should().BeGreaterThan(-45.0,
                "an asymmetric tube curve has a second harmonic; a symmetric one would not");
        }
    }

    /// <summary>
    /// Turning the gain up drives it harder.
    /// </summary>
    [Fact]
    public void MoreOverdriveGainMeansMoreDistortion()
    {
        double _clean, _dirty;

        using (var _fx = new OverdriveEffect(1.0f, 0.5f, 1.0f, 0.7f)) { _clean = SignalMeasure.Thd(_steadyTone(_fx), Tone, Rate); }
        using (var _fx = new OverdriveEffect(20.0f, 0.5f, 1.0f, 0.7f)) { _dirty = SignalMeasure.Thd(_steadyTone(_fx), Tone, Rate); }

        _dirty.Should().BeGreaterThan(_clean);
    }

    /// <summary>
    /// Tone at 1 has to let more top end through than tone at 0. Measured as absolute
    /// level at the fifth harmonic, not relative to the fundamental — the wider low-pass
    /// lifts the fundamental too, so a ratio would read backwards.
    /// </summary>
    [Fact]
    public void OverdriveToneOpensUpTheTopEnd()
    {
        double _dark, _bright;

        using (var _fx = new OverdriveEffect(10.0f, 0.0f, 1.0f, 0.7f))
            _dark = SignalMeasure.MagnitudeDbAt(_steadyTone(_fx), Tone * 5, Rate);

        using (var _fx = new OverdriveEffect(10.0f, 1.0f, 1.0f, 0.7f))
            _bright = SignalMeasure.MagnitudeDbAt(_steadyTone(_fx), Tone * 5, Rate);

        _bright.Should().BeGreaterThan(_dark + 1.0,
            $"tone 1.0 ({_bright:F1} dB at the 5th) has to be brighter than tone 0.0 ({_dark:F1} dB)");
    }

    /// <summary>
    /// The enhancer only has the band above its crossover to work with, so it generates
    /// harmonics from a tone that sits up there.
    /// </summary>
    [Fact]
    public void EnhancerAddsHarmonicsToContentAboveTheCrossover()
    {
        using (var _fx = new EnhancerEffect(1.0f, 2000f, 6.0f, Rate))
        {
            int _frames = EffectHarness.SettleFrames + Rate;
            float[] _in = SignalGenerator.Sine(3000, -12.0, _frames, Ch, Rate);
            float[] _out = EffectHarness.Steady(EffectHarness.Render(_fx, _in, Ch), Ch);

            SignalMeasure.Thd(_out, 3000, Rate).Should().BeGreaterThan(0.01,
                "a 3 kHz tone is above the 2 kHz corner, so the tanh stage has something to bite on");
        }
    }

    /// <summary>
    /// Content under the crossover is left alone.
    /// </summary>
    [Fact]
    public void EnhancerLeavesContentBelowTheCrossoverAlone()
    {
        using (var _fx = new EnhancerEffect(1.0f, 8000f, 6.0f, Rate))
        {
            SignalMeasure.Thd(_steadyTone(_fx), Tone, Rate).Should().BeLessThan(0.005,
                "a 500 Hz tone is four octaves under an 8 kHz corner");
        }
    }

    /// <summary>
    /// Mix at zero is the dry signal for all three.
    /// </summary>
    [Theory]
    [InlineData("distortion")]
    [InlineData("overdrive")]
    [InlineData("enhancer")]
    public void ZeroMixIsFullyDry(string which)
    {
        IEffectProcessor _fx = which switch
        {
            "distortion" => new DistortionEffect(10.0f, 0.0f, 1.0f),
            "overdrive" => new OverdriveEffect(10.0f, 0.5f, 0.0f, 1.0f),
            _ => new EnhancerEffect(0.0f, 3000f, 4.0f, Rate)
        };

        using (_fx)
        {
            float[] _in = SignalGenerator.Sine(Tone, -12.0, 4096, Ch, Rate);
            float[] _out = EffectHarness.Render(_fx, _in, Ch);

            SignalMeasure.MaxDiff(_in, _out).Should().BeLessThan(1e-5, $"{which} at Mix=0 must not change anything");
        }
    }
}
