using System.Collections.Generic;
using OwnAudio.Midi.IO;

namespace OwnAudio.MidiTest;

/// <summary>
/// In-memory fake MIDI output port used in unit tests that need to verify
/// what messages the system under test sends without requiring hardware.
/// </summary>
internal sealed class FakeMidiOutput : IMidiOutputPort
{
    /// <summary>
    /// All short MIDI messages that have been sent via <see cref="Send"/>,
    /// in the order they were received.
    /// </summary>
    public readonly List<MidiMessage> SentMessages = new();

    /// <summary>
    /// All SysEx byte arrays that have been sent via <see cref="SendSysEx"/>,
    /// in the order they were received.
    /// </summary>
    public readonly List<byte[]> SentSysEx = new();

    /// <summary>
    /// Gets the display name of this fake port.
    /// </summary>
    public string Name => "FakeOutput";

    /// <summary>
    /// Gets a value indicating whether the port is considered open.
    /// Always returns <see langword="true"/> for the fake implementation.
    /// </summary>
    public bool IsOpen => true;

    /// <summary>
    /// No-op open implementation — the fake port requires no system resources.
    /// </summary>
    public void Open() { }

    /// <summary>
    /// No-op close implementation — the fake port holds no system resources.
    /// </summary>
    public void Close() { }

    /// <summary>
    /// Records the given short MIDI message in <see cref="SentMessages"/>.
    /// </summary>
    /// <param name="message">
    /// The MIDI message to record.
    /// </param>
    public void Send(in MidiMessage message) => SentMessages.Add(message);

    /// <summary>
    /// Records a copy of the SysEx bytes in <see cref="SentSysEx"/>.
    /// The span is copied immediately so it remains accessible after the call returns.
    /// </summary>
    /// <param name="data">
    /// The complete SysEx byte sequence to record.
    /// </param>
    public void SendSysEx(System.ReadOnlySpan<byte> data) => SentSysEx.Add(data.ToArray());

    /// <summary>
    /// No-op dispose — the fake port holds no unmanaged resources.
    /// </summary>
    public void Dispose() { }
}
