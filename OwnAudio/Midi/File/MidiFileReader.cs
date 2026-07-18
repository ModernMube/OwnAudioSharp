using System.Runtime.InteropServices;
using OwnAudio.Midi.Internal;
using OwnAudio.Midi.Interop;

namespace OwnAudio.Midi.File;

/// <summary>
/// Loads Standard MIDI Files. The native core does the parsing, we just rebuild
/// the managed model out of what it hands back.
/// </summary>
public static class MidiFileReader
{
    /// <summary>
    /// Reads the SMF at the given path.
    /// </summary>
    public static MidiFile Read(string path)
    {
        using var stream = System.IO.File.OpenRead(path);
        return Read(stream);
    }

    /// <summary>
    /// Reads an SMF off a stream. Throws InvalidDataException on garbage input.
    /// </summary>
    public static unsafe MidiFile Read(Stream stream)
    {
        byte[] _data = _readAllBytes(stream);

        fixed (byte* ptr = _data)
        {
            int code = MidiNativeMethods.ownaudio_midi_v1_file_parse(ptr, (nuint)_data.Length, out var _file);
            MidiErrorCodeMapper.ThrowIfError(code, nameof(Read));
            using (_file)
            {
                return _buildMidiFile(_file);
            }
        }
    }

    /// <summary>
    /// Slurps the whole stream into one array.
    /// </summary>
    private static byte[] _readAllBytes(Stream stream)
    {
        if (stream is MemoryStream _existing) return _existing.ToArray();

        using (var _memory = new MemoryStream())
        {
            stream.CopyTo(_memory);
            return _memory.ToArray();
        }
    }

    /// <summary>
    /// Walks the parsed native file and puts the managed objects together.
    /// </summary>
    private static MidiFile _buildMidiFile(MidiFileHandle handle)
    {
        MidiNativeMethods.ownaudio_midi_v1_file_get_format(handle, out ushort _format);
        MidiNativeMethods.ownaudio_midi_v1_file_get_ticks_per_beat(handle, out ushort _tpb);
        MidiNativeMethods.ownaudio_midi_v1_file_get_track_count(handle, out nuint _trackCount);

        var _tracks = new MidiTrack[(int)_trackCount];
        for (nuint t = 0; t < _trackCount; t++)
        {
            MidiNativeMethods.ownaudio_midi_v1_file_get_event_count(handle, t, out nuint _eventCount);
            var _events = new List<MidiEvent>((int)_eventCount);
            for (nuint e = 0; e < _eventCount; e++)
            {
                MidiNativeMethods.ownaudio_midi_v1_file_get_event(handle, t, e, out var _native);
                _events.Add(_convertNativeEvent(_native));
            }
            _tracks[(int)t] = new MidiTrack(_events);
        }

        return new MidiFile(_format, _tpb, _tracks);
    }

    /// <summary>
    /// Native event struct to managed MidiEvent.
    /// </summary>
    private static MidiEvent _convertNativeEvent(NativeMidiEvent nativeEvent)
    {
        switch (nativeEvent.EventType)
        {
            case 1:
                return new MidiEvent(nativeEvent.DeltaTime, nativeEvent.MetaType, _readPayload(nativeEvent));
            case 2:
                return new MidiEvent(nativeEvent.DeltaTime, _readPayload(nativeEvent));
            default:
                return new MidiEvent(nativeEvent.DeltaTime, nativeEvent.Status, nativeEvent.Data1, nativeEvent.Data2);
        }
    }

    /// <summary>
    /// Copies the payload out of native memory before the file handle dies.
    /// </summary>
    private static byte[] _readPayload(NativeMidiEvent nativeEvent)
    {
        int _len = (int)nativeEvent.MetaDataLen;
        if(_len == 0 || nativeEvent.MetaData == IntPtr.Zero) return Array.Empty<byte>();

        var _payload = new byte[_len];
        Marshal.Copy(nativeEvent.MetaData, _payload, 0, _len);
        return _payload;
    }
}
