using System.Buffers.Binary;

namespace OwnAudio.Midi.File;

public static class MidiFileWriter
{
    public static void Write(MidiFile file, string path)
    {
        using var stream = System.IO.File.Create(path);
        Write(file, stream);
    }

    public static void Write(MidiFile file, Stream stream)
    {
        // MThd header
        Span<byte> header = stackalloc byte[14];
        header[0] = (byte)'M'; header[1] = (byte)'T';
        header[2] = (byte)'h'; header[3] = (byte)'d';
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], 6); // chunk length always 6
        BinaryPrimitives.WriteUInt16BigEndian(header[8..], file.Format);
        BinaryPrimitives.WriteUInt16BigEndian(header[10..], (ushort)file.Tracks.Count);
        BinaryPrimitives.WriteUInt16BigEndian(header[12..], file.TicksPerBeat);
        stream.Write(header);

        foreach (var track in file.Tracks)
            WriteTrack(track, stream);
    }

    private static void WriteTrack(MidiTrack track, Stream stream)
    {
        using var trackBuffer = new MemoryStream();

        byte runningStatus = 0;
        foreach (var evt in track.Events)
        {
            WriteVarLen(trackBuffer, evt.DeltaTime);

            if (evt.Type == MidiEventType.Meta)
            {
                trackBuffer.WriteByte(0xFF);
                trackBuffer.WriteByte(evt.MetaType);
                WriteVarLen(trackBuffer, evt.MetaData?.Length ?? 0);
                if (evt.MetaData != null)
                    trackBuffer.Write(evt.MetaData);
                runningStatus = 0;
            }
            else if (evt.Type == MidiEventType.SysEx)
            {
                if (evt.MetaData != null)
                    trackBuffer.Write(evt.MetaData);
                runningStatus = 0;
            }
            else // MIDI
            {
                // Write status byte only if it differs from running status
                if (evt.Status != runningStatus)
                {
                    trackBuffer.WriteByte(evt.Status);
                    runningStatus = evt.Status;
                }
                trackBuffer.WriteByte(evt.Data1);

                byte type = (byte)(evt.Status & 0xF0);
                if (type != 0xC0 && type != 0xD0)
                    trackBuffer.WriteByte(evt.Data2);
            }
        }

        // Ensure track ends with End of Track meta event
        if (track.Events.Count == 0 || !track.Events[^1].IsEndOfTrack)
        {
            WriteVarLen(trackBuffer, 0); // delta time 0
            trackBuffer.WriteByte(0xFF);
            trackBuffer.WriteByte(0x2F);
            trackBuffer.WriteByte(0x00);
        }

        // Write MTrk header + track data
        Span<byte> chunkHeader = stackalloc byte[8];
        chunkHeader[0] = (byte)'M'; chunkHeader[1] = (byte)'T';
        chunkHeader[2] = (byte)'r'; chunkHeader[3] = (byte)'k';
        BinaryPrimitives.WriteUInt32BigEndian(chunkHeader[4..], (uint)trackBuffer.Length);
        stream.Write(chunkHeader);
        trackBuffer.Position = 0;
        trackBuffer.CopyTo(stream);
    }

    private static void WriteVarLen(Stream stream, int value)
    {
        if (value < 0) value = 0;
        Span<byte> buf = stackalloc byte[4];
        int len = 0;

        buf[len++] = (byte)(value & 0x7F);
        value >>= 7;
        while (value > 0)
        {
            buf[len++] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }

        // Write in reverse (most-significant byte first)
        for (int i = len - 1; i >= 0; i--)
            stream.WriteByte(buf[i]);
    }
}
