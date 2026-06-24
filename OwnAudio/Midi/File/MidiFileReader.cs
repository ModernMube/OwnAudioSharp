using System.Runtime.InteropServices;
using OwnAudio.Midi.Internal;
using OwnAudio.Midi.Interop;

namespace OwnAudio.Midi.File;

/// <summary>
/// Reads Standard MIDI Files (SMF) from a file path or stream and returns a
/// <see cref="MidiFile"/> object. Parsing is performed by the native MIDI core;
/// this class rebuilds the managed model from the parsed native representation.
/// </summary>
public static class MidiFileReader
{
    /// <summary>
    /// Reads a MIDI file from the specified path and returns the parsed <see cref="MidiFile"/>.
    /// </summary>
    public static MidiFile Read(string path)
    {
        using var stream = System.IO.File.OpenRead(path);
        return Read(stream);
    }

    /// <summary>
    /// Reads a MIDI file from a stream and returns the parsed <see cref="MidiFile"/>.
    /// Throws <see cref="InvalidDataException"/> if the stream does not contain a valid SMF.
    /// </summary>
    public static MidiFile Read(Stream stream)
    {
        byte[] data = ReadAllBytes(stream);

        unsafe
        {
            fixed (byte* ptr = data)
            {
                int code = MidiNativeMethods.ownaudio_midi_v1_file_parse(
                    ptr, (nuint)data.Length, out var fileHandle);
                MidiErrorCodeMapper.ThrowIfError(code, nameof(Read));
                using (fileHandle)
                {
                    return BuildMidiFile(fileHandle);
                }
            }
        }
    }

    /// <summary>
    /// Reads the entire stream contents into a byte array.
    /// </summary>
    private static byte[] ReadAllBytes(Stream stream)
    {
        if (stream is MemoryStream existing)
        {
            return existing.ToArray();
        }

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>
    /// Builds the managed <see cref="MidiFile"/> by querying the parsed native file.
    /// </summary>
    /// <param name="handle">
    /// Handle to the parsed native MIDI file.
    /// </param>
    private static MidiFile BuildMidiFile(MidiFileHandle handle)
    {
        MidiNativeMethods.ownaudio_midi_v1_file_get_format(handle, out ushort format);
        MidiNativeMethods.ownaudio_midi_v1_file_get_ticks_per_beat(handle, out ushort ticksPerBeat);
        MidiNativeMethods.ownaudio_midi_v1_file_get_track_count(handle, out nuint trackCount);

        var tracks = new MidiTrack[(int)trackCount];
        for (nuint t = 0; t < trackCount; t++)
        {
            MidiNativeMethods.ownaudio_midi_v1_file_get_event_count(handle, t, out nuint eventCount);
            var events = new List<MidiEvent>((int)eventCount);
            for (nuint e = 0; e < eventCount; e++)
            {
                MidiNativeMethods.ownaudio_midi_v1_file_get_event(handle, t, e, out var nativeEvent);
                events.Add(ConvertNativeEvent(nativeEvent));
            }
            tracks[(int)t] = new MidiTrack(events);
        }

        return new MidiFile(format, ticksPerBeat, tracks);
    }

    /// <summary>
    /// Converts a native event struct into the corresponding managed <see cref="MidiEvent"/>.
    /// </summary>
    /// <param name="nativeEvent">
    /// The native event read from the parsed file.
    /// </param>
    private static MidiEvent ConvertNativeEvent(NativeMidiEvent nativeEvent)
    {
        switch (nativeEvent.EventType)
        {
            case 1:
                return new MidiEvent(nativeEvent.DeltaTime, nativeEvent.MetaType, ReadPayload(nativeEvent));
            case 2:
                return new MidiEvent(nativeEvent.DeltaTime, ReadPayload(nativeEvent));
            default:
                return new MidiEvent(
                    nativeEvent.DeltaTime, nativeEvent.Status, nativeEvent.Data1, nativeEvent.Data2);
        }
    }

    /// <summary>
    /// Copies a native event payload into a managed byte array.
    /// </summary>
    /// <param name="nativeEvent">
    /// The native event whose <see cref="NativeMidiEvent.MetaData"/> is copied.
    /// </param>
    private static byte[] ReadPayload(NativeMidiEvent nativeEvent)
    {
        int length = (int)nativeEvent.MetaDataLen;
        if (length == 0 || nativeEvent.MetaData == IntPtr.Zero)
        {
            return Array.Empty<byte>();
        }

        var payload = new byte[length];
        Marshal.Copy(nativeEvent.MetaData, payload, 0, length);
        return payload;
    }
}
