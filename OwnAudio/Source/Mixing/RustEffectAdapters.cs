using System;
using System.Collections.Generic;
using OwnaudioNET.Interfaces;
using ME = OwnaudioNET.Effects;
using SM = OwnaudioNET.Effects.SmartMaster;
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
/// Only the effect types with a registered adapter are routed to native. This includes the composite
/// <c>SmartMasterEffect</c>, which maps to the single native <see cref="EffectType.SmartMaster"/>
/// effect (its whole DSP chain runs natively, with <see cref="MirrorSmartMaster"/> pushing the managed
/// <c>SmartMasterConfig</c> onto the native parameters). VST3 is not routed through this registry — it
/// is hosted natively through its own dedicated bridge (plan E.6). Any other effect without a
/// registered adapter is skipped and produces no native processing.
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
            [typeof(SM.SmartMasterEffect)]      = new Adapter(EffectType.SmartMaster, MirrorSmartMaster),
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

    /// <summary>
    /// Mirrors the composite <see cref="SM.SmartMasterEffect"/>'s current configuration onto its
    /// single native <see cref="EffectType.SmartMaster"/> effect. The managed effect stays the
    /// parameter model (UI / preset owner); the whole DSP chain runs natively.
    /// </summary>
    /// <remarks>
    /// The parameter ids and units match the native composite's contract (see the Rust
    /// <c>smartmaster</c> module): EQ band gains occupy ids 2–31 (dB), then subharmonic (32–33),
    /// compressor (34–38, threshold kept as the config's linear 0–1 value and converted to dB
    /// natively), crossover frequency (39), phase-alignment delays/inversions (40–45) and the limiter
    /// (46–48). The common enable/mix (ids 0–1) are pushed by the base <see cref="Mirror"/>.
    /// </remarks>
    private static void MirrorSmartMaster(IEffectProcessor e, ParamSink sink)
    {
        var sm = (SM.SmartMasterEffect)e;
        SM.SmartMasterConfig cfg = sm.GetConfiguration();

        // Graphic EQ: 30 band gains in dB (ids 2..31). GraphicEQGains is oversized (31); use the
        // first 30, matching the native 30-band equalizer.
        float[] eqGains = cfg.GraphicEQGains;
        for (int i = 0; i < 30; i++)
        {
            sink((uint)(2 + i), (eqGains is not null && i < eqGains.Length) ? eqGains[i] : 0f);
        }

        // Subharmonic synthesizer.
        sink(32, cfg.SubharmonicEnabled ? 1f : 0f);
        sink(33, cfg.SubharmonicMix);           // 0–1

        // Compressor. Threshold is the config's linear 0–1 value; the native effect converts it to dB.
        sink(34, cfg.CompressorEnabled ? 1f : 0f);
        sink(35, cfg.CompressorThreshold);      // linear 0–1
        sink(36, cfg.CompressorRatio);          // N:1
        sink(37, cfg.CompressorAttack);         // ms
        sink(38, cfg.CompressorRelease);        // ms

        // Crossover split frequency.
        sink(39, cfg.CrossoverFrequency);       // Hz

        // Phase alignment: per-channel delays (ms) and polarity flips for L, R, Sub.
        float[] delays = cfg.TimeDelays;
        bool[] invert = cfg.PhaseInvert;
        sink(40, (delays is not null && delays.Length > 0) ? delays[0] : 0f);
        sink(41, (delays is not null && delays.Length > 1) ? delays[1] : 0f);
        sink(42, (delays is not null && delays.Length > 2) ? delays[2] : 0f);
        sink(43, (invert is not null && invert.Length > 0 && invert[0]) ? 1f : 0f);
        sink(44, (invert is not null && invert.Length > 1 && invert[1]) ? 1f : 0f);
        sink(45, (invert is not null && invert.Length > 2 && invert[2]) ? 1f : 0f);

        // Brick-wall limiter.
        sink(46, cfg.LimiterThreshold);         // dBFS
        sink(47, cfg.LimiterCeiling);           // dBFS
        sink(48, cfg.LimiterRelease);           // ms
    }
}
