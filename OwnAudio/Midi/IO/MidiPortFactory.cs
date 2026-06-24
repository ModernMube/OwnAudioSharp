using OwnAudio.Midi.Interop;
using OwnAudio.Midi.Internal;

namespace OwnAudio.Midi.IO;

/// <summary>
/// Cross-platform factory for enumerating, opening, and creating virtual MIDI
/// ports. All operations are routed to the native MIDI core (built on the
/// platform's WinMM, CoreMIDI or ALSA backend), so no platform branching is
/// required in managed code. The public surface is unchanged from earlier
/// versions for binary compatibility.
/// </summary>
public static class MidiPortFactory
{
    /// <summary>
    /// Raised when the set of available MIDI ports changes. Retained for binary
    /// compatibility; the native backend does not surface hot-plug events, so
    /// this event is never raised.
    /// </summary>
#pragma warning disable CS0067 // Event is a documented compatibility stub.
    public static event Action? PortsChanged;
#pragma warning restore CS0067

    /// <summary>
    /// Returns the names of all available MIDI input ports.
    /// </summary>
    public static IReadOnlyList<string> GetInputPortNames()
        => MidiNativeHelper.ListInputPortNames();

    /// <summary>
    /// Returns the names of all available MIDI output ports.
    /// </summary>
    public static IReadOnlyList<string> GetOutputPortNames()
        => MidiNativeHelper.ListOutputPortNames();

    /// <summary>
    /// Opens a MIDI input port by name and returns it ready for listening.
    /// Throws <see cref="ArgumentException"/> if the port name is not found.
    /// </summary>
    public static IMidiInputPort OpenInput(string portName)
    {
        int code = MidiNativeMethods.ownaudio_midi_v1_input_port_open(portName, out var handle);
        MidiErrorCodeMapper.ThrowIfError(code, nameof(OpenInput));
        return new RustMidiInputPort(portName, handle);
    }

    /// <summary>
    /// Opens a MIDI output port by name and returns it ready for sending messages.
    /// Throws <see cref="ArgumentException"/> if the port name is not found.
    /// </summary>
    public static IMidiOutputPort OpenOutput(string portName)
    {
        int code = MidiNativeMethods.ownaudio_midi_v1_output_port_open(portName, out var handle);
        MidiErrorCodeMapper.ThrowIfError(code, nameof(OpenOutput));
        return new RustMidiOutputPort(portName, handle);
    }

    /// <summary>
    /// Creates a virtual MIDI input port with the given name.
    /// Throws <see cref="PlatformNotSupportedException"/> on platforms without
    /// virtual port support (for example Windows under WinMM).
    /// </summary>
    /// <param name="name">
    /// Display name for the virtual port as it will appear to other applications.
    /// </param>
    public static IMidiInputPort CreateVirtualInput(string name)
    {
        int code = MidiNativeMethods.ownaudio_midi_v1_create_virtual_input(name, out var handle);
        MidiErrorCodeMapper.ThrowIfError(code, nameof(CreateVirtualInput));
        return new RustMidiInputPort(name, handle);
    }

    /// <summary>
    /// Creates a virtual MIDI output port with the given name.
    /// Throws <see cref="PlatformNotSupportedException"/> on platforms without
    /// virtual port support (for example Windows under WinMM).
    /// </summary>
    /// <param name="name">
    /// Display name for the virtual port as it will appear to other applications.
    /// </param>
    public static IMidiOutputPort CreateVirtualOutput(string name)
    {
        int code = MidiNativeMethods.ownaudio_midi_v1_create_virtual_output(name, out var handle);
        MidiErrorCodeMapper.ThrowIfError(code, nameof(CreateVirtualOutput));
        return new RustMidiOutputPort(name, handle);
    }

    /// <summary>
    /// Begins monitoring the system for MIDI port arrival and removal.
    /// Retained for binary compatibility; the native backend manages device
    /// changes internally, so this method performs no work.
    /// </summary>
    public static void StartMonitoring()
    {
    }

    /// <summary>
    /// Stops monitoring for MIDI port changes.
    /// Retained for binary compatibility; see <see cref="StartMonitoring"/>.
    /// </summary>
    public static void StopMonitoring()
    {
    }
}
