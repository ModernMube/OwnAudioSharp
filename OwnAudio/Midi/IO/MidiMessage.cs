namespace OwnAudio.Midi.IO;

public readonly struct MidiMessage
{
    public readonly byte Status;
    public readonly byte Data1;
    public readonly byte Data2;
    public readonly long Timestamp; // nanoseconds

    public MidiMessage(byte status, byte data1, byte data2, long timestamp = 0)
    {
        Status = status;
        Data1 = data1;
        Data2 = data2;
        Timestamp = timestamp;
    }

    public MidiMessageType Type => (MidiMessageType)(Status & 0xF0);
    public int Channel => Status & 0x0F;

    public bool IsNoteOn => Type == MidiMessageType.NoteOn && Data2 > 0;
    public bool IsNoteOff => Type == MidiMessageType.NoteOff || (Type == MidiMessageType.NoteOn && Data2 == 0);
    public bool IsControlChange => Type == MidiMessageType.ControlChange;
    public bool IsProgramChange => Type == MidiMessageType.ProgramChange;
    public bool IsPitchBend => Type == MidiMessageType.PitchBend;

    public override string ToString() =>
        $"[{Type} Ch={Channel} D1={Data1} D2={Data2}]";
}

public enum MidiMessageType : byte
{
    NoteOff         = 0x80,
    NoteOn          = 0x90,
    Aftertouch      = 0xA0,
    ControlChange   = 0xB0,
    ProgramChange   = 0xC0,
    ChannelPressure = 0xD0,
    PitchBend       = 0xE0,
    SysEx           = 0xF0,
    Meta            = 0xFF
}
