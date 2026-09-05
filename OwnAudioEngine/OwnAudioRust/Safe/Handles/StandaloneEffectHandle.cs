using System;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Pointer from ownaudio_v1_standalone_effect_create. Not a mixer twin — destroying it
/// only drops this instance.
/// </summary>
public sealed class StandaloneEffectHandle : NativePtrHandle
{
    /// <inheritdoc/>
    protected override void Destroy(IntPtr ptr) => OwnAudioNative.ownaudio_v1_standalone_effect_destroy(ptr);
}
