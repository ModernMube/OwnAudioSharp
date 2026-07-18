using OwnAudio.Midi.Interop;
using OwnAudio.Midi.IO;

namespace OwnAudio.Midi.Internal;

/// <summary>
/// Output port on top of the native backend. Hardware and virtual ports both
/// land here, only the handle differs.
/// </summary>
internal sealed class RustMidiOutputPort : IMidiOutputPort
{
    /// <summary>
    /// The native port.
    /// </summary>
    private readonly MidiOutputPortHandle _handle;

    /// <summary>
    /// Double-dispose guard.
    /// </summary>
    private bool _disposed;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public bool IsOpen => !_handle.IsInvalid;

    /// <summary>
    /// Takes over an already opened native handle.
    /// </summary>
    internal RustMidiOutputPort(string name, MidiOutputPortHandle handle)
    {
        Name = name;
        _handle = handle;
    }

    /// <summary>
    /// Nothing to do, the factory opened it already.
    /// </summary>
    public void Open() { }

    /// <inheritdoc />
    public void Close() => Dispose();

    /// <inheritdoc />
    public void Send(in MidiMessage message)
    {
        var _native = new NativeMidiMessage
        {
            Status = message.Status,
            Data1 = message.Data1,
            Data2 = message.Data2,
            Pad = 0,
            TimestampUs = message.Timestamp
        };

        int code = MidiNativeMethods.ownaudio_midi_v1_output_port_send(_handle, _native);
        MidiErrorCodeMapper.ThrowIfError(code, nameof(Send));
    }

    /// <inheritdoc />
    public unsafe void SendSysEx(ReadOnlySpan<byte> data)
    {
        int code;
        fixed (byte* ptr = data)
        {
            code = MidiNativeMethods.ownaudio_midi_v1_output_port_send_sysex(_handle, ptr, (nuint)data.Length);
        }
        MidiErrorCodeMapper.ThrowIfError(code, nameof(SendSysEx));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _handle.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Last resort if nobody called Dispose.
    /// </summary>
    ~RustMidiOutputPort() => Dispose();
}
