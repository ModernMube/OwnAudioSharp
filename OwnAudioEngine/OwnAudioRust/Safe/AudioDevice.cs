using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Structs;

namespace Ownaudio.Safe;

/// <summary>
/// One enumerated input or output device. The name is copied out of the rust owned
/// buffer right at construction, so we survive the device list being freed.
/// </summary>
public sealed class AudioDevice
{
    #region Propertyes

    public string Name { get; }

    // default in/out flags as the host api reports them
    public bool IsDefaultInput { get; }
    public bool IsDefaultOutput { get; }

    public int MaxInputChannels { get; }
    public int MaxOutputChannels { get; }

    /// <summary>
    /// Preferred rate of the device in Hz, 44100 or 48000 in most cases.
    /// </summary>
    public int DefaultSampleRate { get; }

    #endregion

    internal AudioDevice(in NativeDeviceInfo native)
    {
        Name = native.Name != IntPtr.Zero ? Marshal.PtrToStringUTF8(native.Name) ?? string.Empty : string.Empty;

        IsDefaultInput    = native.IsDefaultInput  != 0;
        IsDefaultOutput   = native.IsDefaultOutput != 0;
        MaxInputChannels  = native.MaxInputChannels;
        MaxOutputChannels = native.MaxOutputChannels;
        DefaultSampleRate = (int)native.DefaultSampleRate;
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        return $"{Name} (in={MaxInputChannels} out={MaxOutputChannels} @{DefaultSampleRate} Hz)";
    }
}
