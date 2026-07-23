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

                _addTrackEvents(writer, _track.Events);
            }

            _serialize(writer, stream);
        }
    }

    /// <summary>
    /// Pushes a whole track's events into the writer's current track in one call.
    /// Every payload goes into a single pinned blob and each native event points
    /// into it, so a big track costs one FFI crossing instead of one per event.
    /// </summary>
    private static unsafe void _addTrackEvents(MidiWriterHandle writer, IReadOnlyList<MidiEvent> events)
    {
        int _count = events.Count;
        if (_count == 0) return;

        var _native = new NativeMidiEvent[_count];
        var _offsets = new int[_count];
        int _blobLen = 0;

        for (int i = 0; i < _count; i++)
        {
            var _evt = events[i];
            byte[]? _payload = _evt.MetaData;
            int _payloadLen = _payload?.Length ?? 0;

            _offsets[i] = _payloadLen > 0 ? _blobLen : -1;
            _blobLen += _payloadLen;

            _native[i] = new NativeMidiEvent
            {
                DeltaTime = _evt.DeltaTime,
                EventType = _eventTypeToByte(_evt.Type),
                Status = _evt.Status,
                Data1 = _evt.Data1,
                Data2 = _evt.Data2,
                MetaType = _evt.MetaType,
                MetaDataLen = (nuint)_payloadLen
            };
        }

        var _blob = new byte[_blobLen];
        for (int i = 0; i < _count; i++)
        {
            if (_offsets[i] < 0) continue;
            byte[] _payload = events[i].MetaData!;
            Buffer.BlockCopy(_payload, 0, _blob, _offsets[i], _payload.Length);
        }

        int code;
        fixed (byte* _blobPtr = _blob)
        fixed (NativeMidiEvent* _evPtr = _native)
        {
            for (int i = 0; i < _count; i++)
                if (_offsets[i] >= 0)
                    _evPtr[i].MetaData = (IntPtr)(_blobPtr + _offsets[i]);

            code = MidiNativeMethods.ownaudio_midi_v1_writer_add_events(writer, _evPtr, (nuint)_count);
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
