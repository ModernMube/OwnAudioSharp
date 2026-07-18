using System.Runtime.InteropServices;
using OwnAudio.Midi.Interop;
using OwnAudio.Midi.IO;

namespace OwnAudio.Midi.Internal;

/// <summary>
/// Input port on top of the native backend. Hardware and virtual ports both land
/// here, only the handle differs. Messages arrive on the backend thread.
/// </summary>
internal sealed class RustMidiInputPort : IMidiInputPort
{
    /// <summary>
    /// The native port.
    /// </summary>
    private readonly MidiInputPortHandle _handle;

    /// <summary>
    /// Pin so the unmanaged callbacks can find us again. Alive between Start and Stop.
    /// </summary>
    private GCHandle _selfPin;

    /// <summary>
    /// Double-dispose guard.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Are we listening?
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
    /// Takes over an already opened native handle.
    /// </summary>
    internal RustMidiInputPort(string name, MidiInputPortHandle handle)
    {
        Name = name;
        _handle = handle;
    }

    /// <summary>
    /// Nothing to do, the factory opened it already.
    /// </summary>
    public void Open() { }

    /// <inheritdoc />
    public void Start()
    {
        if (_started) return;

        _selfPin = GCHandle.Alloc(this);

        int code;
        unsafe
        {
            code = MidiNativeMethods.ownaudio_midi_v1_input_port_start_with_sysex(
                _handle, &_onNativeMidiMessage, &_onNativeSysEx, GCHandle.ToIntPtr(_selfPin));
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
        if (!_started) return;

        MidiNativeMethods.ownaudio_midi_v1_input_port_stop(_handle);
        if (_selfPin.IsAllocated) { _selfPin.Free(); }
        _started = false;
    }

    /// <inheritdoc />
    public void Close() => Dispose();

    /// <summary>
    /// Short message callback from the backend thread; userData is the pinned port.
    /// </summary>
    [UnmanagedCallersOnly]
    private static void _onNativeMidiMessage(NativeMidiMessage msg, IntPtr userData)
    {
        if (GCHandle.FromIntPtr(userData).Target is not RustMidiInputPort _port) return;
        _port.MessageReceived?.Invoke(new MidiMessage(msg.Status, msg.Data1, msg.Data2, msg.TimestampUs));
    }

    /// <summary>
    /// Full SysEx blob callback. The bytes only live for the length of this call,
    /// userData is the pinned port.
    /// </summary>
    [UnmanagedCallersOnly]
    private static unsafe void _onNativeSysEx(IntPtr data, nuint len, IntPtr userData)
    {
        if (GCHandle.FromIntPtr(userData).Target is not RustMidiInputPort _port) return;
        _port.SysExReceived?.Invoke(new ReadOnlySpan<byte>((void*)data, (int)len));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _handle.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Last resort if nobody called Dispose.
    /// </summary>
    ~RustMidiInputPort() => Dispose();
}
