namespace OwnAudio.Midi.IO;

/// <summary>
/// One short MIDI message — status, two data bytes, timestamp. Immutable.
/// </summary>
public readonly struct MidiMessage
{
    /// <summary>
    /// Type in the high nibble, channel in the low one.
    /// </summary>
    public readonly byte Status;

    /// <summary>
    /// Note number, CC number, that sort of thing.
    /// </summary>
    public readonly byte Data1;

    /// <summary>
    /// Velocity, CC value, that sort of thing.
    /// </summary>
    public readonly byte Data2;

    /// <summary>
    /// Arrival time in microseconds.
    /// </summary>
    public readonly long Timestamp;

    /// <summary>
    /// Builds a message; timestamp is optional and defaults to zero.
    /// </summary>
    public MidiMessage(byte status, byte data1, byte data2, long timestamp = 0)
    {
        Status = status;
        Data1 = data1;
        Data2 = data2;
        Timestamp = timestamp;
    }

    /// <summary>
    /// Message type off the upper nibble.
    /// </summary>
    public MidiMessageType Type => (MidiMessageType)(Status & 0xF0);

    /// <summary>
    /// Channel, 0-15.
    /// </summary>
    public int Channel => Status & 0x0F;

    /// <summary>
    /// Note On with a real velocity behind it.
    /// </summary>
    public bool IsNoteOn => Type == MidiMessageType.NoteOn && Data2 > 0;

    /// <summary>
    /// Note Off — the zero-velocity Note On counts too.
    /// </summary>
    public bool IsNoteOff => Type == MidiMessageType.NoteOff || (Type == MidiMessageType.NoteOn && Data2 == 0);

    /// <summary>
    /// Control Change?
    /// </summary>
    public bool IsControlChange => Type == MidiMessageType.ControlChange;

    /// <summary>
    /// Program Change?
    /// </summary>
    public bool IsProgramChange => Type == MidiMessageType.ProgramChange;

    /// <summary>
    /// Pitch Bend?
    /// </summary>
    public bool IsPitchBend => Type == MidiMessageType.PitchBend;

    /// <summary>
    /// Readable form for logs.
    /// </summary>
    public override string ToString() => $"[{Type} Ch={Channel} D1={Data1} D2={Data2}]";
}

/// <summary>
/// Message type, i.e. the upper nibble of the status byte.
/// </summary>
public enum MidiMessageType : byte
{
    /// <summary>
    /// Kills a sounding note.
    /// </summary>
    NoteOff         = 0x80,

    /// <summary>
    /// Starts a note; velocity 0 means note off.
    /// </summary>
    NoteOn          = 0x90,

    /// <summary>
    /// Per-note aftertouch.
    /// </summary>
    Aftertouch      = 0xA0,

    /// <summary>
    /// Controller number + value.
    /// </summary>
    ControlChange   = 0xB0,

    /// <summary>
    /// Patch select.
    /// </summary>
    ProgramChange   = 0xC0,

    /// <summary>
    /// Mono aftertouch for the whole channel.
    /// </summary>
    ChannelPressure = 0xD0,

    /// <summary>
    /// Pitch up or down on the channel.
    /// </summary>
    PitchBend       = 0xE0,

    /// <summary>
    /// Vendor specific, variable length.
    /// </summary>
    SysEx           = 0xF0,

    /// <summary>
    /// File-only, never goes over the wire.
    /// </summary>
    Meta            = 0xFF
}
