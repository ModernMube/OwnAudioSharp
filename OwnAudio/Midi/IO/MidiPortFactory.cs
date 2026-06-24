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
    /// Raised when the set of available MIDI input or output ports changes,
    /// for example when a device is plugged in or removed. The event is driven
    /// by background polling that must be enabled with
    /// <see cref="StartMonitoring"/>; until then it is never raised.
    /// </summary>
    public static event Action? PortsChanged;

    /// <summary>
    /// Synchronizes access to the monitoring timer and the last-seen port
    /// snapshots between callers and the polling callback.
    /// </summary>
    private static readonly object MonitorLock = new();

    /// <summary>
    /// Interval between hot-plug polling passes. Chosen to detect device
    /// changes promptly without measurable load.
    /// </summary>
    private static readonly TimeSpan MonitorInterval = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Background timer that periodically polls the available ports while
    /// monitoring is active; <c>null</c> when monitoring is stopped.
    /// </summary>
    private static System.Threading.Timer? _monitorTimer;

    /// <summary>
    /// Snapshot of the input port names seen on the previous polling pass, used
    /// to detect changes. <c>null</c> while monitoring is stopped.
    /// </summary>
    private static IReadOnlyList<string>? _lastInputNames;

    /// <summary>
    /// Snapshot of the output port names seen on the previous polling pass, used
    /// to detect changes. <c>null</c> while monitoring is stopped.
    /// </summary>
    private static IReadOnlyList<string>? _lastOutputNames;

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
    /// Begins monitoring the system for MIDI port arrival and removal. While
    /// active, the set of available input and output ports is polled in the
    /// background and <see cref="PortsChanged"/> is raised whenever it differs
    /// from the previous pass. Calling this while already monitoring is a no-op.
    /// </summary>
    public static void StartMonitoring()
    {
        lock (MonitorLock)
        {
            if (_monitorTimer is not null)
            {
                return;
            }

            _lastInputNames = GetInputPortNames();
            _lastOutputNames = GetOutputPortNames();
            _monitorTimer = new System.Threading.Timer(PollPorts, null, MonitorInterval, MonitorInterval);
        }
    }

    /// <summary>
    /// Stops background monitoring for MIDI port changes and releases the
    /// polling timer. Calling this while not monitoring is a no-op.
    /// </summary>
    public static void StopMonitoring()
    {
        lock (MonitorLock)
        {
            _monitorTimer?.Dispose();
            _monitorTimer = null;
            _lastInputNames = null;
            _lastOutputNames = null;
        }
    }

    /// <summary>
    /// Polling callback invoked by the monitoring timer. Compares the current
    /// input and output port names against the previous snapshot and raises
    /// <see cref="PortsChanged"/> when either set has changed. Transient native
    /// enumeration failures are ignored so the next pass can retry.
    /// </summary>
    /// <param name="state">
    /// Unused timer state; required by the <see cref="System.Threading.TimerCallback"/> signature.
    /// </param>
    private static void PollPorts(object? state)
    {
        IReadOnlyList<string> inputs;
        IReadOnlyList<string> outputs;
        try
        {
            inputs = GetInputPortNames();
            outputs = GetOutputPortNames();
        }
        catch
        {
            return;
        }

        bool changed;
        lock (MonitorLock)
        {
            if (_monitorTimer is null)
            {
                return;
            }

            changed = !PortNamesEqual(_lastInputNames, inputs)
                || !PortNamesEqual(_lastOutputNames, outputs);

            if (changed)
            {
                _lastInputNames = inputs;
                _lastOutputNames = outputs;
            }
        }

        if (changed)
        {
            PortsChanged?.Invoke();
        }
    }

    /// <summary>
    /// Returns true if both port-name lists are non-null and contain the same
    /// names in the same order. A null previous snapshot counts as not equal so
    /// the first pass after a state reset is treated as a change.
    /// </summary>
    /// <param name="previous">The previously recorded port names, or null.</param>
    /// <param name="current">The freshly enumerated port names.</param>
    private static bool PortNamesEqual(IReadOnlyList<string>? previous, IReadOnlyList<string> current)
    {
        if (previous is null || previous.Count != current.Count)
        {
            return false;
        }

        for (int i = 0; i < previous.Count; i++)
        {
            if (!string.Equals(previous[i], current[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
