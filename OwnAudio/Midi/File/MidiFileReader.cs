using System.Buffers.Binary;

namespace OwnAudio.Midi.File;

public static class MidiFileReader
{
    public static MidiFile Read(string path)
    {
        using var stream = System.IO.File.OpenRead(path);
        return Read(stream);
    }

    public static MidiFile Read(Stream stream)
    {
        Span<byte> header = stackalloc byte[14];
        stream.ReadExactly(header);

        if (header[0] != 'M' || header[1] != 'T' || header[2] != 'h' || header[3] != 'd')
            throw new InvalidDataException("Not a valid MIDI file (missing MThd header).");

        uint chunkLength = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        if (chunkLength < 6) throw new InvalidDataException("Invalid MThd chunk length.");

        ushort format = BinaryPrimitives.ReadUInt16BigEndian(header[8..]);
        ushort trackCount = BinaryPrimitives.ReadUInt16BigEndian(header[10..]);
        ushort ticksPerBeat = BinaryPrimitives.ReadUInt16BigEndian(header[12..]);

        // Skip any extra header bytes beyond the standard 6
        if (chunkLength > 6)
            stream.Seek(chunkLength - 6, SeekOrigin.Current);

        var tracks = new MidiTrack[trackCount];
        for (int i = 0; i < trackCount; i++)
            tracks[i] = ReadTrack(stream);

        return new MidiFile(format, ticksPerBeat, tracks);
    }

    private static MidiTrack ReadTrack(Stream stream)
    {
        Span<byte> trackHeader = stackalloc byte[8];
        stream.ReadExactly(trackHeader);

        if (trackHeader[0] != 'M' || trackHeader[1] != 'T' || trackHeader[2] != 'r' || trackHeader[3] != 'k')
            throw new InvalidDataException("Expected MTrk chunk.");

        int length = (int)BinaryPrimitives.ReadUInt32BigEndian(trackHeader[4..]);
        var data = new byte[length];
        stream.ReadExactly(data);

        return new MidiTrack(ParseEvents(data));
    }

    private static List<MidiEvent> ParseEvents(ReadOnlySpan<byte> data)
    {
        var events = new List<MidiEvent>(64);
        int pos = 0;
        byte runningStatus = 0;

        while (pos < data.Length)
        {
            int delta = ReadVarLen(data, ref pos);
            if (pos >= data.Length) break;

            byte b = data[pos];

            if (b == 0xFF) // Meta event
            {
                pos++;
                if (pos >= data.Length) break;
                byte metaType = data[pos++];
                int metaLen = ReadVarLen(data, ref pos);
                byte[] metaData = data.Slice(pos, metaLen).ToArray();
                pos += metaLen;
                events.Add(new MidiEvent(delta, metaType, metaData));
                if (metaType == 0x2F) break; // End of Track
                continue;
            }

            if (b == 0xF0 || b == 0xF7) // SysEx
            {
                pos++;
                int sysexLen = ReadVarLen(data, ref pos);
                var sysex = new byte[sysexLen + 1];
                sysex[0] = b;
                data.Slice(pos, sysexLen).CopyTo(sysex.AsSpan(1));
                pos += sysexLen;
                runningStatus = 0;
                events.Add(new MidiEvent(delta, sysex));
                continue;
            }

            // MIDI event – running status
            if ((b & 0x80) != 0)
            {
                runningStatus = b;
                pos++;
            }

            if (runningStatus == 0) continue;

            byte type = (byte)(runningStatus & 0xF0);
            byte d1 = pos < data.Length ? data[pos++] : (byte)0;

            // 2-byte messages: Program Change (0xC0), Channel Pressure (0xD0)
            if (type == 0xC0 || type == 0xD0)
            {
                events.Add(new MidiEvent(delta, runningStatus, d1, 0));
            }
            else
            {
                byte d2 = pos < data.Length ? data[pos++] : (byte)0;
                events.Add(new MidiEvent(delta, runningStatus, d1, d2));
            }
        }

        return events;
    }

    private static int ReadVarLen(ReadOnlySpan<byte> data, ref int pos)
    {
        int value = 0;
        for (int i = 0; i < 4 && pos < data.Length; i++)
        {
            byte b = data[pos++];
            value = (value << 7) | (b & 0x7F);
            if ((b & 0x80) == 0) break;
        }
        return value;
    }
}
