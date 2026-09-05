using System;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// BPM detector pointer from ownaudio_v1_bpm_create, freed by the finalizer if need be.
/// </summary>
public sealed class BpmDetectHandle : NativePtrHandle
{
    /// <inheritdoc/>
    protected override void Destroy(IntPtr ptr) => OwnAudioNative.ownaudio_v1_bpm_destroy(ptr);
}
