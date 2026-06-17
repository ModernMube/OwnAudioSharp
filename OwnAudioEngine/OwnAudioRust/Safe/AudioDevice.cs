using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Structs;

namespace Ownaudio.Safe;

/// <summary>
/// Immutable description of an audio input or output device enumerated from the native engine.
/// All string data is copied from the Rust-owned buffer during construction, so the instance
/// remains valid after the native device list is freed.
/// </summary>
public sealed class AudioDevice
{
    #region Properties

    /// <summary>Human-readable device name provided by the OS audio subsystem.</summary>
    public string Name { get; }

    /// <summary>
    /// <see langword="true"/> when this device is the system default input device.
    /// </summary>
    public bool IsDefaultInput { get; }

    /// <summary>
    /// <see langword="true"/> when this device is the system default output device.
    /// </summary>
    public bool IsDefaultOutput { get; }

    /// <summary>Maximum number of input channels this device supports.</summary>
    public int MaxInputChannels { get; }

    /// <summary>Maximum number of output channels this device supports.</summary>
    public int MaxOutputChannels { get; }

    /// <summary>The device's preferred sample rate in Hz (e.g. 44100 or 48000).</summary>
    public int DefaultSampleRate { get; }

    #endregion

    #region Construction

    /// <summary>
    /// Constructs an <see cref="AudioDevice"/> from a <see cref="NativeDeviceInfo"/> struct.
    /// The name pointer is read immediately; callers may free the native array afterwards.
    /// </summary>
    /// <param name="native">The blittable struct received from the FFI layer.</param>
    internal AudioDevice(in NativeDeviceInfo native)
    {
        Name = native.Name != IntPtr.Zero
            ? Marshal.PtrToStringUTF8(native.Name) ?? string.Empty
            : string.Empty;

        IsDefaultInput    = native.IsDefaultInput  != 0;
        IsDefaultOutput   = native.IsDefaultOutput != 0;
        MaxInputChannels  = native.MaxInputChannels;
        MaxOutputChannels = native.MaxOutputChannels;
        DefaultSampleRate = (int)native.DefaultSampleRate;
    }

    #endregion

    #region Object overrides

    /// <inheritdoc/>
    public override string ToString()
    {
        return $"{Name} (in={MaxInputChannels} out={MaxOutputChannels} @{DefaultSampleRate} Hz)";
    }

    #endregion
}
