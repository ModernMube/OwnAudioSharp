using System;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Mixer pointer from ownaudio_v1_mixer_create. Every track and effect handle that came out
/// of this mixer has to be disposed first, otherwise you're freeing under their feet.
/// </summary>
public sealed class MixerHandle : NativePtrHandle
{
    /// <inheritdoc/>
    protected override void Destroy(IntPtr ptr)
    {
        OwnAudioNative.ownaudio_v1_mixer_destroy(ptr);
    }
}
