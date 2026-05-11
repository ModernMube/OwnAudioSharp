using System.Buffers.Binary;

namespace OwnAudio.Midi.File;

/// <summary>
/// Writes a <see cref="MidiFile"/> to a file path or stream in Standard MIDI File (SMF) format.
/// Uses running status compression and automatically appends an End-of-Track meta event when missing.
/// </summary>
public static class MidiFileWriter
{
    /// <summary>
    /// Writes the <see cref="MidiFile"/> to the file at the specified path, creating or overwriting it.
    /// </summary>
    public static void Write(MidiFile file, string path)
    {
        using var stream = System.IO.File.Create(path);
        Write(file, stream);
    }

    /// <summary>
    /// Writes the <see cref="MidiFile"/> to the given stream in SMF binary format.
    /// </summary>
    public static void Write(MidiFile file, Stream stream)
    {
        Span<byte> header = stackalloc byte[14];
        header[0] = (byte)'M'; header[1] = (byte)'T';
        header[2] = (byte)'h'; header[3] = (byte)'d';
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], 6);
        BinaryPrimitives.WriteUInt16BigEndian(header[8..], file.Format);
        BinaryPrimitives.WriteUInt16BigEndian(header[10..], (ushort)file.Tracks.Count);
        BinaryPrimitives.WriteUInt16BigEndian(header[12..], file.TicksPerBeat);
        stream.Write(header);

        foreach (var track in file.Tracks)
            WriteTrack(track, stream);
    }

    /// <summary>
    /// Serializes a single track into an MTrk chunk and writes it to the stream.
    /// Applies running-status compression and ensures the chunk ends with an End-of-Track event.
    /// </summary>
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
            else
            {
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

        if (track.Events.Count == 0 || !track.Events[^1].IsEndOfTrack)
        {
            WriteVarLen(trackBuffer, 0);
            trackBuffer.WriteByte(0xFF);
            trackBuffer.WriteByte(0x2F);
            trackBuffer.WriteByte(0x00);
        }

        Span<byte> chunkHeader = stackalloc byte[8];
        chunkHeader[0] = (byte)'M'; chunkHeader[1] = (byte)'T';
        chunkHeader[2] = (byte)'r'; chunkHeader[3] = (byte)'k';
        BinaryPrimitives.WriteUInt32BigEndian(chunkHeader[4..], (uint)trackBuffer.Length);
        stream.Write(chunkHeader);
        trackBuffer.Position = 0;
        trackBuffer.CopyTo(stream);
    }

    /// <summary>
    /// Encodes an integer as a MIDI variable-length quantity and writes it to the stream.
    /// Negative values are clamped to zero.
    /// </summary>
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

        for (int i = len - 1; i >= 0; i--)
            stream.WriteByte(buf[i]);
    }
}
