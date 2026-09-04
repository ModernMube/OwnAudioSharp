using System;
using System.Collections.Generic;
using OwnaudioNET.Effects;
using OwnaudioNET.Effects.SmartMaster;
using OwnaudioNET.Interfaces;

namespace Ownaudio.OwnaudioNET.Tests.Dsp.Harness;

/// <summary>
/// One built-in effect plus what the invariant suite is allowed to expect from it.
/// Anything set to false here is a documented deviation, not a silent skip.
/// </summary>
public sealed record EffectCase(string Name, Func<IEffectProcessor> Create)
{
    /// <summary>
    /// Mix = 0 gives back the dry signal.
    /// </summary>
    public bool MixHonored { get; init; } = true;

    /// <summary>
    /// Silence in, silence out.
    /// </summary>
    public bool SilenceStaysSilent { get; init; } = true;

    /// <summary>
    /// One gain value per Process() call rather than per sample, so the output does
    /// depend on the host block size. Only the settled level is comparable.
    /// </summary>
    public bool BlockRateGain { get; init; }

    /// <summary>
    /// Why one of the flags above is off. Shows up in the test output so the gap
    /// stays visible instead of disappearing.
    /// </summary>
    public string? Deviation { get; init; }
}

/// <summary>
/// Every built-in effect the DSP suite runs over. VST3 is out — it needs a real plugin.
/// </summary>
public static class EffectCatalog
{
    private const int Rate = EffectHarness.SampleRate;

    private static readonly Dictionary<string, EffectCase> _cases = _build();

    /// <summary>
    /// Effect names, for MemberData. Strings keep the test output readable.
    /// </summary>
    public static IEnumerable<object[]> Names
    {
        get
        {
            foreach (string _n in _cases.Keys)
                yield return new object[] { _n };
        }
    }

    /// <summary>
    /// Looks up a case by the name that came out of <see cref="Names"/>.
    /// </summary>
    public static EffectCase Get(string name) => _cases[name];

    private static Dictionary<string, EffectCase> _build()
    {
        var _all = new List<EffectCase>
        {
            new EffectCase("AutoGain", () => new AutoGainEffect(AutoGainPreset.Default))
            {
                MixHonored = false,
                Deviation = "AutoGain has no wet/dry path — Mix is hardwired to 1.0."
            },

            new EffectCase("Chorus", () => new ChorusEffect(ChorusPreset.Default, Rate)),

            new EffectCase("Compressor", () => new CompressorEffect(CompressorPreset.Default, Rate))
            {
                MixHonored = false,
                Deviation = "Compressor is always fully wet by design; parallel compression is not offered."
            },

            new EffectCase("Delay", () => new DelayEffect(sampleRate: Rate)),
            new EffectCase("Distortion", () => new DistortionEffect(DistortionPreset.Default)),

            new EffectCase("DynamicAmp", () => new DynamicAmpEffect(DynamicAmpPreset.Default, Rate))
            {
                MixHonored = false,
                BlockRateGain = true,
                Deviation = "Slow AGC: no dry path, and one gain per Process() call, so its output " +
                            "tracks the host block size. Only the settled level is block-size independent."
            },

            new EffectCase("Enhancer", () => new EnhancerEffect(EnhancerPreset.Default, Rate)),
            new EffectCase("Equalizer", () => new EqualizerEffect(Rate, 6f, 0f, -6f, 0f, 0f, 0f, 0f, 3f, 0f, 0f)),

            new EffectCase("Equalizer30Band", () => _eq30()),

            new EffectCase("Flanger", () => new FlangerEffect(FlangerPreset.Default, Rate)),

            //The limiter takes its rate from the ctor only — Initialize() ignores config.SampleRate
            new EffectCase("Limiter", () => new LimiterEffect(Rate, LimiterPreset.Default))
            {
                MixHonored = false,
                Deviation = "Process() never reads Mix; the limiter is always fully wet."
            },

            new EffectCase("Overdrive", () => new OverdriveEffect(OverdrivePreset.Default)),
            new EffectCase("Phaser", () => new PhaserEffect(PhaserPreset.Default, Rate)),
            new EffectCase("Reverb", () => new ReverbEffect(ReverbPreset.Default)),
            new EffectCase("Rotary", () => new RotaryEffect(RotaryPreset.Default, Rate)),

            new EffectCase("SmartMaster", () => new SmartMasterEffect())
            {
                MixHonored = false,
                Deviation = "Mastering chain, always fully wet — the Mix property is inert."
            }
        };

        var _map = new Dictionary<string, EffectCase>();
        foreach (EffectCase _c in _all)
            _map[_c.Name] = _c;

        return _map;
    }

    private static Equalizer30BandEffect _eq30()
    {
        var _eq = new Equalizer30BandEffect(Rate);
        _eq[10] = 6.0f;
        return _eq;
    }
}
