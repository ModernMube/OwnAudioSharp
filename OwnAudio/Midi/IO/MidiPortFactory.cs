using System.Runtime.InteropServices;
using OwnAudio.Midi.IO.Platform;

namespace OwnAudio.Midi.IO;

/// <summary>
/// Cross-platform factory for enumerating, opening, and creating virtual MIDI ports.
/// Also provides hot-plug change notification for platforms that support it.
/// Selects the platform-specific implementation at runtime.
/// </summary>
public static class MidiPortFactory
{
    /// <summary>
    /// FileSystemWatcher used on Linux to detect ALSA rawmidi device node changes in /dev/snd.
    /// </summary>
    private static FileSystemWatcher? _linuxWatcher;

    /// <summary>
    /// Guards against concurrent calls to <see cref="StartMonitoring"/> and <see cref="StopMonitoring"/>.
    /// </summary>
    private static readonly object _monitorLock = new();

    /// <summary>
    /// Raised when the set of available MIDI ports on this system has changed (device plugged or unplugged).
    /// On Linux this is driven by a <see cref="FileSystemWatcher"/> on /dev/snd.
    /// </summary>
    public static event Action? PortsChanged;

    /// <summary>
    /// Returns the names of all available MIDI input ports on the current platform.
    /// </summary>
    public static IReadOnlyList<string> GetInputPortNames()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return WindowsMidiInputPort.GetInputPortNames();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return MacOsMidiInputPort.GetInputPortNames();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return LinuxMidiInputPort.GetInputPortNames();
        return Array.Empty<string>();
    }

    /// <summary>
    /// Returns the names of all available MIDI output ports on the current platform.
    /// </summary>
    public static IReadOnlyList<string> GetOutputPortNames()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return WindowsMidiInputPort.GetOutputPortNames();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return MacOsMidiInputPort.GetOutputPortNames();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return LinuxMidiInputPort.GetOutputPortNames();
        return Array.Empty<string>();
    }

    /// <summary>
    /// Opens a MIDI input port by name and returns it ready for listening.
    /// Throws <see cref="ArgumentException"/> if the port name is not found.
    /// </summary>
    public static IMidiInputPort OpenInput(string portName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var names = WindowsMidiInputPort.GetInputPortNames();
            int idx = names.ToList().IndexOf(portName);
            if (idx < 0) throw new ArgumentException($"MIDI input port not found: '{portName}'");
            return new WindowsMidiInputPort(portName, idx);
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var names = MacOsMidiInputPort.GetInputPortNames();
            int idx = names.ToList().IndexOf(portName);
            if (idx < 0) throw new ArgumentException($"MIDI input port not found: '{portName}'");
            nint src = NativeMacOsMidi.MIDIGetSource((nint)idx);
            return new MacOsMidiInputPort(portName, src);
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var deviceMap = LinuxMidiInputPort.GetInputDeviceMap();
            if (!deviceMap.TryGetValue(portName, out var devicePath))
                throw new ArgumentException($"MIDI input port not found: '{portName}'");
            return new LinuxMidiInputPort(portName, devicePath);
        }
        throw new PlatformNotSupportedException("MIDI is not supported on this platform.");
    }

    /// <summary>
    /// Opens a MIDI output port by name and returns it ready for sending messages.
    /// Throws <see cref="ArgumentException"/> if the port name is not found.
    /// </summary>
    public static IMidiOutputPort OpenOutput(string portName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var names = WindowsMidiInputPort.GetOutputPortNames();
            int idx = names.ToList().IndexOf(portName);
            if (idx < 0) throw new ArgumentException($"MIDI output port not found: '{portName}'");
            return new WindowsMidiOutputPort(portName, idx);
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var names = MacOsMidiInputPort.GetOutputPortNames();
            int idx = names.ToList().IndexOf(portName);
            if (idx < 0) throw new ArgumentException($"MIDI output port not found: '{portName}'");
            nint dest = NativeMacOsMidi.MIDIGetDestination((nint)idx);
            return new MacOsMidiOutputPort(portName, dest);
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var deviceMap = LinuxMidiInputPort.GetOutputDeviceMap();
            if (!deviceMap.TryGetValue(portName, out var devicePath))
                throw new ArgumentException($"MIDI output port not found: '{portName}'");
            return new LinuxMidiOutputPort(portName, devicePath);
        }
        throw new PlatformNotSupportedException("MIDI is not supported on this platform.");
    }

    /// <summary>
    /// Creates a virtual MIDI input port with the given name.
    /// On macOS a CoreMIDI destination endpoint is created; on Linux an ALSA sequencer port is created.
    /// Throws <see cref="PlatformNotSupportedException"/> on Windows (WinMM does not support virtual ports).
    /// </summary>
    /// <param name="name">
    /// Display name for the virtual port as it will appear to other applications.
    /// </param>
    public static IMidiInputPort CreateVirtualInput(string name)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return new MacOsVirtualMidiInputPort(name);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return new LinuxVirtualMidiInputPort(name);
        throw new PlatformNotSupportedException("Virtual MIDI input ports are not supported on this platform.");
    }

    /// <summary>
    /// Creates a virtual MIDI output port with the given name.
    /// On macOS a CoreMIDI source endpoint is created; on Linux an ALSA sequencer port is created.
    /// Throws <see cref="PlatformNotSupportedException"/> on Windows (WinMM does not support virtual ports).
    /// </summary>
    /// <param name="name">
    /// Display name for the virtual port as it will appear to other applications.
    /// </param>
    public static IMidiOutputPort CreateVirtualOutput(string name)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return new MacOsVirtualMidiOutputPort(name);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return new LinuxVirtualMidiOutputPort(name);
        throw new PlatformNotSupportedException("Virtual MIDI output ports are not supported on this platform.");
    }

    /// <summary>
    /// Starts monitoring the system for MIDI port arrival and removal events.
    /// On Linux, watches /dev/snd for midi* device node changes.
    /// On macOS, CoreMIDI notification callbacks are set up automatically when ports are opened.
    /// Calling this method more than once before <see cref="StopMonitoring"/> is a no-op.
    /// </summary>
    public static void StartMonitoring()
    {
        lock (_monitorLock)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && _linuxWatcher is null)
            {
                if (Directory.Exists("/dev/snd"))
                {
                    _linuxWatcher = new FileSystemWatcher("/dev/snd", "midi*")
                    {
                        EnableRaisingEvents = true
                    };
                    _linuxWatcher.Created += OnLinuxDeviceChanged;
                    _linuxWatcher.Deleted += OnLinuxDeviceChanged;
                }
            }
        }
    }

    /// <summary>
    /// Stops monitoring for MIDI port changes and releases the associated watcher resources.
    /// </summary>
    public static void StopMonitoring()
    {
        lock (_monitorLock)
        {
            if (_linuxWatcher is not null)
            {
                _linuxWatcher.EnableRaisingEvents = false;
                _linuxWatcher.Created -= OnLinuxDeviceChanged;
                _linuxWatcher.Deleted -= OnLinuxDeviceChanged;
                _linuxWatcher.Dispose();
                _linuxWatcher = null;
            }
        }
    }

    /// <summary>
    /// Invoked by the <see cref="FileSystemWatcher"/> when a Linux MIDI device node is created or deleted.
    /// Raises <see cref="PortsChanged"/> on a thread-pool thread.
    /// </summary>
    /// <param name="sender">
    /// The <see cref="FileSystemWatcher"/> that detected the change.
    /// </param>
    /// <param name="e">
    /// Event arguments describing the file system change.
    /// </param>
    private static void OnLinuxDeviceChanged(object sender, FileSystemEventArgs e)
    {
        PortsChanged?.Invoke();
    }
}
