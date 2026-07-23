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
    /// Topology fingerprint from the previous pass. No value means we haven't
    /// taken a baseline yet (fresh start), so the first tick won't spuriously fire.
    /// </summary>
    private static ulong? _lastFingerprint;

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

            _lastFingerprint = _tryReadFingerprint(out ulong _fp) ? _fp : null;
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
            _lastFingerprint = null;
        }
    }

    /// <summary>
    /// Timer tick. Reads the cheap native fingerprint and fires PortsChanged only
    /// when it moved — no name enumeration, no string allocation on a quiet tick.
    /// A failed read is swallowed on purpose; this runs on a pool thread and the
    /// next pass can just try again.
    /// </summary>
    private static void _pollPorts(object? state)
    {
        if (!_tryReadFingerprint(out ulong _fingerprint)) return;

        bool _changed;
        lock (_monitorLock)
        {
            if (_monitorTimer is null) return;

            _changed = _lastFingerprint is ulong _prev && _prev != _fingerprint;
            _lastFingerprint = _fingerprint;
        }

        if (_changed) { PortsChanged?.Invoke(); }
    }

    /// <summary>
    /// Pulls the native port-topology fingerprint. Returns false on any native
    /// error so the caller can just skip this tick.
    /// </summary>
    private static bool _tryReadFingerprint(out ulong fingerprint)
    {
        try
        {
            int code = MidiNativeMethods.ownaudio_midi_v1_port_fingerprint(out _, out _, out fingerprint);
            return code == 0;
        }
        catch
        {
            fingerprint = 0;
            return false;
        }
    }
}
