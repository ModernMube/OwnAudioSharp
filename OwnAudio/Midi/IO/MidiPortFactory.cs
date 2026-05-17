using System.Runtime.InteropServices;
using OwnAudio.Midi.IO.Platform;

namespace OwnAudio.Midi.IO;

/// <summary>
/// Cross-platform factory for enumerating and opening MIDI input and output ports.
/// Selects the platform-specific implementation at runtime.
/// </summary>
public static class MidiPortFactory
{
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
            return new LinuxMidiInputPort(portName, portName);
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
            return new LinuxMidiOutputPort(portName, portName);
        throw new PlatformNotSupportedException("MIDI is not supported on this platform.");
    }
}
