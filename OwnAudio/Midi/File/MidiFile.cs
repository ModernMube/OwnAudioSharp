namespace OwnAudio.Midi.File;

/// <summary>
/// Represents a complete MIDI file with its format, timing resolution, and track list.
/// </summary>
public sealed class MidiFile
{
    /// <summary>
    /// Gets the MIDI file format: 0 (single track), 1 (multi-track sync), or 2 (multi-track async).
    /// </summary>
    public ushort Format { get; }

    /// <summary>
    /// Gets the number of MIDI ticks that represent one quarter note (PPQ resolution).
    /// </summary>
    public ushort TicksPerBeat { get; }

    /// <summary>
    /// Gets the ordered list of tracks contained in the file.
    /// </summary>
    public IReadOnlyList<MidiTrack> Tracks { get; }

    /// <summary>
    /// Initializes a new <see cref="MidiFile"/> with the specified format, resolution, and tracks.
    /// </summary>
    public MidiFile(ushort format, ushort ticksPerBeat, MidiTrack[] tracks)
    {
        Format = format;
        TicksPerBeat = ticksPerBeat;
        Tracks = tracks;
    }
}

/// <summary>
/// A sequence of timed MIDI events forming one track within a MIDI file.
/// </summary>
public sealed class MidiTrack
{
    /// <summary>
    /// Gets the ordered list of events in this track.
    /// </summary>
    public IReadOnlyList<MidiEvent> Events { get; }

    /// <summary>
    /// Initializes a new <see cref="MidiTrack"/> with the given event list.
    /// </summary>
    public MidiTrack(List<MidiEvent> events)
    {
        Events = events;
    }
}

/// <summary>
/// A single timed event within a MIDI track: either a MIDI message, a meta event, or a SysEx block.
/// </summary>
public readonly struct MidiEvent
{
    /// <summary>
    /// Number of MIDI ticks since the previous event in the track.
    /// </summary>
    public readonly int DeltaTime;

    /// <summary>
    /// Classifies the event as MIDI, Meta, or SysEx.
    /// </summary>
    public readonly MidiEventType Type;

    /// <summary>
    /// MIDI status byte (or 0xFF for meta events, 0xF0 for SysEx).
    /// </summary>
    public readonly byte Status;

    /// <summary>
    /// First MIDI data byte (unused for meta and SysEx events).
    /// </summary>
    public readonly byte Data1;

    /// <summary>
    /// Second MIDI data byte (unused for meta and SysEx events).
    /// </summary>
    public readonly byte Data2;

    /// <summary>
    /// Meta event sub-type byte (e.g., 0x51 for tempo, 0x2F for end-of-track).
    /// </summary>
    public readonly byte MetaType;

    /// <summary>
    /// Payload bytes for meta and SysEx events; null for plain MIDI events.
    /// </summary>
    public readonly byte[]? MetaData;

    /// <summary>
    /// Initializes a standard MIDI channel event with status and two data bytes.
    /// </summary>
    public MidiEvent(int deltaTime, byte status, byte data1, byte data2)
    {
        DeltaTime = deltaTime;
        Type = MidiEventType.Midi;
        Status = status;
        Data1 = data1;
        Data2 = data2;
        MetaType = 0;
        MetaData = null;
    }

    /// <summary>
    /// Initializes a meta event with the given sub-type and payload data.
    /// </summary>
    public MidiEvent(int deltaTime, byte metaType, byte[] metaData)
    {
        DeltaTime = deltaTime;
        Type = MidiEventType.Meta;
        Status = 0xFF;
        Data1 = 0;
        Data2 = 0;
        MetaType = metaType;
        MetaData = metaData;
    }

    /// <summary>
    /// Initializes a SysEx event with the raw byte payload (including the 0xF0 prefix).
    /// </summary>
    public MidiEvent(int deltaTime, byte[] sysexData)
    {
        DeltaTime = deltaTime;
        Type = MidiEventType.SysEx;
        Status = 0xF0;
        Data1 = 0;
        Data2 = 0;
        MetaType = 0;
        MetaData = sysexData;
    }

    /// <summary>
    /// Gets a value indicating whether this event marks the end of the track (meta type 0x2F).
    /// </summary>
    public bool IsEndOfTrack => Type == MidiEventType.Meta && MetaType == 0x2F;

    /// <summary>
    /// Gets a value indicating whether this event carries a tempo change (meta type 0x51).
    /// </summary>
    public bool IsTempoChange => Type == MidiEventType.Meta && MetaType == 0x51;

    /// <summary>
    /// Returns the tempo in microseconds per quarter note from a tempo meta event.
    /// Returns 500 000 (120 BPM) if the event is not a valid tempo event.
    /// </summary>
    public int GetTempoMicroseconds()
    {
        if (!IsTempoChange || MetaData is null || MetaData.Length < 3)
            return 500_000;
        return (MetaData[0] << 16) | (MetaData[1] << 8) | MetaData[2];
    }
}

/// <summary>
/// Classifies the content type of a <see cref="MidiEvent"/>.
/// </summary>
public enum MidiEventType : byte
{
    /// <summary>
    /// Standard MIDI channel message (Note On/Off, CC, etc.).
    /// </summary>
    Midi,

    /// <summary>
    /// Meta event carrying file-level information such as tempo, time signature, or track name.
    /// </summary>
    Meta,

    /// <summary>
    /// System Exclusive message containing manufacturer-specific data.
    /// </summary>
    SysEx
}
