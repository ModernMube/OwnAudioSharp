using OwnAudio.Midi.Interop;
using OwnAudio.Midi.IO;

namespace OwnAudio.Midi.Internal;

/// <summary>
/// <see cref="IMidiOutputPort"/> implementation backed by a native MIDI output
/// port. The same class serves hardware and virtual ports; the distinction is
/// captured entirely by the native handle.
/// </summary>
internal sealed class RustMidiOutputPort : IMidiOutputPort
{
    /// <summary>
    /// Native output port handle.
    /// </summary>
    private readonly MidiOutputPortHandle _handle;

    /// <summary>
    /// Guards against double-disposal.
    /// </summary>
    private bool _disposed;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public bool IsOpen => !_handle.IsInvalid;

    /// <summary>
    /// Wraps an already-opened native output port handle.
    /// </summary>
    /// <param name="name">
    /// Display name of the port.
    /// </param>
    /// <param name="handle">
    /// Native handle returned by the FFI open call.
    /// </param>
    internal RustMidiOutputPort(string name, MidiOutputPortHandle handle)
    {
        Name = name;
        _handle = handle;
    }

    /// <summary>
    /// No-op — the native port is already opened by the factory.
    /// </summary>
    public void Open()
    {
    }

    /// <inheritdoc />
    public void Close() => Dispose();

    /// <inheritdoc />
    public void Send(in MidiMessage message)
    {
        var native = new NativeMidiMessage
        {
            Status = message.Status,
            Data1 = message.Data1,
            Data2 = message.Data2,
            Pad = 0,
            TimestampUs = message.Timestamp
        };

        int code = MidiNativeMethods.ownaudio_midi_v1_output_port_send(_handle, native);
        MidiErrorCodeMapper.ThrowIfError(code, nameof(Send));
    }

    /// <inheritdoc />
    public void SendSysEx(ReadOnlySpan<byte> data)
    {
        int code;
        unsafe
        {
            fixed (byte* ptr = data)
            {
                code = MidiNativeMethods.ownaudio_midi_v1_output_port_send_sysex(
                    _handle, ptr, (nuint)data.Length);
            }
        }
        MidiErrorCodeMapper.ThrowIfError(code, nameof(SendSysEx));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _handle.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Finalizer that releases the native handle if <see cref="Dispose"/> was not called.
    /// </summary>
    ~RustMidiOutputPort() => Dispose();
}
