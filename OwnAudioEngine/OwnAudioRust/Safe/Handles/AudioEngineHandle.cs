using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Opaque engine pointer from ownaudio_v1_engine_create. Gets destroyed even if nobody
/// calls Dispose. Creating it is AudioEngine's job, we just hold and free.
/// </summary>
public sealed class AudioEngineHandle : SafeHandle
{
    /// <summary>
    /// Starts out invalid, P/Invoke out-marshaling fills the real value in.
    /// </summary>
    public AudioEngineHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        OwnAudioNative.ownaudio_v1_engine_destroy(handle);
        return true;
    }
}
