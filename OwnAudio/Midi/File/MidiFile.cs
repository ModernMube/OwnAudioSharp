namespace OwnAudio.Midi.File;

public sealed class MidiFile
{
    public ushort Format { get; }
    public ushort TicksPerBeat { get; }
    public IReadOnlyList<MidiTrack> Tracks { get; }

    public MidiFile(ushort format, ushort ticksPerBeat, MidiTrack[] tracks)
    {
        Format = format;
        TicksPerBeat = ticksPerBeat;
        Tracks = tracks;
    }
}

public sealed class MidiTrack
{
    public IReadOnlyList<MidiEvent> Events { get; }

    public MidiTrack(List<MidiEvent> events)
    {
        Events = events;
    }
}

public readonly struct MidiEvent
{
    public readonly int DeltaTime;
    public readonly MidiEventType Type;
    public readonly byte Status;
    public readonly byte Data1;
    public readonly byte Data2;
    public readonly byte MetaType;
    public readonly byte[]? MetaData;

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

    public bool IsEndOfTrack => Type == MidiEventType.Meta && MetaType == 0x2F;
    public bool IsTempoChange => Type == MidiEventType.Meta && MetaType == 0x51;

    public int GetTempoMicroseconds()
    {
        if (!IsTempoChange || MetaData is null || MetaData.Length < 3)
            return 500_000; // default 120 BPM
        return (MetaData[0] << 16) | (MetaData[1] << 8) | MetaData[2];
    }
}

public enum MidiEventType : byte
{
    Midi,
    Meta,
    SysEx
}
