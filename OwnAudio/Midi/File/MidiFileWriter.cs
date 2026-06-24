using System.Runtime.InteropServices;
using OwnAudio.Midi.Internal;
using OwnAudio.Midi.Interop;

namespace OwnAudio.Midi.File;

/// <summary>
/// Writes a <see cref="MidiFile"/> to a file path or stream in Standard MIDI File
/// (SMF) format. Serialization is performed by the native MIDI core, which applies
/// running-status compression and appends an End-of-Track meta event when missing.
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
        int code = MidiNativeMethods.ownaudio_midi_v1_writer_create(
            file.Format, file.TicksPerBeat, out var writer);
        MidiErrorCodeMapper.ThrowIfError(code, nameof(Write));

        using (writer)
        {
            foreach (var track in file.Tracks)
            {
                code = MidiNativeMethods.ownaudio_midi_v1_writer_begin_track(writer);
                MidiErrorCodeMapper.ThrowIfError(code, nameof(Write));

                foreach (var evt in track.Events)
                {
                    AddEvent(writer, evt);
                }
            }

            Serialize(writer, stream);
        }
    }

    /// <summary>
    /// Appends a single event to the native writer's current track, pinning any
    /// payload bytes for the duration of the native call.
    /// </summary>
    /// <param name="writer">
    /// The native writer handle.
    /// </param>
    /// <param name="evt">
    /// The managed event to serialize.
    /// </param>
    private static void AddEvent(MidiWriterHandle writer, MidiEvent evt)
    {
        byte[]? payload = evt.MetaData;
        int length = payload?.Length ?? 0;

        int code;
        unsafe
        {
            fixed (byte* ptr = payload)
            {
                var native = new NativeMidiEvent
                {
                    DeltaTime = evt.DeltaTime,
                    EventType = EventTypeToByte(evt.Type),
                    Status = evt.Status,
                    Data1 = evt.Data1,
                    Data2 = evt.Data2,
                    MetaType = evt.MetaType,
                    MetaData = (IntPtr)ptr,
                    MetaDataLen = (nuint)length
                };
                code = MidiNativeMethods.ownaudio_midi_v1_writer_add_event(writer, native);
            }
        }
        MidiErrorCodeMapper.ThrowIfError(code, nameof(Write));
    }

    /// <summary>
    /// Serializes the writer to SMF bytes and copies them into <paramref name="stream"/>,
    /// releasing the native buffer afterwards.
    /// </summary>
    /// <param name="writer">
    /// The native writer handle.
    /// </param>
    /// <param name="stream">
    /// The destination stream.
    /// </param>
    private static void Serialize(MidiWriterHandle writer, Stream stream)
    {
        int code = MidiNativeMethods.ownaudio_midi_v1_writer_serialize(
            writer, out IntPtr data, out nuint len);
        MidiErrorCodeMapper.ThrowIfError(code, nameof(Write));

        if (data == IntPtr.Zero || len == 0)
        {
            return;
        }

        try
        {
            int length = (int)len;
            var buffer = new byte[length];
            Marshal.Copy(data, buffer, 0, length);
            stream.Write(buffer, 0, length);
        }
        finally
        {
            MidiNativeMethods.ownaudio_midi_v1_free_bytes(data, len);
        }
    }

    /// <summary>
    /// Maps a managed <see cref="MidiEventType"/> to the native event-type discriminant.
    /// </summary>
    /// <param name="type">
    /// The managed event type.
    /// </param>
    private static byte EventTypeToByte(MidiEventType type) => type switch
    {
        MidiEventType.Meta => 1,
        MidiEventType.SysEx => 2,
        _ => 0
    };
}
