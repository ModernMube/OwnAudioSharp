using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Effect pointer handed back by ownaudio_v1_track_add_effect. Freeing it only drops the
/// wrapper — it does NOT unhook the effect from the chain, so call
/// ownaudio_v1_effect_remove before you dispose.
/// </summary>
public sealed class EffectHandle : SafeHandle
{
    /// <summary>
    /// Invalid until P/Invoke fills it in.
    /// </summary>
    public EffectHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        OwnAudioNative.ownaudio_v1_effect_destroy(handle);
        return true;
    }
}
