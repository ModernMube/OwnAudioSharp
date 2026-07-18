using System.Runtime.InteropServices;
using OwnAudio.Midi.Internal;

namespace OwnAudio.Midi.Interop;

/// <summary>
/// Every ownaudio_midi_ffi call lives here. Source-generated LibraryImport so it
/// survives AOT. Return value is a MidiErrorCode unless the call can't fail.
/// </summary>
internal static unsafe partial class MidiNativeMethods
{
    /// <summary>
    /// Library name the loader resolves.
    /// </summary>
    private const string LibName = MidiNativeLibraryLoader.LogicalName;

    /// <summary>
    /// Gets the resolver in place before anything calls into native.
    /// </summary>
    static MidiNativeMethods()
    {
        MidiNativeLibraryLoader.EnsureRegistered();
    }

    #region Port Enumeration

    /// <summary>
    /// Input port names as a native array of UTF-8 strings.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_list_input_ports(
        out IntPtr outNames, out nuint outCount);

    /// <summary>
    /// Output port names as a native array of UTF-8 strings.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_list_output_ports(
        out IntPtr outNames, out nuint outCount);

    /// <summary>
    /// Frees a name array we got from the two calls above.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial void ownaudio_midi_v1_free_port_names(
        IntPtr names, nuint count);

    #endregion

    #region Input Port

    /// <summary>
    /// Opens a hardware input port by name.
    /// </summary>
    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ownaudio_midi_v1_input_port_open(
        string portName, out MidiInputPortHandle outHandle);

    /// <summary>
    /// Creates a virtual input port.
    /// </summary>
    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ownaudio_midi_v1_create_virtual_input(
        string name, out MidiInputPortHandle outHandle);

    /// <summary>
    /// Starts pumping short messages and SysEx into the two callbacks; userData
    /// comes back untouched on every call.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_input_port_start_with_sysex(
        MidiInputPortHandle handle,
        delegate* unmanaged<NativeMidiMessage, IntPtr, void> msgCallback,
        delegate* unmanaged<IntPtr, nuint, IntPtr, void> sysexCallback,
        IntPtr userData);

    /// <summary>
    /// Stops delivery and drops the connection.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_input_port_stop(
        MidiInputPortHandle handle);

    /// <summary>
    /// Destroys the port, called from the SafeHandle release.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial void ownaudio_midi_v1_input_port_destroy(IntPtr handle);

    #endregion

    #region Output Port

    /// <summary>
    /// Opens a hardware output port by name.
    /// </summary>
    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ownaudio_midi_v1_output_port_open(
        string portName, out MidiOutputPortHandle outHandle);

    /// <summary>
    /// Creates a virtual output port.
    /// </summary>
    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ownaudio_midi_v1_create_virtual_output(
        string name, out MidiOutputPortHandle outHandle);

    /// <summary>
    /// Sends one short message.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_output_port_send(
        MidiOutputPortHandle handle, NativeMidiMessage msg);

    /// <summary>
    /// Sends a raw SysEx blob.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_output_port_send_sysex(
        MidiOutputPortHandle handle, byte* data, nuint len);

    /// <summary>
    /// Destroys the port, called from the SafeHandle release.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial void ownaudio_midi_v1_output_port_destroy(IntPtr handle);

    #endregion

    #region Clock

    /// <summary>
    /// Creates a stopped clock at the given tempo.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_clock_create(
        double bpm, out MidiClockHandle outHandle);

    /// <summary>
    /// Retunes the clock on the fly.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_clock_set_bpm(
        MidiClockHandle handle, double bpm);

    /// <summary>
    /// Spins up the timing thread; the callback fires on every pulse with userData.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_clock_start(
        MidiClockHandle handle,
        delegate* unmanaged<IntPtr, void> pulseCallback,
        IntPtr userData);

    /// <summary>
    /// Stops the thread and joins it.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_clock_stop(MidiClockHandle handle);

    /// <summary>
    /// Destroys the clock, called from the SafeHandle release.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial void ownaudio_midi_v1_clock_destroy(IntPtr handle);

    #endregion

    #region File Parsing

    /// <summary>
    /// Parses an SMF out of a byte buffer.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_file_parse(
        byte* data, nuint len, out MidiFileHandle outHandle);

    /// <summary>
    /// SMF format word.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_file_get_format(
        MidiFileHandle handle, out ushort outFormat);

    /// <summary>
    /// Ticks-per-beat resolution.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_file_get_ticks_per_beat(
        MidiFileHandle handle, out ushort outTpb);

    /// <summary>
    /// Track count.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_file_get_track_count(
        MidiFileHandle handle, out nuint outCount);

    /// <summary>
    /// Event count of one track.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_file_get_event_count(
        MidiFileHandle handle, nuint trackIndex, out nuint outCount);

    /// <summary>
    /// One event out of a track.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_file_get_event(
        MidiFileHandle handle, nuint trackIndex, nuint eventIndex,
        out NativeMidiEvent outEvent);

    /// <summary>
    /// Destroys the parsed file, called from the SafeHandle release.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial void ownaudio_midi_v1_file_destroy(IntPtr handle);

    #endregion

    #region File Serialization

    /// <summary>
    /// New writer with the given format and resolution.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_writer_create(
        ushort format, ushort ticksPerBeat, out MidiWriterHandle outHandle);

    /// <summary>
    /// Opens a fresh empty track.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_writer_begin_track(MidiWriterHandle handle);

    /// <summary>
    /// Appends an event to the current track.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_writer_add_event(
        MidiWriterHandle handle, NativeMidiEvent evt);

    /// <summary>
    /// Bakes everything into a native SMF byte buffer.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial int ownaudio_midi_v1_writer_serialize(
        MidiWriterHandle handle, out IntPtr outData, out nuint outLen);

    /// <summary>
    /// Destroys the writer, called from the SafeHandle release.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial void ownaudio_midi_v1_writer_destroy(IntPtr handle);

    /// <summary>
    /// Frees the buffer serialize handed back.
    /// </summary>
    [LibraryImport(LibName)]
    internal static partial void ownaudio_midi_v1_free_bytes(IntPtr data, nuint len);

    #endregion
}
