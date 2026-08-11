using System;
using Ownaudio.OwnaudioNET.Tests.Dsp.Harness;
using OwnaudioNET.Effects;
using OwnaudioNET.Interfaces;

namespace Ownaudio.OwnaudioNET.Tests.Dsp.Effects;

/// <summary>
/// Chorus, flanger, phaser and rotary. All four move something at an LFO rate, so a
/// steady sine going in has to come out with sidebands around it — that is the shared
/// signature, and each one gets its own check on top.
/// </summary>
public class ModulationEffectsDspTests
{
    private const int Ch = EffectHarness.Channels;
    private const int Rate = EffectHarness.SampleRate;
    private const double Tone = 1000.0;

    private static float[] _renderTone(IEffectProcessor fx, double levelDb = -12.0, int seconds = 2)
    {
        float[] _in = SignalGenerator.Sine(Tone, levelDb, Rate * seconds, Ch, Rate);
        return EffectHarness.Render(fx, _in, Ch);
    }

    /// <summary>
    /// Energy at the carrier plus or minus the LFO rate, relative to the carrier itself.
    /// A static effect leaves this at the noise floor.
    /// </summary>
    private static double _sidebandDb(float[] rendered, double lfoHz)
    {
        float[] _mono = EffectHarness.Steady(rendered, Ch);

        double _carrier = SignalMeasure.MagnitudeDbAt(_mono, Tone, Rate);
        double _upper = SignalMeasure.MagnitudeDbAt(_mono, Tone + lfoHz, Rate);
        double _lower = SignalMeasure.MagnitudeDbAt(_mono, Tone - lfoHz, Rate);

        return Math.Max(_upper, _lower) - _carrier;
    }

    /// <summary>
    /// A chorus at 2 Hz has to put sidebands 2 Hz either side of the carrier.
    /// </summary>
    [Fact]
    public void ChorusModulatesTheCarrier()
    {
        using (var _fx = new ChorusEffect(1.0f, 0.6f, 0.5f, 3, Rate))
        {
            _sidebandDb(_renderTone(_fx), 1.0).Should().BeGreaterThan(-40.0,
                "a modulated delay line has to smear the tone into sidebands");
        }
    }

    /// <summary>
    /// Stacking voices has to change the sound, not just cost more CPU.
    /// </summary>
    [Fact]
    public void MoreChorusVoicesThickenTheSound()
    {
        float[] _one, _three;

        using (var _fx = new ChorusEffect(0.8f, 0.6f, 1.0f, 1, Rate)) { _one = _renderTone(_fx); }
        using (var _fx = new ChorusEffect(0.8f, 0.6f, 1.0f, 3, Rate)) { _three = _renderTone(_fx); }

        SignalMeasure.MaxDiff(_one, _three).Should().BeGreaterThan(0.01,
            "three voices must not render the same as one");
    }

    /// <summary>
    /// A flanger is a swept comb, so the level at a fixed frequency has to swing over
    /// time as the notches move through it.
    /// </summary>
    [Fact]
    public void FlangerSweepsANotchAcrossTheSignal()
    {
        using (var _fx = new FlangerEffect(0.5f, 0.9f, 0.7f, 1.0f, Rate))
        {
            float[] _out = _renderTone(_fx, seconds: 4);

            double _min = double.MaxValue, _max = double.MinValue;
            for (int f = EffectHarness.SettleFrames; f + Rate / 20 < _out.Length / Ch; f += Rate / 20)
            {
                double _level = SignalMeasure.RmsDbOfFrames(_out, Ch, f, Rate / 20);
                if (_level < _min) _min = _level;
                if (_level > _max) _max = _level;
            }

            (_max - _min).Should().BeGreaterThan(3.0,
                $"the sweeping comb has to move the level around, but it only varied {_max - _min:F1} dB");
        }
    }

    /// <summary>
    /// Feedback deepens the comb, so the swing gets bigger.
    /// </summary>
    [Fact]
    public void MoreFlangerFeedbackDeepensTheSweep()
    {
        double _shallow = _levelSwing(new FlangerEffect(0.5f, 0.9f, 0.05f, 1.0f, Rate));
        double _deep = _levelSwing(new FlangerEffect(0.5f, 0.9f, 0.85f, 1.0f, Rate));

        _deep.Should().BeGreaterThan(_shallow, "a resonant flanger swings harder than a flat one");
    }

    private static double _levelSwing(IEffectProcessor fx) => _levelSwingAt(fx, Tone);

    /// <summary>
    /// Biggest minus smallest block level over the run, in dB — how much the effect moves
    /// the level of a steady tone around.
    /// </summary>
    private static double _levelSwingAt(IEffectProcessor fx, double probeHz)
    {
        using (fx)
        {
            float[] _in = SignalGenerator.Sine(probeHz, -12.0, Rate * 4, Ch, Rate);
            float[] _out = EffectHarness.Render(fx, _in, Ch);

            double _min = double.MaxValue, _max = double.MinValue;
            for (int f = EffectHarness.SettleFrames; f + Rate / 20 < _out.Length / Ch; f += Rate / 20)
            {
                double _level = SignalMeasure.RmsDbOfFrames(_out, Ch, f, Rate / 20);
                if (_level < _min) _min = _level;
                if (_level > _max) _max = _level;
            }

            return _max - _min;
        }
    }

    /// <summary>
    /// Characterisation, not a spec: the phaser currently produces almost no sweep. The
    /// all-pass stages use a = (1-t)/(1+t) where t = tan(pi*f/fs), which places the corner
    /// near 23 kHz for a nominal 1 kHz setting instead of at 1 kHz — the sign of the
    /// coefficient is the wrong way round, so the chain barely shifts phase in the audio
    /// band and dry plus wet never cancels. The native Phaser is a line-for-line port and
    /// behaves the same, so this is not a managed-versus-native gap.
    ///
    /// Fixing it changes how every phaser preset sounds, so the number below is what ships
    /// today. If someone corrects the coefficient this test fails, which is the point.
    /// </summary>
    [Theory]
    [InlineData(300.0)]
    [InlineData(1000.0)]
    [InlineData(4000.0)]
    public void PhaserSweepIsCurrentlyAlmostInaudible(double probeHz)
    {
        double _swing = _levelSwingAt(new PhaserEffect(0.6f, 0.9f, 0.7f, 0.5f, 6, Rate), probeHz);

        _swing.Should().BeLessThan(0.5,
            $"the notch never reaches {probeHz:F0} Hz with the current all-pass coefficient " +
            $"(measured {_swing:F3} dB of level movement; a working phaser would swing several dB)");
    }

    /// <summary>
    /// Fully wet with no feedback the chain is flat in magnitude, which is what an all-pass
    /// should be. This part is correct and worth keeping correct.
    /// </summary>
    [Fact]
    public void FullyWetPhaserIsFlatInMagnitude()
    {
        _levelSwing(new PhaserEffect(0.6f, 0.9f, 0.0f, 1.0f, 6, Rate))
            .Should().BeLessThan(0.5, "an all-pass with no feedback does not change the magnitude");
    }

    /// <summary>
    /// Whatever the notch is doing, the stage count still has to reach the output.
    /// </summary>
    [Fact]
    public void StageCountChangesTheOutput()
    {
        float[] _two, _eight;

        using (var _fx = new PhaserEffect(0.6f, 0.9f, 0.7f, 0.5f, 2, Rate)) { _two = _renderTone(_fx); }
        using (var _fx = new PhaserEffect(0.6f, 0.9f, 0.7f, 0.5f, 8, Rate)) { _eight = _renderTone(_fx); }

        SignalMeasure.MaxDiff(_two, _eight).Should().BeGreaterThan(0.001,
            "8 stages must not render identically to 2");
    }

    /// <summary>
    /// The cabinet splits at 800 Hz, so a 1 kHz tone rides the horn and gets modulated at
    /// the horn rate — the rotor never sees it.
    /// </summary>
    [Fact]
    public void RotaryModulatesTheHornBandAtTheHornRate()
    {
        using (var _fx = new RotaryEffect(6.0f, 1.0f, 1.0f, 1.0f, false, Rate))
        {
            _sidebandDb(_renderTone(_fx), 6.0).Should().BeGreaterThan(-40.0,
                "a 1 kHz tone goes to the horn, which spins at 6 Hz here");
        }
    }

    /// <summary>
    /// And a bass tone goes the other way, to the drum.
    /// </summary>
    [Fact]
    public void RotaryModulatesTheDrumBandAtTheRotorRate()
    {
        using (var _fx = new RotaryEffect(0.8f, 5.0f, 1.0f, 1.0f, false, Rate))
        {
            float[] _in = SignalGenerator.Sine(200, -12.0, Rate * 2, Ch, Rate);
            float[] _out = EffectHarness.Render(_fx, _in, Ch);
            float[] _mono = EffectHarness.Steady(_out, Ch);

            double _carrier = SignalMeasure.MagnitudeDbAt(_mono, 200, Rate);
            double _side = Math.Max(SignalMeasure.MagnitudeDbAt(_mono, 205, Rate),
                                    SignalMeasure.MagnitudeDbAt(_mono, 195, Rate));

            (_side - _carrier).Should().BeGreaterThan(-40.0, "200 Hz is under the 800 Hz split, so the drum has it");
        }
    }

    /// <summary>
    /// The fast setting really does spin faster, so the level wobbles at a higher rate.
    /// </summary>
    [Fact]
    public void RotaryFastModeSpinsFaster()
    {
        int _slow = _levelCrossings(new RotaryEffect(0.8f, 0.7f, 1.0f, 1.0f, false, Rate));
        int _fast = _levelCrossings(new RotaryEffect(0.8f, 0.7f, 1.0f, 1.0f, true, Rate));

        _fast.Should().BeGreaterThan(_slow, $"fast mode wobbled {_fast} times against {_slow} slow");
    }

    /// <summary>
    /// Counts how often the block level crosses its own mean — a cheap modulation rate.
    /// </summary>
    private static int _levelCrossings(IEffectProcessor fx)
    {
        using (fx)
        {
            float[] _out = _renderTone(fx, seconds: 6);

            int _block = Rate / 100;
            int _start = EffectHarness.SettleFrames;
            int _count = (_out.Length / Ch - _start) / _block;

            double[] _levels = new double[_count];
            double _sum = 0.0;
            for (int i = 0; i < _count; i++)
            {
                _levels[i] = SignalMeasure.RmsDbOfFrames(_out, Ch, _start + i * _block, _block);
                _sum += _levels[i];
            }

            double _mean = _sum / _count;
            int _crossings = 0;
            for (int i = 1; i < _count; i++)
            {
                if ((_levels[i - 1] - _mean) * (_levels[i] - _mean) < 0) _crossings++;
            }

            return _crossings;
        }
    }
}
