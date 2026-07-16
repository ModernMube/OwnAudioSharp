using System.Runtime.InteropServices;

namespace Ownaudio.Native.RustAudio.Interop;

/// <summary>
/// Output callback, same shape as OwnAudioOutputCallback in ownaudio_ffi.h.
/// Runs on the rust/cpal rt thread, so no allocation, no locks, no blocking in here.
/// The safe layer pins it with GCHandle.Alloc and keeps that alive while the stream lives.
/// </summary>
/// <param name="buffer"></param>
/// <param name="frameCount"></param>
/// <param name="channels"></param>
/// <param name="userData"></param>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate void NativeOutputCallback(
    float* buffer,
    nuint frameCount,
    ushort channels,
    void* userData);

/// <summary>
/// Input side of the same deal. Rt rules apply here too, and the buffer is read only.
/// </summary>
/// <param name="buffer"></param>
/// <param name="frameCount"></param>
/// <param name="channels"></param>
/// <param name="userData"></param>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate void NativeInputCallback(
    float* buffer,
    nuint frameCount,
    ushort channels,
    void* userData);
