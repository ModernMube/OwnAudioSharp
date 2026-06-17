using System;
using System.Runtime.InteropServices;

namespace Ownaudio.Native.RustAudio.Structs;

/// <summary>
/// Mirrors <c>OwnAudioDeviceInfo</c> from <c>ownaudio_ffi.h</c>.
/// Field order and sizes must match exactly — verified by unit test against
/// the cbindgen-generated layout.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Name"/> field is a Rust-owned, null-terminated UTF-8 pointer.
/// Do <b>not</b> free it directly; pass the entire array to
/// <c>ownaudio_v1_free_device_list</c> when finished.
/// </para>
/// <para>
/// Layout on 64-bit (little-endian, natural C alignment):
/// <code>
/// Offset  0 : Name              (8 bytes — pointer)
/// Offset  8 : IsDefaultInput    (1 byte  — bool as u8)
/// Offset  9 : IsDefaultOutput   (1 byte  — bool as u8)
/// Offset 10 : MaxInputChannels  (2 bytes — u16)
/// Offset 12 : MaxOutputChannels (2 bytes — u16)
/// Offset 14 : [2 bytes padding] (u32 alignment)
/// Offset 16 : DefaultSampleRate (4 bytes — u32)
/// Total  : 24 bytes (padded to pointer alignment)
/// </code>
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeDeviceInfo
{
    /// <summary>
    /// Pointer to a null-terminated UTF-8 device name string, allocated by Rust.
    /// Read with <c>Marshal.PtrToStringUTF8(Name)</c> in the safe wrapper layer.
    /// </summary>
    public IntPtr Name;

    /// <summary>Non-zero when this device is the system default input device.</summary>
    public byte IsDefaultInput;

    /// <summary>Non-zero when this device is the system default output device.</summary>
    public byte IsDefaultOutput;

    /// <summary>Maximum number of input channels supported by this device.</summary>
    public ushort MaxInputChannels;

    /// <summary>Maximum number of output channels supported by this device.</summary>
    public ushort MaxOutputChannels;

    /// <summary>
    /// Two bytes of implicit padding inserted by the C ABI to align
    /// <see cref="DefaultSampleRate"/> on a 4-byte boundary.
    /// </summary>
    private ushort _pad;

    /// <summary>The device's preferred sample rate in Hz.</summary>
    public uint DefaultSampleRate;
}
