using OwnAudio.Midi.IO.Platform;

namespace OwnAudio.Midi.IO;

/// <summary>
/// Cross-platform factory for enumerating and opening MIDI input and output ports.
/// Selects the platform-specific implementation at compile time.
/// </summary>
public static class MidiPortFactory
{
    /// <summary>
    /// Returns the names of all available MIDI input ports on the current platform.
    /// </summary>
    public static IReadOnlyList<string> GetInputPortNames()
    {
#if WINDOWS
        return WindowsMidiInputPort.GetInputPortNames();
#elif MACOS
        return MacOsMidiInputPort.GetInputPortNames();
#elif LINUX
        return LinuxMidiInputPort.GetInputPortNames();
#else
        return Array.Empty<string>();
#endif
    }

    /// <summary>
    /// Returns the names of all available MIDI output ports on the current platform.
    /// </summary>
    public static IReadOnlyList<string> GetOutputPortNames()
    {
#if WINDOWS
        return WindowsMidiInputPort.GetOutputPortNames();
#elif MACOS
        return MacOsMidiInputPort.GetOutputPortNames();
#elif LINUX
        return LinuxMidiInputPort.GetOutputPortNames();
#else
        return Array.Empty<string>();
#endif
    }

    /// <summary>
    /// Opens a MIDI input port by name and returns it ready for listening.
    /// Throws <see cref="ArgumentException"/> if the port name is not found.
    /// </summary>
    public static IMidiInputPort OpenInput(string portName)
    {
#if WINDOWS
        var names = WindowsMidiInputPort.GetInputPortNames();
        int idx = names.ToList().IndexOf(portName);
        if (idx < 0) throw new ArgumentException($"MIDI input port not found: '{portName}'");
        return new WindowsMidiInputPort(portName, idx);
#elif MACOS
        var names = MacOsMidiInputPort.GetInputPortNames();
        int idx = names.ToList().IndexOf(portName);
        if (idx < 0) throw new ArgumentException($"MIDI input port not found: '{portName}'");
        nint src = NativeMacOsMidi.MIDIGetSource((nint)idx);
        return new MacOsMidiInputPort(portName, src);
#elif LINUX
        return new LinuxMidiInputPort(portName, portName);
#else
        throw new PlatformNotSupportedException("MIDI is not supported on this platform.");
#endif
    }

    /// <summary>
    /// Opens a MIDI output port by name and returns it ready for sending messages.
    /// Throws <see cref="ArgumentException"/> if the port name is not found.
    /// </summary>
    public static IMidiOutputPort OpenOutput(string portName)
    {
#if WINDOWS
        var names = WindowsMidiInputPort.GetOutputPortNames();
        int idx = names.ToList().IndexOf(portName);
        if (idx < 0) throw new ArgumentException($"MIDI output port not found: '{portName}'");
        return new WindowsMidiOutputPort(portName, idx);
#elif MACOS
        var names = MacOsMidiInputPort.GetOutputPortNames();
        int idx = names.ToList().IndexOf(portName);
        if (idx < 0) throw new ArgumentException($"MIDI output port not found: '{portName}'");
        nint dest = NativeMacOsMidi.MIDIGetDestination((nint)idx);
        return new MacOsMidiOutputPort(portName, dest);
#elif LINUX
        return new LinuxMidiOutputPort(portName, portName);
#else
        throw new PlatformNotSupportedException("MIDI is not supported on this platform.");
#endif
    }
}
