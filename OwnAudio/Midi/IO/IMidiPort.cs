namespace OwnAudio.Midi.IO;

/// <summary>
/// Handler for incoming SysEx. The data span holds the whole message, 0xF0 and
/// 0xF7 included, and only stays valid for the length of the call.
/// </summary>
public delegate void SysExReceivedHandler(ReadOnlySpan<byte> data);

/// <summary>
/// What every MIDI port can do — open, close, tell you its name.
/// </summary>
public interface IMidiPort : IDisposable
{
    /// <summary>
    /// Port name as the system reports it.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Open right now?
    /// </summary>
    bool IsOpen { get; }

    /// <summary>
    /// Opens the port.
    /// </summary>
    void Open();

    /// <summary>
    /// Closes it and drops the native resources.
    /// </summary>
    void Close();
}

/// <summary>
/// Input side — messages coming in from a hardware or virtual device.
/// </summary>
public interface IMidiInputPort : IMidiPort
{
    /// <summary>
    /// Fires on every short message from the device.
    /// </summary>
    event Action<MidiMessage>? MessageReceived;

    /// <summary>
    /// Fires once a whole SysEx (0xF0 ... 0xF7) is in. The span dies when the
    /// handler returns, so copy it if you need to keep it.
    /// </summary>
    event SysExReceivedHandler? SysExReceived;

    /// <summary>
    /// Starts listening.
    /// </summary>
    void Start();

    /// <summary>
    /// Stops listening.
    /// </summary>
    void Stop();
}

/// <summary>
/// Output side — messages going out to a hardware or virtual device.
/// </summary>
public interface IMidiOutputPort : IMidiPort
{
    /// <summary>
    /// Sends one short message.
    /// </summary>
    void Send(in MidiMessage message);

    /// <summary>
    /// Sends a SysEx blob.
    /// </summary>
    void SendSysEx(ReadOnlySpan<byte> data);
}
