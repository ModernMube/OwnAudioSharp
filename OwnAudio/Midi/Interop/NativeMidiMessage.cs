using System.Runtime.InteropServices;

namespace OwnAudio.Midi.Interop;

/// <summary>
/// Short MIDI message as it crosses the FFI. The pad byte is there to keep the
/// layout at size 16 / align 8 like the Rust repr(C); a parity test watches it.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeMidiMessage
{
    /// <summary>
    /// Type in the high nibble, channel in the low one.
    /// </summary>
    public byte Status;

    /// <summary>
    /// First data byte.
    /// </summary>
    public byte Data1;

    /// <summary>
    /// Second data byte.
    /// </summary>
    public byte Data2;

    /// <summary>
    /// Padding, always zero.
    /// </summary>
    public byte Pad;

    /// <summary>
    /// When it arrived, in microseconds.
    /// </summary>
    public long TimestampUs;
}
