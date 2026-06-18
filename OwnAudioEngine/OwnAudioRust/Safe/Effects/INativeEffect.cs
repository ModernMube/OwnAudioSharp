using System;
namespace Ownaudio.Safe.Effects;

/// <summary>
/// Contract for a native Rust-backed audio effect.
/// </summary>
/// <remarks>
/// <para>
/// Implementations hold an <see cref="Ownaudio.Safe.Handles.EffectHandle"/> and delegate all
/// DSP work to the native layer via <c>ownaudio_v1_effect_set_param</c> /
/// <c>ownaudio_v1_effect_get_param</c>.  No audio processing happens in managed code.
/// </para>
/// <para>
/// All parameter setters write through to the native effect immediately; getters return
/// a cached copy to avoid interop overhead on every UI read.
/// </para>
/// </remarks>
public interface INativeEffect : IDisposable
{
    /// <summary>
    /// Gets the static type tag of this effect.
    /// </summary>
    Audio.Effects.EffectType EffectType { get; }

    /// <summary>
    /// Gets or sets whether the effect is active.
    /// When <see langword="false"/> the effect is bypassed on the native side.
    /// </summary>
    bool IsEnabled { get; set; }
}
