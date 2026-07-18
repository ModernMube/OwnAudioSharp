using OwnAudio.Midi.Interop;
using OwnAudio.Midi.Internal;

namespace OwnAudio.Midi.IO;

/// <summary>
/// Everything port related: list them, open them, make virtual ones. It all goes
/// down to the native core (WinMM / CoreMIDI / ALSA), so there is no platform
/// branching up here.
/// </summary>
public static class MidiPortFactory
{
    /// <summary>
    /// Fires when a device shows up or disappears. Only ever fires while
    /// StartMonitoring is active — the polling is what drives it.
    /// </summary>
    public static event Action? PortsChanged;

    /// <summary>
    /// Guards the timer and the two snapshots against the polling callback.
    /// </summary>
    private static readonly object _monitorLock = new object();

    /// <summary>
    /// Hot-plug poll rate. Fast enough to feel instant, slow enough to not matter.
    /// </summary>
    private static readonly TimeSpan _monitorInterval = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// The polling timer, null while we're not monitoring.
    /// </summary>
    private static System.Threading.Timer? _monitorTimer;

    /// <summary>
    /// Input names from the previous pass; null while stopped.
    /// </summary>
    private static IReadOnlyList<string>? _lastInputNames;

    /// <summary>
    /// Output names from the previous pass; null while stopped.
    /// </summary>
    private static IReadOnlyList<string>? _lastOutputNames;

    /// <summary>
    /// Every input port name we can see.
    /// </summary>
    public static IReadOnlyList<string> GetInputPortNames() => MidiNativeHelper.ListInputPortNames();

    /// <summary>
    /// Every output port name we can see.
    /// </summary>
    public static IReadOnlyList<string> GetOutputPortNames() => MidiNativeHelper.ListOutputPortNames();

    /// <summary>
    /// Opens an input port by name, ready to Start(). Unknown name gives ArgumentException.
    /// </summary>
    public static IMidiInputPort OpenInput(string portName)
    {
        int code = MidiNativeMethods.ownaudio_midi_v1_input_port_open(portName, out var handle);
        MidiErrorCodeMapper.ThrowIfError(code, nameof(OpenInput));
        return new RustMidiInputPort(portName, handle);
    }

    /// <summary>
    /// Opens an output port by name. Unknown name gives ArgumentException.
    /// </summary>
    public static IMidiOutputPort OpenOutput(string portName)
    {
        int code = MidiNativeMethods.ownaudio_midi_v1_output_port_open(portName, out var handle);
        MidiErrorCodeMapper.ThrowIfError(code, nameof(OpenOutput));
        return new RustMidiOutputPort(portName, handle);
    }

    /// <summary>
    /// Publishes a virtual input port under the given name, which is what other
    /// apps will see. Throws PlatformNotSupportedException where there is no
    /// virtual port support (Windows / WinMM).
    /// </summary>
    public static IMidiInputPort CreateVirtualInput(string name)
    {
        int code = MidiNativeMethods.ownaudio_midi_v1_create_virtual_input(name, out var handle);
        MidiErrorCodeMapper.ThrowIfError(code, nameof(CreateVirtualInput));
        return new RustMidiInputPort(name, handle);
    }

    /// <summary>
    /// Same for an output port; the name is what other apps will see.
    /// </summary>
    public static IMidiOutputPort CreateVirtualOutput(string name)
    {
        int code = MidiNativeMethods.ownaudio_midi_v1_create_virtual_output(name, out var handle);
        MidiErrorCodeMapper.ThrowIfError(code, nameof(CreateVirtualOutput));
        return new RustMidiOutputPort(name, handle);
    }

    /// <summary>
    /// Starts watching for devices coming and going. Calling it twice is a no-op.
    /// </summary>
    public static void StartMonitoring()
    {
        lock (_monitorLock)
        {
            if (_monitorTimer is not null) return;

            _lastInputNames = GetInputPortNames();
            _lastOutputNames = GetOutputPortNames();
            _monitorTimer = new System.Threading.Timer(_pollPorts, null, _monitorInterval, _monitorInterval);
        }
    }

    /// <summary>
    /// Stops watching and drops the timer. No-op if we weren't watching.
    /// </summary>
    public static void StopMonitoring()
    {
        lock (_monitorLock)
        {
            _monitorTimer?.Dispose();
            _monitorTimer = null;
            _lastInputNames = null;
            _lastOutputNames = null;
        }
    }

    /// <summary>
    /// Timer tick. Re-enumerates and fires PortsChanged if anything moved. A
    /// failed enumeration is swallowed on purpose — this runs on a pool thread
    /// and the next pass can just try again.
    /// </summary>
    private static void _pollPorts(object? state)
    {
        IReadOnlyList<string> _inputs;
        IReadOnlyList<string> _outputs;
        try
        {
            _inputs = GetInputPortNames();
            _outputs = GetOutputPortNames();
        }
        catch
        {
            return;
        }

        bool _changed;
        lock (_monitorLock)
        {
            if (_monitorTimer is null) return;

            _changed = !_portNamesEqual(_lastInputNames, _inputs) || !_portNamesEqual(_lastOutputNames, _outputs);
            if (_changed)
            {
                _lastInputNames = _inputs;
                _lastOutputNames = _outputs;
            }
        }

        if (_changed) { PortsChanged?.Invoke(); }
    }

    /// <summary>
    /// Same names in the same order? A null previous snapshot counts as changed,
    /// so the first pass after a reset always reports.
    /// </summary>
    private static bool _portNamesEqual(IReadOnlyList<string>? previous, IReadOnlyList<string> current)
    {
        if (previous is null || previous.Count != current.Count) return false;

        for (int i = 0; i < previous.Count; i++)
        {
            if (!string.Equals(previous[i], current[i], StringComparison.Ordinal)) return false;
        }

        return true;
    }
}
