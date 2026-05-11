namespace OwnAudio.Midi.IO;

/// <summary>
/// Immutable representation of a single MIDI message with status, data bytes, and timestamp.
/// </summary>
public readonly struct MidiMessage
{
    /// <summary>
    /// MIDI status byte encoding the message type and channel number.
    /// </summary>
    public readonly byte Status;

    /// <summary>
    /// First data byte, such as note number or controller number.
    /// </summary>
    public readonly byte Data1;

    /// <summary>
    /// Second data byte, such as velocity or controller value.
    /// </summary>
    public readonly byte Data2;

    /// <summary>
    /// Message arrival timestamp in nanoseconds.
    /// </summary>
    public readonly long Timestamp;

    /// <summary>
    /// Initializes a new MIDI message with the given status, data bytes, and optional timestamp.
    /// </summary>
    public MidiMessage(byte status, byte data1, byte data2, long timestamp = 0)
    {
        Status = status;
        Data1 = data1;
        Data2 = data2;
        Timestamp = timestamp;
    }

    /// <summary>
    /// Gets the message type derived from the upper nibble of the status byte.
    /// </summary>
    public MidiMessageType Type => (MidiMessageType)(Status & 0xF0);

    /// <summary>
    /// Gets the zero-based MIDI channel number (0–15).
    /// </summary>
    public int Channel => Status & 0x0F;

    /// <summary>
    /// Gets a value indicating whether this message is a Note On event with non-zero velocity.
    /// </summary>
    public bool IsNoteOn => Type == MidiMessageType.NoteOn && Data2 > 0;

    /// <summary>
    /// Gets a value indicating whether this message represents a Note Off event,
    /// including Note On with velocity zero.
    /// </summary>
    public bool IsNoteOff => Type == MidiMessageType.NoteOff || (Type == MidiMessageType.NoteOn && Data2 == 0);

    /// <summary>
    /// Gets a value indicating whether this is a Control Change message.
    /// </summary>
    public bool IsControlChange => Type == MidiMessageType.ControlChange;

    /// <summary>
    /// Gets a value indicating whether this is a Program Change message.
    /// </summary>
    public bool IsProgramChange => Type == MidiMessageType.ProgramChange;

    /// <summary>
    /// Gets a value indicating whether this is a Pitch Bend message.
    /// </summary>
    public bool IsPitchBend => Type == MidiMessageType.PitchBend;

    /// <summary>
    /// Returns a human-readable string representation of the MIDI message.
    /// </summary>
    public override string ToString() =>
        $"[{Type} Ch={Channel} D1={Data1} D2={Data2}]";
}

/// <summary>
/// MIDI message type encoded in the upper nibble of the status byte.
/// </summary>
public enum MidiMessageType : byte
{
    /// <summary>
    /// Note Off event — stops a sounding note.
    /// </summary>
    NoteOff         = 0x80,

    /// <summary>
    /// Note On event — starts a note; velocity zero acts as Note Off.
    /// </summary>
    NoteOn          = 0x90,

    /// <summary>
    /// Polyphonic key pressure (per-note aftertouch).
    /// </summary>
    Aftertouch      = 0xA0,

    /// <summary>
    /// Control Change message carrying a controller number and value.
    /// </summary>
    ControlChange   = 0xB0,

    /// <summary>
    /// Program Change — selects a patch or instrument.
    /// </summary>
    ProgramChange   = 0xC0,

    /// <summary>
    /// Channel Pressure — mono aftertouch applied to all notes on the channel.
    /// </summary>
    ChannelPressure = 0xD0,

    /// <summary>
    /// Pitch Bend Change — adjusts pitch up or down on the channel.
    /// </summary>
    PitchBend       = 0xE0,

    /// <summary>
    /// System Exclusive — manufacturer-specific variable-length message.
    /// </summary>
    SysEx           = 0xF0,

    /// <summary>
    /// Meta event — used only inside MIDI files, not transmitted over the wire.
    /// </summary>
    Meta            = 0xFF
}
