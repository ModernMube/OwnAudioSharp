using System;
using System.Collections.Generic;
using OwnaudioNET.Interfaces;
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
/// sync tick through <see cref="Mirror"/> — converting units where the managed and native parameter
/// conventions differ (for example the compressor threshold is linear 0–1 managed, decibels native).
/// </para>
/// <para>
/// Only the effect types with a registered adapter are routed to native; others are skipped (they
/// simply produce no master-bus processing until an adapter is added in E.4).
/// </para>
/// </remarks>
internal static class RustEffectAdapters
{
    /// <summary>Native parameter id shared by every effect: enable/bypass (0 = bypass).</summary>
    private const uint ParamEnabled = 0;

    /// <summary>Native parameter id shared by every effect: wet/dry mix.</summary>
    private const uint ParamMix = 1;

    /// <summary>Receives one native parameter change (id + value) for the paired effect.</summary>
    internal delegate void ParamSink(uint paramId, float value);

    private sealed record Adapter(EffectType Type, Action<IEffectProcessor, ParamSink> Mirror);

    /// <summary>
    /// Registry keyed by the concrete managed effect type. E.4 extends this with the remaining
    /// effect types; E.3 ships the compressor adapter as the end-to-end proof.
    /// </summary>
    private static readonly IReadOnlyDictionary<Type, Adapter> Adapters =
        new Dictionary<Type, Adapter>
        {
            [typeof(OwnaudioNET.Effects.CompressorEffect)] =
                new Adapter(EffectType.Compressor, MirrorCompressor),
        };

    /// <summary>
    /// Resolves the native <see cref="EffectType"/> for a managed effect, when an adapter exists.
    /// </summary>
    /// <param name="effect">The managed effect.</param>
    /// <param name="effectType">Receives the native effect type on success.</param>
    /// <returns><see langword="true"/> when the effect type is routable to native.</returns>
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
    /// <paramref name="sink"/>. Always mirrors the common enable and mix parameters, then the
    /// effect-specific parameters when an adapter is registered.
    /// </summary>
    /// <param name="effect">The managed effect acting as the parameter model.</param>
    /// <param name="sink">Callback that applies one native parameter to the paired effect.</param>
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

    private static void MirrorCompressor(IEffectProcessor effect, ParamSink sink)
    {
        var c = (OwnaudioNET.Effects.CompressorEffect)effect;

        // The managed public properties already expose the same units as the native compressor
        // params (threshold=2 in dB, ratio=3, attack=4 in ms, release=5 in ms, makeup=6 in dB), so
        // they pass through directly — the linear↔dB conversion lives inside the managed properties.
        sink(2, c.Threshold);
        sink(3, c.Ratio);
        sink(4, c.AttackTime);
        sink(5, c.ReleaseTime);
        sink(6, c.MakeupGain);
    }
}
