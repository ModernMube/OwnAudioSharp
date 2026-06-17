using System.Runtime.InteropServices;

namespace Ownaudio.Native.RustAudio.Interop;

/// <summary>
/// Managed delegate type for the output audio callback, matching the C ABI signature
/// <c>OwnAudioOutputCallback</c> in <c>ownaudio_ffi.h</c>.
/// </summary>
/// <remarks>
/// <para>
/// This delegate runs on the Rust/cpal real-time audio thread.
/// Implementations must be real-time safe: no heap allocation, no blocking I/O,
/// no managed GC interaction, no non-RT-safe locks.
/// </para>
/// <para>
/// The safe wrapper layer (step 5) is responsible for pinning an instance of this
/// delegate via <c>GCHandle.Alloc(pin, GCHandleType.Normal)</c> and converting it
/// to a native function pointer with <c>Marshal.GetFunctionPointerForDelegate</c>
/// before passing it to <c>ownaudio_v1_open_output_stream</c>.
/// The <c>GCHandle</c> must stay alive for the entire lifetime of the stream.
/// </para>
/// </remarks>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate void NativeOutputCallback(
    float* buffer,
    nuint frameCount,
    ushort channels,
    void* userData);

/// <summary>
/// Managed delegate type for the input audio callback, matching the C ABI signature
/// <c>OwnAudioInputCallback</c> in <c>ownaudio_ffi.h</c>.
/// </summary>
/// <remarks>
/// Same real-time and lifetime constraints as <see cref="NativeOutputCallback"/>.
/// The buffer is read-only; writing to it results in undefined behaviour.
/// </remarks>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate void NativeInputCallback(
    float* buffer,
    nuint frameCount,
    ushort channels,
    void* userData);
