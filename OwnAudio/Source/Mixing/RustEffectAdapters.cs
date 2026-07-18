using System;
using System.Collections.Generic;
using OwnaudioNET.Interfaces;
using ME = OwnaudioNET.Effects;
using SM = OwnaudioNET.Effects.SmartMaster;
using EffectType = Ownaudio.Audio.Effects.EffectType;

namespace OwnaudioNET.Mixing;

/// <summary>
/// Pairs a managed effect with its native Rust twin and shoves the managed
/// parameters onto the native side. In the rust-native chain the managed
/// Process never runs — the managed object is just the parameter model the UI
/// binds to; the control-rate tick pushes its values across via Mirror.
/// VST3 isn't routed here (own native bridge); effects without an adapter get
/// no native processing.
/// </summary>
internal static class RustEffectAdapters
{
    /// <summary>Shared id: enable/bypass (0 = bypass).</summary>
    private const uint ParamEnabled = 0;

    /// <summary>Shared id: wet/dry mix (ignored where the native effect has none).</summary>
    private const uint ParamMix = 1;

    /// <summary>One native param change (id + value) for the paired effect.</summary>
    internal delegate void ParamSink(uint paramId, float value);

    /// <summary>
    /// Managed effect type → native type + the mirror that pushes its params.
    /// </summary>
    private sealed record Adapter(EffectType Type, Action<IEffectProcessor, ParamSink> Mirror);

    /// <summary>
    /// Keyed by concrete managed effect type.
    /// </summary>
    private static readonly IReadOnlyDictionary<Type, Adapter> _adapters =
        new Dictionary<Type, Adapter>
        {
            [typeof(ME.ReverbEffect)]           = new Adapter(EffectType.Reverb, _mirrorReverb),
            [typeof(ME.EqualizerEffect)]        = new Adapter(EffectType.Equalizer, _mirrorEqualizer),
            [typeof(ME.Equalizer30BandEffect)]  = new Adapter(EffectType.Equalizer30, _mirrorEqualizer30),
            [typeof(ME.CompressorEffect)]       = new Adapter(EffectType.Compressor, _mirrorCompressor),
            [typeof(ME.LimiterEffect)]          = new Adapter(EffectType.Limiter, _mirrorLimiter),
            [typeof(ME.DelayEffect)]            = new Adapter(EffectType.Delay, _mirrorDelay),
            [typeof(ME.ChorusEffect)]           = new Adapter(EffectType.Chorus, _mirrorChorus),
            [typeof(ME.DistortionEffect)]       = new Adapter(EffectType.Distortion, _mirrorDistortion),
            [typeof(ME.OverdriveEffect)]        = new Adapter(EffectType.Overdrive, _mirrorOverdrive),
            [typeof(ME.FlangerEffect)]          = new Adapter(EffectType.Flanger, _mirrorFlanger),
            [typeof(ME.PhaserEffect)]           = new Adapter(EffectType.Phaser, _mirrorPhaser),
            [typeof(ME.RotaryEffect)]           = new Adapter(EffectType.Rotary, _mirrorRotary),
            [typeof(ME.AutoGainEffect)]         = new Adapter(EffectType.AutoGain, _mirrorAutoGain),
            [typeof(ME.EnhancerEffect)]         = new Adapter(EffectType.Enhancer, _mirrorEnhancer),
            [typeof(ME.DynamicAmpEffect)]       = new Adapter(EffectType.DynamicAmp, _mirrorDynamicAmp),
            [typeof(SM.SmartMasterEffect)]      = new Adapter(EffectType.SmartMaster, _mirrorSmartMaster),
        };

    /// <summary>
    /// Native effect type for a managed effect, if we have an adapter for it.
    /// </summary>
    internal static bool TryGetEffectType(IEffectProcessor effect, out EffectType effectType)
    {
        if (effect is not null && _adapters.TryGetValue(effect.GetType(), out Adapter? adapter))
        {
            effectType = adapter.Type;
            return true;
        }

        effectType = default;
        return false;
    }

    /// <summary>
    /// Pushes the managed effect's current params onto its native twin: always
    /// the common enable + mix, then the effect-specific ones.
    /// </summary>
    internal static void Mirror(IEffectProcessor effect, ParamSink sink)
    {
        if (effect is null) return;

        sink(ParamEnabled, effect.Enabled ? 1f : 0f);
        sink(ParamMix, effect.Mix);

        if (_adapters.TryGetValue(effect.GetType(), out Adapter? adapter))
            adapter.Mirror(effect, sink);
    }

    // Per-effect mirrors. Managed props already speak native units, so every
    // param is a straight pass-through to its native id.

    private static void _mirrorReverb(IEffectProcessor e, ParamSink sink)
    {
        var r = (ME.ReverbEffect)e;
        sink(2, r.RoomSize);
        sink(3, r.Damping);
        sink(4, r.Width);
        sink(5, r.WetLevel);
        sink(6, r.DryLevel);
    }

    private static void _mirrorEqualizer(IEffectProcessor e, ParamSink sink)
    {
        var q = (ME.EqualizerEffect)e;
        sink(2, q.Band0Gain);
        sink(3, q.Band1Gain);
        sink(4, q.Band2Gain);
        sink(5, q.Band3Gain);
        sink(6, q.Band4Gain);
        sink(7, q.Band5Gain);
        sink(8, q.Band6Gain);
        sink(9, q.Band7Gain);
        sink(10, q.Band8Gain);
        sink(11, q.Band9Gain);
    }

    private static void _mirrorEqualizer30(IEffectProcessor e, ParamSink sink)
    {
        var q = (ME.Equalizer30BandEffect)e;
        for (int i = 0; i < 30; i++)
            sink((uint)(2 + i), q[i]);
    }

    private static void _mirrorCompressor(IEffectProcessor e, ParamSink sink)
    {
        var c = (ME.CompressorEffect)e;
        sink(2, c.Threshold);
        sink(3, c.Ratio);
        sink(4, c.AttackTime);
        sink(5, c.ReleaseTime);
        sink(6, c.MakeupGain);
    }

    private static void _mirrorLimiter(IEffectProcessor e, ParamSink sink)
    {
        var l = (ME.LimiterEffect)e;
        sink(2, l.Threshold);
        sink(3, l.Ceiling);
        sink(4, l.Release);
        sink(5, l.LookAheadMs);
    }

    private static void _mirrorDelay(IEffectProcessor e, ParamSink sink)
    {
        var d = (ME.DelayEffect)e;
        sink(2, d.Time);
        sink(3, d.Repeat);
        sink(4, d.Damping);
        sink(5, d.PingPong ? 1f : 0f);
    }

    private static void _mirrorChorus(IEffectProcessor e, ParamSink sink)
    {
        var c = (ME.ChorusEffect)e;
        sink(2, c.Rate);
        sink(3, c.Depth);
        sink(4, c.Voices);
    }

    private static void _mirrorDistortion(IEffectProcessor e, ParamSink sink)
    {
        var d = (ME.DistortionEffect)e;
        sink(2, d.Drive);
        sink(3, d.OutputGain);
    }

    private static void _mirrorOverdrive(IEffectProcessor e, ParamSink sink)
    {
        var o = (ME.OverdriveEffect)e;
        sink(2, o.Gain);
        sink(3, o.Tone);
        sink(4, o.OutputLevel);
    }

    private static void _mirrorFlanger(IEffectProcessor e, ParamSink sink)
    {
        var f = (ME.FlangerEffect)e;
        sink(2, f.Rate);
        sink(3, f.Depth);
        sink(4, f.Feedback);
    }

    private static void _mirrorPhaser(IEffectProcessor e, ParamSink sink)
    {
        var p = (ME.PhaserEffect)e;
        sink(2, p.Rate);
        sink(3, p.Depth);
        sink(4, p.Feedback);
        sink(5, p.Stages);
    }

    private static void _mirrorRotary(IEffectProcessor e, ParamSink sink)
    {
        var r = (ME.RotaryEffect)e;
        sink(2, r.HornSpeed);
        sink(3, r.RotorSpeed);
        sink(4, r.Intensity);
        sink(5, r.IsFast ? 1f : 0f);
    }

    private static void _mirrorAutoGain(IEffectProcessor e, ParamSink sink)
    {
        var a = (ME.AutoGainEffect)e;
        sink(2, a.TargetLevel);
        sink(3, a.AttackCoefficient);
        sink(4, a.ReleaseCoefficient);
        sink(5, a.MaximumGain);
        sink(6, a.MinimumGain);
        sink(7, a.GateThreshold);
    }

    private static void _mirrorEnhancer(IEffectProcessor e, ParamSink sink)
    {
        var h = (ME.EnhancerEffect)e;
        sink(2, h.Gain);
        sink(3, h.CutoffFrequency);
    }

    private static void _mirrorDynamicAmp(IEffectProcessor e, ParamSink sink)
    {
        var d = (ME.DynamicAmpEffect)e;
        sink(2, d.TargetRmsLevelDb);
        sink(3, d.AttackTime);
        sink(4, d.ReleaseTime);
        sink(5, d.NoiseGateThresholdDb);
        sink(6, d.MaxGain);
        sink(7, d.MaxGainReductionDb);
        sink(8, d.RmsWindowSeconds);
        sink(9, d.MaxGainChangePerSecondDb);
    }

    /// <summary>
    /// Mirrors the composite SmartMaster config onto its single native effect.
    /// Param layout matches the Rust smartmaster module: EQ gains 2–31 (dB),
    /// subharmonic 32–33, compressor 34–38, crossover 39, phase align 40–45,
    /// limiter 46–48. Enable/mix (0–1) come from the base Mirror.
    /// </summary>
    private static void _mirrorSmartMaster(IEffectProcessor e, ParamSink sink)
    {
        var sm = (SM.SmartMasterEffect)e;
        SM.SmartMasterConfig cfg = sm.GetConfiguration();

        // GraphicEQGains is oversized (31); take the first 30 for the native 30-band EQ.
        float[] eqGains = cfg.GraphicEQGains;
        for (int i = 0; i < 30; i++)
            sink((uint)(2 + i), (eqGains is not null && i < eqGains.Length) ? eqGains[i] : 0f);

        sink(32, cfg.SubharmonicEnabled ? 1f : 0f);
        sink(33, cfg.SubharmonicMix);

        // Compressor — threshold stays linear 0–1, native turns it into dB.
        sink(34, cfg.CompressorEnabled ? 1f : 0f);
        sink(35, cfg.CompressorThreshold);
        sink(36, cfg.CompressorRatio);
        sink(37, cfg.CompressorAttack);
        sink(38, cfg.CompressorRelease);

        sink(39, cfg.CrossoverFrequency);

        // Phase align: per-channel delay (ms) + polarity flip for L, R, Sub.
        float[] delays = cfg.TimeDelays;
        bool[] invert = cfg.PhaseInvert;
        sink(40, (delays is not null && delays.Length > 0) ? delays[0] : 0f);
        sink(41, (delays is not null && delays.Length > 1) ? delays[1] : 0f);
        sink(42, (delays is not null && delays.Length > 2) ? delays[2] : 0f);
        sink(43, (invert is not null && invert.Length > 0 && invert[0]) ? 1f : 0f);
        sink(44, (invert is not null && invert.Length > 1 && invert[1]) ? 1f : 0f);
        sink(45, (invert is not null && invert.Length > 2 && invert[2]) ? 1f : 0f);

        sink(46, cfg.LimiterThreshold);
        sink(47, cfg.LimiterCeiling);
        sink(48, cfg.LimiterRelease);
    }
}
