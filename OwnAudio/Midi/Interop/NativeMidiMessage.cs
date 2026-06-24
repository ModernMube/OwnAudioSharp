using System.Runtime.InteropServices;

namespace OwnAudio.Midi.Interop;

/// <summary>
/// Blittable mirror of the native <c>NativeMidiMessage</c> struct used to carry a
/// short MIDI message across the FFI boundary. The explicit padding byte keeps
/// the layout (size 16, 8-byte aligned) identical to the Rust <c>#[repr(C)]</c>
/// definition; a parity test verifies both sides agree.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeMidiMessage
{
    /// <summary>
    /// Status byte (message type in the high nibble, channel in the low nibble).
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
    /// Alignment padding; always zero.
    /// </summary>
    public byte Pad;

    /// <summary>
    /// Arrival timestamp in microseconds.
    /// </summary>
    public long TimestampUs;
}
