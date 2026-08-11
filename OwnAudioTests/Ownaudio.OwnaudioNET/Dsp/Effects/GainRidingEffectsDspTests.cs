using System;
using Ownaudio.OwnaudioNET.Tests.Dsp.Harness;
using OwnaudioNET.Effects;
using OwnaudioNET.Interfaces;

namespace Ownaudio.OwnaudioNET.Tests.Dsp.Effects;

/// <summary>
/// The two automatic level riders. Both are slow, so everything here is measured on the
/// last second of a long tone, once the gain has finished moving.
/// </summary>
public class GainRidingEffectsDspTests
{
    private const int Ch = EffectHarness.Channels;
    private const int Rate = EffectHarness.SampleRate;
    private const double Tone = 440.0;

    /// <summary>
    /// Settled RMS in dB after letting the effect run for the given number of seconds.
    /// </summary>
    private static double _settledRmsDb(IEffectProcessor fx, double inputDb, int seconds)
    {
        int _frames = Rate * seconds;
        float[] _in = SignalGenerator.Sine(Tone, inputDb, _frames, Ch, Rate);
        float[] _out = EffectHarness.Render(fx, _in, Ch);

        return SignalMeasure.RmsDbOfFrames(_out, Ch, _frames - Rate, Rate);
    }

    /// <summary>
    /// A quiet source gets lifted toward the target instead of being left where it was.
    /// </summary>
    [Fact]
    public void AutoGainLiftsAQuietSource()
    {
        using (var _fx = new AutoGainEffect(AutoGainPreset.Default))
        {
            double _quiet = _settledRmsDb(_fx, -34.0, 8);
            _quiet.Should().BeGreaterThan(-30.0, "AutoGain is meant to bring a -34 dB source up");
        }
    }

    /// <summary>
    /// And a hot one gets pulled down.
    /// </summary>
    [Fact]
    public void AutoGainPullsALoudSourceDown()
    {
        using (var _fx = new AutoGainEffect(AutoGainPreset.Default))
        {
            double _loud = _settledRmsDb(_fx, -3.0, 8);
            _loud.Should().BeLessThan(-4.0, "a -3 dB source is over any sensible target");
        }
    }

    /// <summary>
    /// Whatever it does, it stays inside the gain limits it was given.
    /// </summary>
    [Fact]
    public void AutoGainRespectsItsGainLimits()
    {
        using (var _fx = new AutoGainEffect(AutoGainPreset.Default))
        {
            _fx.MaximumGain = 2.0f;
            _fx.MinimumGain = 0.5f;

            _settledRmsDb(_fx, -50.0, 8);

            _fx.CurrentGain.Should().BeInRange(0.5f, 2.0f,
                "even a very quiet source may not push the gain past its ceiling");
        }
    }

    /// <summary>
    /// Under the gate the rider leaves things alone rather than hauling the noise floor up.
    /// </summary>
    [Fact]
    public void AutoGainDoesNotChaseSignalUnderTheGate()
    {
        using (var _fx = new AutoGainEffect(AutoGainPreset.Default))
        {
            _fx.GateThreshold = 0.01f;

            double _in = -80.0;
            double _out = _settledRmsDb(_fx, _in, 4);

            (_out - _in).Should().BeLessThan(6.0, "-80 dBFS is under the gate and should not be amplified hard");
        }
    }

    /// <summary>
    /// DynamicAmp drives the RMS to the level it was asked for.
    /// </summary>
    [Theory]
    [InlineData(-20.0)]
    [InlineData(-26.0)]
    public void DynamicAmpConvergesOnItsTargetLevel(double inputDb)
    {
        using (var _fx = new DynamicAmpEffect(DynamicAmpPreset.Default, Rate))
        {
            _fx.TargetRmsLevelDb = -12.0f;

            double _settled = _settledRmsDb(_fx, inputDb, 12);
            _settled.Should().BeApproximately(-12.0, 1.5,
                $"a {inputDb:F0} dBFS tone is within reach of the -12 dB target");
        }
    }

    /// <summary>
    /// Too quiet to reach the target and the lift stops at MaxGain rather than running away.
    /// </summary>
    [Fact]
    public void DynamicAmpStopsAtItsMaximumGain()
    {
        using (var _fx = new DynamicAmpEffect(DynamicAmpPreset.Default, Rate))
        {
            _fx.TargetRmsLevelDb = -6.0f;
            _fx.MaxGain = 2.0f;

            _settledRmsDb(_fx, -40.0, 12);

            _fx.CurrentGain.Should().BeLessThanOrEqualTo(2.01f, "MaxGain is a hard ceiling");
        }
    }

    /// <summary>
    /// A source over the target comes down to it.
    /// </summary>
    [Fact]
    public void DynamicAmpPullsAHotSourceDown()
    {
        using (var _fx = new DynamicAmpEffect(DynamicAmpPreset.Default, Rate))
        {
            _fx.TargetRmsLevelDb = -20.0f;

            double _settled = _settledRmsDb(_fx, -6.0, 12);
            _settled.Should().BeLessThan(-14.0, "a -6 dB tone has to be brought back toward -20");
        }
    }

    /// <summary>
    /// The gate's job is to freeze the gain on a quiet passage, not to chase it up to the
    /// ceiling. With the gate open on the same signal the rider runs all the way to MaxGain,
    /// with it closed it stops well short. The ctor primes the level estimate at -20 dBFS,
    /// so the gate does open for an instant at startup either way — what matters is that it
    /// then stops climbing.
    /// </summary>
    [Fact]
    public void NoiseGateStopsDynamicAmpChasingAQuietPassage()
    {
        float _gated = _gainOnQuietInput(gateDb: -40.0f);
        float _open = _gainOnQuietInput(gateDb: -90.0f);

        _open.Should().BeGreaterThan(5.5f, "with the gate out of the way the rider runs up to its 6x ceiling");
        _gated.Should().BeLessThan(5.0f,
            $"a -40 dB gate has to stop the climb short of the ceiling ({_gated:F2}x against {_open:F2}x ungated)");
    }

    private static float _gainOnQuietInput(float gateDb)
    {
        using (var _fx = new DynamicAmpEffect(DynamicAmpPreset.Default, Rate))
        {
            _fx.NoiseGateThresholdDb = gateDb;
            _fx.TargetRmsLevelDb = -12.0f;
            _fx.MaxGain = 6.0f;

            _settledRmsDb(_fx, -70.0, 8);
            return _fx.CurrentGain;
        }
    }
}
