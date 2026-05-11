namespace OwnAudio.Midi.IO;

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
