namespace OwnAudio.Midi.IO;

/// <summary>
/// Delegate for receiving System Exclusive (SysEx) MIDI messages.
/// The <paramref name="data"/> span is only valid for the duration of the callback.
/// </summary>
/// <param name="data">
/// The complete SysEx byte sequence including the leading 0xF0 and trailing 0xF7 bytes.
/// </param>
public delegate void SysExReceivedHandler(ReadOnlySpan<byte> data);

/// <summary>
/// Base interface for all MIDI ports providing open/close lifecycle management.
/// </summary>
public interface IMidiPort : IDisposable
{
    /// <summary>
    /// Gets the display name of the MIDI port.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets a value indicating whether the port is currently open.
    /// </summary>
    bool IsOpen { get; }

    /// <summary>
    /// Opens the MIDI port for communication.
    /// </summary>
    void Open();

    /// <summary>
    /// Closes the MIDI port and releases its native resources.
    /// </summary>
    void Close();
}

/// <summary>
/// MIDI input port that receives messages from a hardware or virtual device.
/// </summary>
public interface IMidiInputPort : IMidiPort
{
    /// <summary>
    /// Raised when a MIDI message is received from the connected device.
    /// </summary>
    event Action<MidiMessage>? MessageReceived;

    /// <summary>
    /// Raised when a complete SysEx message (0xF0 ... 0xF7) has been received.
    /// The span passed to the handler is only valid for the duration of the callback invocation.
    /// </summary>
    event SysExReceivedHandler? SysExReceived;

    /// <summary>
    /// Starts listening for incoming MIDI messages.
    /// </summary>
    void Start();

    /// <summary>
    /// Stops listening for incoming MIDI messages.
    /// </summary>
    void Stop();
}

/// <summary>
/// MIDI output port that sends messages to a hardware or virtual device.
/// </summary>
public interface IMidiOutputPort : IMidiPort
{
    /// <summary>
    /// Sends a short MIDI message to the output device.
    /// </summary>
    void Send(in MidiMessage message);

    /// <summary>
    /// Sends a SysEx (System Exclusive) byte sequence to the output device.
    /// </summary>
    void SendSysEx(ReadOnlySpan<byte> data);
}
