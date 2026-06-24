using System.Runtime.InteropServices;

namespace OwnAudio.Midi.Interop;

/// <summary>
/// Blittable mirror of the native <c>NativeMidiEvent</c> struct used by both the
/// file parser query API (the native side fills it) and the writer API (the
/// managed side fills it). The explicit padding bytes keep the layout (size 32,
/// 8-byte aligned) identical to the Rust <c>#[repr(C)]</c> definition; a parity
/// test verifies both sides agree.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeMidiEvent
{
    /// <summary>
    /// Ticks since the previous event in the track.
    /// </summary>
    public int DeltaTime;

    /// <summary>
    /// Event category: 0 = MIDI, 1 = Meta, 2 = SysEx.
    /// </summary>
    public byte EventType;

    /// <summary>
    /// Status byte.
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
    /// Meta event sub-type byte.
    /// </summary>
    public byte MetaType;

    /// <summary>
    /// Alignment padding; always zero.
    /// </summary>
    public byte Pad0;

    /// <summary>
    /// Alignment padding; always zero.
    /// </summary>
    public byte Pad1;

    /// <summary>
    /// Alignment padding; always zero.
    /// </summary>
    public byte Pad2;

    /// <summary>
    /// Pointer to the payload bytes, or <see cref="IntPtr.Zero"/> when there is none.
    /// </summary>
    public IntPtr MetaData;

    /// <summary>
    /// Number of payload bytes referenced by <see cref="MetaData"/>.
    /// </summary>
    public nuint MetaDataLen;
}
