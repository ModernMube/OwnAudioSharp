using System;
namespace Ownaudio.Safe.Effects;

/// <summary>
/// Contract for a Rust-backed effect. Implementations just own an EffectHandle and push
/// everything down through ownaudio_v1_effect_set_param — zero DSP in managed code.
/// Setters write through right away, getters hand back a cached value.
/// </summary>
public interface INativeEffect : IDisposable
{
    /// <summary>Static type tag of this effect.</summary>
    Audio.Effects.EffectType EffectType { get; }

    /// <summary>
    /// False means the native side bypasses it.
    /// </summary>
    bool IsEnabled { get; set; }
}
