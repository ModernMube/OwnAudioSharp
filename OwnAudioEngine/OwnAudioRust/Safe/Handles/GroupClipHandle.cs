using System;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// A clip loaded for groups (ownaudio_v1_group_clip_open). Destroying it drops our reference
/// only — groups it was placed on keep what they need to go on playing it.
/// </summary>
public sealed class GroupClipHandle : NativePtrHandle
{
    /// <inheritdoc/>
    protected override void Destroy(IntPtr ptr) => OwnAudioNative.ownaudio_v1_group_clip_destroy(ptr);
}
