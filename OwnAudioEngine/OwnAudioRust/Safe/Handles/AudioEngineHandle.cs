using System;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Opaque engine pointer from ownaudio_v1_engine_create. Gets destroyed even if nobody
/// calls Dispose. Creating it is AudioEngine's job, we just hold and free.
/// </summary>
public sealed class AudioEngineHandle : NativePtrHandle
{
    /// <inheritdoc/>
    protected override void Destroy(IntPtr ptr) => OwnAudioNative.ownaudio_v1_engine_destroy(ptr);
}
