using System.Runtime.InteropServices;

namespace OwnAudio.Midi.Interop;

/// <summary>
/// File event across the FFI — the parser fills it, the writer reads it. Pad bytes
/// keep it at size 32 / align 8 like the Rust repr(C); a parity test watches it.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeMidiEvent
{
    /// <summary>
    /// Ticks since the previous event.
    /// </summary>
    public int DeltaTime;

    /// <summary>
    /// 0 = MIDI, 1 = Meta, 2 = SysEx.
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
    /// Meta sub-type.
    /// </summary>
    public byte MetaType;

    /// <summary>
    /// Padding, always zero.
    /// </summary>
    public byte Pad0;

    /// <summary>
    /// Padding, always zero.
    /// </summary>
    public byte Pad1;

    /// <summary>
    /// Padding, always zero.
    /// </summary>
    public byte Pad2;

    /// <summary>
    /// Payload bytes, or zero if there aren't any.
    /// </summary>
    public IntPtr MetaData;

    /// <summary>
    /// How many bytes MetaData points at.
    /// </summary>
    public nuint MetaDataLen;
}
