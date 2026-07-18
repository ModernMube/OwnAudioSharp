using System.Runtime.InteropServices;
using OwnAudio.Midi.Internal;
using OwnAudio.Midi.Interop;

namespace OwnAudio.Midi.File;

/// <summary>
/// Dumps a MidiFile back out as SMF. The native core does the serializing — it
/// also handles running-status compression and tacks on End-of-Track if missing.
/// </summary>
public static class MidiFileWriter
{
    /// <summary>
    /// Writes to the given path, creating or overwriting.
    /// </summary>
    public static void Write(MidiFile file, string path)
    {
        using var stream = System.IO.File.Create(path);
        Write(file, stream);
    }

    /// <summary>
    /// Writes SMF bytes into the stream.
    /// </summary>
    public static void Write(MidiFile file, Stream stream)
    {
        int code = MidiNativeMethods.ownaudio_midi_v1_writer_create(file.Format, file.TicksPerBeat, out var writer);
        MidiErrorCodeMapper.ThrowIfError(code, nameof(Write));

        using (writer)
        {
            foreach (var _track in file.Tracks)
            {
                code = MidiNativeMethods.ownaudio_midi_v1_writer_begin_track(writer);
                MidiErrorCodeMapper.ThrowIfError(code, nameof(Write));

                foreach (var _evt in _track.Events)
                    _addEvent(writer, _evt);
            }

            _serialize(writer, stream);
        }
    }

    /// <summary>
    /// Pushes one event into the writer's current track, payload pinned for the call.
    /// </summary>
    private static unsafe void _addEvent(MidiWriterHandle writer, MidiEvent evt)
    {
        byte[]? _payload = evt.MetaData;

        int code;
        fixed (byte* ptr = _payload)
        {
            var _native = new NativeMidiEvent
            {
                DeltaTime = evt.DeltaTime,
                EventType = _eventTypeToByte(evt.Type),
                Status = evt.Status,
                Data1 = evt.Data1,
                Data2 = evt.Data2,
                MetaType = evt.MetaType,
                MetaData = (IntPtr)ptr,
                MetaDataLen = (nuint)(_payload?.Length ?? 0)
            };
            code = MidiNativeMethods.ownaudio_midi_v1_writer_add_event(writer, _native);
        }
        MidiErrorCodeMapper.ThrowIfError(code, nameof(Write));
    }

    /// <summary>
    /// Bakes the SMF bytes, copies them to the stream, then frees the native buffer.
    /// </summary>
    private static void _serialize(MidiWriterHandle writer, Stream stream)
    {
        int code = MidiNativeMethods.ownaudio_midi_v1_writer_serialize(writer, out IntPtr data, out nuint len);
        MidiErrorCodeMapper.ThrowIfError(code, nameof(Write));

        if (data == IntPtr.Zero || len == 0) return;

        try
        {
            int _len = (int)len;
            var _buffer = new byte[_len];
            Marshal.Copy(data, _buffer, 0, _len);
            stream.Write(_buffer, 0, _len);
        }
        finally
        {
            MidiNativeMethods.ownaudio_midi_v1_free_bytes(data, len);
        }
    }

    /// <summary>
    /// Managed event type to the native discriminant.
    /// </summary>
    private static byte _eventTypeToByte(MidiEventType type) => type switch
    {
        MidiEventType.Meta => 1,
        MidiEventType.SysEx => 2,
        _ => 0
    };
}
