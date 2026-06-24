using System.Runtime.InteropServices;
using OwnAudio.Midi.Internal;

namespace OwnAudio.Midi.Interop;

/// <summary>
/// Single point of entry for every native <c>ownaudio_midi_ffi</c> call. All
/// declarations use source-generated <c>LibraryImport</c> for AOT compatibility.
/// Each function returns an <c>int</c> error code (see <see cref="MidiErrorCode"/>)
/// unless it has no failure mode.
/// </summary>
internal static unsafe partial class MidiNativeMethods
{
    /// <summary>
    /// Logical native library name resolved by <see cref="MidiNativeLibraryLoader"/>.
    /// </summary>
    private const string LibName = MidiNativeLibraryLoader.LogicalName;

    /// <summary>
    /// Registers the native library resolver before the first P/Invoke call.
    /// </summary>
    static MidiNativeMethods()
    {
        MidiNativeLibraryLoader.EnsureRegistered();
    }

    #region Port Enumeration

    /// <summary>
    /// Retrieves the list of MIDI input port names as a native array of UTF-8 strings.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_list_input_ports(
        out IntPtr outNames, out nuint outCount);

    /// <summary>
    /// Retrieves the list of MIDI output port names as a native array of UTF-8 strings.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_list_output_ports(
        out IntPtr outNames, out nuint outCount);

    /// <summary>
    /// Releases a port-name array previously returned by an enumeration call.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial void ownaudio_midi_v1_free_port_names(
        IntPtr names, nuint count);

    #endregion

    #region Input Port

    /// <summary>
    /// Opens a hardware MIDI input port by name.
    /// </summary>
    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ownaudio_midi_v1_input_port_open(
        string portName, out MidiInputPortHandle outHandle);

    /// <summary>
    /// Creates a virtual MIDI input port.
    /// </summary>
    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ownaudio_midi_v1_create_virtual_input(
        string name, out MidiInputPortHandle outHandle);

    /// <summary>
    /// Starts delivering short messages and SysEx data to the supplied callbacks.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_input_port_start_with_sysex(
        MidiInputPortHandle handle,
        delegate* unmanaged<NativeMidiMessage, IntPtr, void> msgCallback,
        delegate* unmanaged<IntPtr, nuint, IntPtr, void> sysexCallback,
        IntPtr userData);

    /// <summary>
    /// Stops delivery of incoming messages, closing the active connection.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_input_port_stop(
        MidiInputPortHandle handle);

    /// <summary>
    /// Destroys an input port handle. Called from the SafeHandle release path.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial void ownaudio_midi_v1_input_port_destroy(IntPtr handle);

    #endregion

    #region Output Port

    /// <summary>
    /// Opens a hardware MIDI output port by name.
    /// </summary>
    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ownaudio_midi_v1_output_port_open(
        string portName, out MidiOutputPortHandle outHandle);

    /// <summary>
    /// Creates a virtual MIDI output port.
    /// </summary>
    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ownaudio_midi_v1_create_virtual_output(
        string name, out MidiOutputPortHandle outHandle);

    /// <summary>
    /// Sends a short MIDI message to the output port.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_output_port_send(
        MidiOutputPortHandle handle, NativeMidiMessage msg);

    /// <summary>
    /// Sends a raw SysEx byte sequence to the output port.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_output_port_send_sysex(
        MidiOutputPortHandle handle, byte* data, nuint len);

    /// <summary>
    /// Destroys an output port handle. Called from the SafeHandle release path.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial void ownaudio_midi_v1_output_port_destroy(IntPtr handle);

    #endregion

    #region Clock

    /// <summary>
    /// Creates a stopped MIDI timing clock at the given tempo.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_clock_create(
        double bpm, out MidiClockHandle outHandle);

    /// <summary>
    /// Sets the clock tempo in beats per minute.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_clock_set_bpm(
        MidiClockHandle handle, double bpm);

    /// <summary>
    /// Starts the clock thread, invoking the pulse callback for each timing pulse.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_clock_start(
        MidiClockHandle handle,
        delegate* unmanaged<IntPtr, void> pulseCallback,
        IntPtr userData);

    /// <summary>
    /// Stops the clock thread and waits for it to exit.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_clock_stop(MidiClockHandle handle);

    /// <summary>
    /// Destroys a clock handle. Called from the SafeHandle release path.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial void ownaudio_midi_v1_clock_destroy(IntPtr handle);

    #endregion

    #region File Parsing

    /// <summary>
    /// Parses a Standard MIDI File from a byte buffer.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_file_parse(
        byte* data, nuint len, out MidiFileHandle outHandle);

    /// <summary>
    /// Reads the parsed file's SMF format word.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_file_get_format(
        MidiFileHandle handle, out ushort outFormat);

    /// <summary>
    /// Reads the parsed file's ticks-per-beat resolution.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_file_get_ticks_per_beat(
        MidiFileHandle handle, out ushort outTpb);

    /// <summary>
    /// Reads the parsed file's track count.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_file_get_track_count(
        MidiFileHandle handle, out nuint outCount);

    /// <summary>
    /// Reads the number of events in the given track.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_file_get_event_count(
        MidiFileHandle handle, nuint trackIndex, out nuint outCount);

    /// <summary>
    /// Reads a single event from the given track.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_file_get_event(
        MidiFileHandle handle, nuint trackIndex, nuint eventIndex,
        out NativeMidiEvent outEvent);

    /// <summary>
    /// Destroys a parsed file handle. Called from the SafeHandle release path.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial void ownaudio_midi_v1_file_destroy(IntPtr handle);

    #endregion

    #region File Serialization

    /// <summary>
    /// Creates a MIDI file writer with the given format and timing resolution.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_writer_create(
        ushort format, ushort ticksPerBeat, out MidiWriterHandle outHandle);

    /// <summary>
    /// Begins a new, empty track in the writer.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_writer_begin_track(MidiWriterHandle handle);

    /// <summary>
    /// Appends an event to the writer's current track.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_writer_add_event(
        MidiWriterHandle handle, NativeMidiEvent evt);

    /// <summary>
    /// Serializes all added tracks to a native SMF byte buffer.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_writer_serialize(
        MidiWriterHandle handle, out IntPtr outData, out nuint outLen);

    /// <summary>
    /// Destroys a writer handle. Called from the SafeHandle release path.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial void ownaudio_midi_v1_writer_destroy(IntPtr handle);

    /// <summary>
    /// Releases a byte buffer returned by <see cref="ownaudio_midi_v1_writer_serialize"/>.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial void ownaudio_midi_v1_free_bytes(IntPtr data, nuint len);

    #endregion
}
