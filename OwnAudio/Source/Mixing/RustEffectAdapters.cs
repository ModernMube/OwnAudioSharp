using System;
using System.Collections.Generic;
using OwnaudioNET.Interfaces;
using ME = OwnaudioNET.Effects;
using EffectType = Ownaudio.Audio.Effects.EffectType;

namespace OwnaudioNET.Mixing;

/// <summary>
/// Maps managed <see cref="IEffectProcessor"/> effects to their native Rust
/// counterparts and mirrors their parameters onto the native effect (plan E.3/E.4).
/// </summary>
/// <remarks>
/// <para>
/// In the Rust-native chain the managed effect's <see cref="IEffectProcessor.Process"/> never runs;
/// the native effect does the audio. The managed effect object remains the parameter model (the UI
/// binds to it), and its parameters are pushed onto the paired native effect by the control-rate
/// sync tick through <see cref="Mirror"/>. The managed public properties are designed to expose the
/// same units as the native effect parameters (dB, ms, Hz, 0–1), so the mirror is a direct
/// pass-through per parameter — the linear↔dB / ms↔seconds conversions live inside the managed
/// properties themselves.
/// </para>
/// <para>
/// Only the built-in effect types with a registered adapter are routed to native. Effects without a
/// native counterpart — VST3 (see plan E.6) and the composite <c>SmartMasterEffect</c> — are skipped
/// and produce no native processing until their dedicated support lands.
/// </para>
/// </remarks>
internal static class RustEffectAdapters
{
    /// <summary>Native parameter id shared by every effect: enable/bypass (0 = bypass).</summary>
    private const uint ParamEnabled = 0;

    /// <summary>Native parameter id shared by most effects: wet/dry mix (ignored where unsupported).</summary>
    private const uint ParamMix = 1;

    /// <summary>Receives one native parameter change (id + value) for the paired effect.</summary>
    internal delegate void ParamSink(uint paramId, float value);

    private sealed record Adapter(EffectType Type, Action<IEffectProcessor, ParamSink> Mirror);

    /// <summary>
    /// Registry keyed by the concrete managed effect type → native effect type + parameter mirror.
    /// </summary>
    private static readonly IReadOnlyDictionary<Type, Adapter> Adapters =
        new Dictionary<Type, Adapter>
        {
            [typeof(ME.ReverbEffect)]           = new Adapter(EffectType.Reverb, MirrorReverb),
            [typeof(ME.EqualizerEffect)]        = new Adapter(EffectType.Equalizer, MirrorEqualizer),
            [typeof(ME.Equalizer30BandEffect)]  = new Adapter(EffectType.Equalizer30, MirrorEqualizer30),
            [typeof(ME.CompressorEffect)]       = new Adapter(EffectType.Compressor, MirrorCompressor),
            [typeof(ME.LimiterEffect)]          = new Adapter(EffectType.Limiter, MirrorLimiter),
            [typeof(ME.DelayEffect)]            = new Adapter(EffectType.Delay, MirrorDelay),
            [typeof(ME.ChorusEffect)]           = new Adapter(EffectType.Chorus, MirrorChorus),
            [typeof(ME.DistortionEffect)]       = new Adapter(EffectType.Distortion, MirrorDistortion),
            [typeof(ME.OverdriveEffect)]        = new Adapter(EffectType.Overdrive, MirrorOverdrive),
            [typeof(ME.FlangerEffect)]          = new Adapter(EffectType.Flanger, MirrorFlanger),
            [typeof(ME.PhaserEffect)]           = new Adapter(EffectType.Phaser, MirrorPhaser),
            [typeof(ME.RotaryEffect)]           = new Adapter(EffectType.Rotary, MirrorRotary),
            [typeof(ME.AutoGainEffect)]         = new Adapter(EffectType.AutoGain, MirrorAutoGain),
            [typeof(ME.EnhancerEffect)]         = new Adapter(EffectType.Enhancer, MirrorEnhancer),
            [typeof(ME.DynamicAmpEffect)]       = new Adapter(EffectType.DynamicAmp, MirrorDynamicAmp),
        };

    /// <summary>
    /// Resolves the native <see cref="EffectType"/> for a managed effect, when an adapter exists.
    /// </summary>
    internal static bool TryGetEffectType(IEffectProcessor effect, out EffectType effectType)
    {
        if (effect is not null && Adapters.TryGetValue(effect.GetType(), out Adapter? adapter))
        {
            effectType = adapter.Type;
            return true;
        }

        effectType = default;
        return false;
    }

    /// <summary>
    /// Pushes the managed effect's current parameters onto its paired native effect via
    /// <paramref name="sink"/>. Always mirrors the common enable and mix parameters (mix is ignored
    /// natively where the effect has none), then the effect-specific parameters.
    /// </summary>
    internal static void Mirror(IEffectProcessor effect, ParamSink sink)
    {
        if (effect is null)
        {
            return;
        }

        sink(ParamEnabled, effect.Enabled ? 1f : 0f);
        sink(ParamMix, effect.Mix);

        if (Adapters.TryGetValue(effect.GetType(), out Adapter? adapter))
        {
            adapter.Mirror(effect, sink);
        }
    }

    // -- Per-effect parameter adapters --------------------------------------
    // The managed public properties already expose native-compatible units, so each parameter is a
    // direct pass-through to the matching native parameter id.

    private static void MirrorReverb(IEffectProcessor e, ParamSink sink)
    {
        var r = (ME.ReverbEffect)e;
        sink(2, r.RoomSize);   // 0–1
        sink(3, r.Damping);    // 0–1
        sink(4, r.Width);      // stereo width
        sink(5, r.WetLevel);   // 0–1
        sink(6, r.DryLevel);   // 0–1
    }

    private static void MirrorEqualizer(IEffectProcessor e, ParamSink sink)
    {
        var q = (ME.EqualizerEffect)e;
        // Native band params: Band0=2 … Band9=11 (gain in dB).
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

    private static void MirrorEqualizer30(IEffectProcessor e, ParamSink sink)
    {
        var q = (ME.Equalizer30BandEffect)e;
        // Native band params: Band0=2 … Band29=31 (gain in dB); managed exposes bands via indexer.
        for (int i = 0; i < 30; i++)
        {
            sink((uint)(2 + i), q[i]);
        }
    }

    private static void MirrorCompressor(IEffectProcessor e, ParamSink sink)
    {
        var c = (ME.CompressorEffect)e;
        sink(2, c.Threshold);    // dB (managed property is dB; ctor arg is linear)
        sink(3, c.Ratio);        // N:1
        sink(4, c.AttackTime);   // ms
        sink(5, c.ReleaseTime);  // ms
        sink(6, c.MakeupGain);   // dB
    }

    private static void MirrorLimiter(IEffectProcessor e, ParamSink sink)
    {
        var l = (ME.LimiterEffect)e;
        // Native limiter has no mix param; threshold/ceiling in dB, release/lookahead in ms.
        sink(2, l.Threshold);
        sink(3, l.Ceiling);
        sink(4, l.Release);
        sink(5, l.LookAheadMs);
    }

    private static void MirrorDelay(IEffectProcessor e, ParamSink sink)
    {
        var d = (ME.DelayEffect)e;
        sink(2, d.Time);                  // ms
        sink(3, d.Repeat);                // feedback amount
        sink(4, d.Damping);               // 0–1
        sink(5, d.PingPong ? 1f : 0f);
    }

    private static void MirrorChorus(IEffectProcessor e, ParamSink sink)
    {
        var c = (ME.ChorusEffect)e;
        sink(2, c.Rate);       // Hz
        sink(3, c.Depth);      // 0–1
        sink(4, c.Voices);     // count
    }

    private static void MirrorDistortion(IEffectProcessor e, ParamSink sink)
    {
        var d = (ME.DistortionEffect)e;
        sink(2, d.Drive);
        sink(3, d.OutputGain);
    }

    private static void MirrorOverdrive(IEffectProcessor e, ParamSink sink)
    {
        var o = (ME.OverdriveEffect)e;
        sink(2, o.Gain);
        sink(3, o.Tone);
        sink(4, o.OutputLevel);
    }

    private static void MirrorFlanger(IEffectProcessor e, ParamSink sink)
    {
        var f = (ME.FlangerEffect)e;
        sink(2, f.Rate);       // Hz
        sink(3, f.Depth);      // 0–1
        sink(4, f.Feedback);   // 0–1
    }

    private static void MirrorPhaser(IEffectProcessor e, ParamSink sink)
    {
        var p = (ME.PhaserEffect)e;
        sink(2, p.Rate);       // Hz
        sink(3, p.Depth);      // 0–1
        sink(4, p.Feedback);   // 0–1
        sink(5, p.Stages);     // count
    }

    private static void MirrorRotary(IEffectProcessor e, ParamSink sink)
    {
        var r = (ME.RotaryEffect)e;
        sink(2, r.HornSpeed);
        sink(3, r.RotorSpeed);
        sink(4, r.Intensity);
        sink(5, r.IsFast ? 1f : 0f);
    }

    private static void MirrorAutoGain(IEffectProcessor e, ParamSink sink)
    {
        var a = (ME.AutoGainEffect)e;
        // Native auto-gain has no mix param.
        sink(2, a.TargetLevel);
        sink(3, a.AttackCoefficient);
        sink(4, a.ReleaseCoefficient);
        sink(5, a.MaximumGain);
        sink(6, a.MinimumGain);
        sink(7, a.GateThreshold);
    }

    private static void MirrorEnhancer(IEffectProcessor e, ParamSink sink)
    {
        var h = (ME.EnhancerEffect)e;
        sink(2, h.Gain);
        sink(3, h.CutoffFrequency);   // Hz
    }

    private static void MirrorDynamicAmp(IEffectProcessor e, ParamSink sink)
    {
        var d = (ME.DynamicAmpEffect)e;
        sink(2, d.TargetRmsLevelDb);            // dB
        sink(3, d.AttackTime);                  // ms/s per managed property
        sink(4, d.ReleaseTime);
        sink(5, d.NoiseGateThresholdDb);        // dB
        sink(6, d.MaxGain);
        sink(7, d.MaxGainReductionDb);          // dB
        sink(8, d.RmsWindowSeconds);            // s
        sink(9, d.MaxGainChangePerSecondDb);    // dB/s
    }
}
