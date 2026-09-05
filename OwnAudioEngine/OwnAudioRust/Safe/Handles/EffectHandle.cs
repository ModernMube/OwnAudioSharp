using System;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Effect pointer handed back by ownaudio_v1_track_add_effect. Freeing it only drops the
/// wrapper — it does NOT unhook the effect from the chain, so call
/// ownaudio_v1_effect_remove before you dispose.
/// </summary>
public sealed class EffectHandle : NativePtrHandle
{
    /// <inheritdoc/>
    protected override void Destroy(IntPtr ptr) => OwnAudioNative.ownaudio_v1_effect_destroy(ptr);
}
