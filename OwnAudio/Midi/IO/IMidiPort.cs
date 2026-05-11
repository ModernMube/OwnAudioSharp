namespace OwnAudio.Midi.IO;

public interface IMidiPort : IDisposable
{
    string Name { get; }
    bool IsOpen { get; }

    void Open();
    void Close();
}

public interface IMidiInputPort : IMidiPort
{
    event Action<MidiMessage>? MessageReceived;
    void Start();
    void Stop();
}

public interface IMidiOutputPort : IMidiPort
{
    void Send(in MidiMessage message);
    void SendSysEx(ReadOnlySpan<byte> data);
}
