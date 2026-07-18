namespace OwnAudio.Midi.File;

/// <summary>
/// A whole MIDI file: format, resolution, tracks.
/// </summary>
public sealed class MidiFile
{
    /// <summary>
    /// 0 = single track, 1 = multi-track sync, 2 = multi-track async.
    /// </summary>
    public ushort Format { get; }

    /// <summary>
    /// Ticks per quarter note (PPQ).
    /// </summary>
    public ushort TicksPerBeat { get; }

    /// <summary>
    /// The tracks, in file order.
    /// </summary>
    public IReadOnlyList<MidiTrack> Tracks { get; }

    /// <summary>
    /// Builds a file from the pieces.
    /// </summary>
    public MidiFile(ushort format, ushort ticksPerBeat, MidiTrack[] tracks)
    {
        Format = format;
        TicksPerBeat = ticksPerBeat;
        Tracks = tracks;
    }
}

/// <summary>
/// One track — a run of timed events.
/// </summary>
public sealed class MidiTrack
{
    /// <summary>
    /// The events, in order.
    /// </summary>
    public IReadOnlyList<MidiEvent> Events { get; }

    /// <summary>
    /// Wraps an event list as a track.
    /// </summary>
    public MidiTrack(List<MidiEvent> events)
    {
        Events = events;
    }
}

/// <summary>
/// One timed event in a track: plain MIDI, meta, or SysEx.
/// </summary>
public readonly struct MidiEvent
{
    /// <summary>
    /// Ticks since the previous event.
    /// </summary>
    public readonly int DeltaTime;

    /// <summary>
    /// Which of the three flavours this is.
    /// </summary>
    public readonly MidiEventType Type;

    /// <summary>
    /// Status byte — 0xFF for meta, 0xF0 for SysEx.
    /// </summary>
    public readonly byte Status;

    /// <summary>
    /// First data byte, unused on meta / SysEx.
    /// </summary>
    public readonly byte Data1;

    /// <summary>
    /// Second data byte, unused on meta / SysEx.
    /// </summary>
    public readonly byte Data2;

    /// <summary>
    /// Meta sub-type, e.g. 0x51 tempo, 0x2F end of track.
    /// </summary>
    public readonly byte MetaType;

    /// <summary>
    /// Meta / SysEx payload, null on a plain MIDI event.
    /// </summary>
    public readonly byte[]? MetaData;

    /// <summary>
    /// Plain channel event.
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
    /// Meta event with sub-type and payload.
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
    /// SysEx event, payload includes the leading 0xF0.
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
    /// End-of-track marker (meta 0x2F).
    /// </summary>
    public bool IsEndOfTrack => Type == MidiEventType.Meta && MetaType == 0x2F;

    /// <summary>
    /// Tempo change (meta 0x51).
    /// </summary>
    public bool IsTempoChange => Type == MidiEventType.Meta && MetaType == 0x51;

    /// <summary>
    /// Microseconds per quarter note. Falls back to 500000 (120 BPM) if this
    /// isn't a usable tempo event.
    /// </summary>
    public int GetTempoMicroseconds()
    {
        if (!IsTempoChange || MetaData is null || MetaData.Length < 3)
            return 500_000;
        return (MetaData[0] << 16) | (MetaData[1] << 8) | MetaData[2];
    }
}

/// <summary>
/// What kind of thing a MidiEvent holds.
/// </summary>
public enum MidiEventType : byte
{
    /// <summary>
    /// Ordinary channel message — note on/off, CC, and friends.
    /// </summary>
    Midi,

    /// <summary>
    /// File-level info: tempo, time signature, track name, etc.
    /// </summary>
    Meta,

    /// <summary>
    /// System Exclusive, vendor specific payload.
    /// </summary>
    SysEx
}
