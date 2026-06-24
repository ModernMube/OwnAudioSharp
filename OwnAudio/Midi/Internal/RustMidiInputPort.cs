using System.Runtime.InteropServices;
using OwnAudio.Midi.Interop;
using OwnAudio.Midi.IO;

namespace OwnAudio.Midi.Internal;

/// <summary>
/// <see cref="IMidiInputPort"/> implementation backed by a native MIDI input
/// port. The same class serves hardware and virtual ports; the distinction is
/// captured entirely by the native handle. Incoming messages are delivered from
/// the backend thread through unmanaged callbacks.
/// </summary>
internal sealed class RustMidiInputPort : IMidiInputPort
{
    /// <summary>
    /// Native input port handle.
    /// </summary>
    private readonly MidiInputPortHandle _handle;

    /// <summary>
    /// Pins this instance so the unmanaged callbacks can recover it from the user
    /// data pointer; allocated on <see cref="Start"/> and freed on <see cref="Stop"/>.
    /// </summary>
    private GCHandle _selfPin;

    /// <summary>
    /// Guards against double-disposal.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Indicates whether listening is currently active.
    /// </summary>
    private bool _started;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public bool IsOpen => !_handle.IsInvalid;

    /// <inheritdoc />
    public event Action<MidiMessage>? MessageReceived;

    /// <inheritdoc />
    public event SysExReceivedHandler? SysExReceived;

    /// <summary>
    /// Wraps an already-opened native input port handle.
    /// </summary>
    /// <param name="name">
    /// Display name of the port.
    /// </param>
    /// <param name="handle">
    /// Native handle returned by the FFI open call.
    /// </param>
    internal RustMidiInputPort(string name, MidiInputPortHandle handle)
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
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _selfPin = GCHandle.Alloc(this);

        int code;
        unsafe
        {
            code = MidiNativeMethods.ownaudio_midi_v1_input_port_start_with_sysex(
                _handle,
                &OnNativeMidiMessage,
                &OnNativeSysEx,
                GCHandle.ToIntPtr(_selfPin));
        }

        if (code != (int)MidiErrorCode.Success)
        {
            _selfPin.Free();
            MidiErrorCodeMapper.ThrowIfError(code, nameof(Start));
        }

        _started = true;
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        MidiNativeMethods.ownaudio_midi_v1_input_port_stop(_handle);
        if (_selfPin.IsAllocated)
        {
            _selfPin.Free();
        }
        _started = false;
    }

    /// <inheritdoc />
    public void Close() => Dispose();

    /// <summary>
    /// Unmanaged callback invoked by the native layer for each short MIDI message.
    /// </summary>
    /// <param name="msg">
    /// The received message with a microsecond timestamp.
    /// </param>
    /// <param name="userData">
    /// Pointer to the pinned <see cref="RustMidiInputPort"/> instance.
    /// </param>
    [UnmanagedCallersOnly]
    private static void OnNativeMidiMessage(NativeMidiMessage msg, IntPtr userData)
    {
        if (GCHandle.FromIntPtr(userData).Target is not RustMidiInputPort port)
        {
            return;
        }
        port.MessageReceived?.Invoke(new MidiMessage(msg.Status, msg.Data1, msg.Data2, msg.TimestampUs));
    }

    /// <summary>
    /// Unmanaged callback invoked by the native layer for each complete SysEx message.
    /// </summary>
    /// <param name="data">
    /// Pointer to the SysEx bytes, valid only for the duration of the call.
    /// </param>
    /// <param name="len">
    /// Number of SysEx bytes.
    /// </param>
    /// <param name="userData">
    /// Pointer to the pinned <see cref="RustMidiInputPort"/> instance.
    /// </param>
    [UnmanagedCallersOnly]
    private static unsafe void OnNativeSysEx(IntPtr data, nuint len, IntPtr userData)
    {
        if (GCHandle.FromIntPtr(userData).Target is not RustMidiInputPort port)
        {
            return;
        }
        var span = new ReadOnlySpan<byte>((void*)data, (int)len);
        port.SysExReceived?.Invoke(span);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        Stop();
        _handle.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Finalizer that releases the native handle if <see cref="Dispose"/> was not called.
    /// </summary>
    ~RustMidiInputPort() => Dispose();
}
