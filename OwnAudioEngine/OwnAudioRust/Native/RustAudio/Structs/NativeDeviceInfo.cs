using System;
using System.Runtime.InteropServices;

namespace Ownaudio.Native.RustAudio.Structs;

/// <summary>
/// OwnAudioDeviceInfo from ownaudio_ffi.h — 24 bytes on 64-bit.
/// Name is Rust-owned, never free it here; the whole array goes back
/// through ownaudio_v1_free_device_list.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeDeviceInfo
{
    /// <summary>
    /// Null-terminated UTF-8 name, Rust side owns it.
    /// Safe layer pulls it out with Marshal.PtrToStringUTF8.
    /// </summary>
    public IntPtr Name;

    /// <summary>Nonzero if this is the system default input.</summary>
    public byte IsDefaultInput;

    /// <summary>Nonzero if this is the system default output.</summary>
    public byte IsDefaultOutput;

    /// <summary>
    /// Max input channels the device can do.
    /// </summary>
    public ushort MaxInputChannels;

    /// <summary>Max output channels.</summary>
    public ushort MaxOutputChannels;

    private ushort _pad;

    /// <summary>
    /// Preferred rate in Hz.
    /// </summary>
    public uint DefaultSampleRate;
}
