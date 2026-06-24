using System.Runtime.InteropServices;
using OwnAudio.Midi.Internal;
using OwnAudio.Midi.Interop;
using OwnAudio.Midi.IO;

namespace OwnAudio.Midi.Clock;

/// <summary>
/// High-resolution MIDI clock that generates 24 PPQN timing pulses and optionally
/// forwards them to a MIDI output port via the standard 0xF8 Timing Clock message.
/// The timing thread runs in the native MIDI core; each pulse is delivered to this
/// instance through an unmanaged callback, which sends the 0xF8 message to the
/// configured output port. This keeps the clock compatible with any
/// <see cref="IMidiOutputPort"/>, including managed test doubles.
/// </summary>
public sealed class MidiClock : IDisposable
{
    /// <summary>
    /// Optional output port that receives Start, Stop, Continue, and Timing Clock messages.
    /// </summary>
    private readonly IMidiOutputPort? _outputPort;

    /// <summary>
    /// Native timing clock handle.
    /// </summary>
    private readonly MidiClockHandle _handle;

    /// <summary>
    /// Pins this instance so the unmanaged pulse callback can recover it;
    /// allocated while the clock is running.
    /// </summary>
    private GCHandle _selfPin;

    /// <summary>
    /// Indicates whether the clock is currently running.
    /// </summary>
    private volatile bool _isRunning;

    /// <summary>
    /// Guards against double-disposal.
    /// </summary>
    private volatile bool _disposed;

    /// <summary>
    /// Current tempo in beats per minute.
    /// </summary>
    private double _bpm;

    /// <summary>
    /// Gets or sets the tempo in beats per minute, clamped to the range [20, 300].
    /// </summary>
    public double Bpm
    {
        get => _bpm;
        set
        {
            _bpm = Math.Clamp(value, 20.0, 300.0);
            if (!_handle.IsInvalid)
            {
                MidiNativeMethods.ownaudio_midi_v1_clock_set_bpm(_handle, _bpm);
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the clock is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Initializes a new <see cref="MidiClock"/> with the specified tempo and optional output port.
    /// </summary>
    public MidiClock(double bpm = 120.0, IMidiOutputPort? outputPort = null)
    {
        _bpm = Math.Clamp(bpm, 20.0, 300.0);
        _outputPort = outputPort;

        int code = MidiNativeMethods.ownaudio_midi_v1_clock_create(_bpm, out _handle);
        MidiErrorCodeMapper.ThrowIfError(code, nameof(MidiClock));
    }

    /// <summary>
    /// Starts the clock thread and sends the MIDI Start message (0xFA) to the output port.
    /// </summary>
    public void Start() => StartInternal(0xFA);

    /// <summary>
    /// Resumes a stopped clock and sends the MIDI Continue message (0xFB) to the output port.
    /// </summary>
    public void Continue() => StartInternal(0xFB);

    /// <summary>
    /// Starts the native timing thread and sends the given transport message.
    /// </summary>
    /// <param name="transportMessage">
    /// The transport status byte to send after starting (0xFA Start or 0xFB Continue).
    /// </param>
    private void StartInternal(byte transportMessage)
    {
        if (_isRunning)
        {
            return;
        }

        _selfPin = GCHandle.Alloc(this);

        int code;
        unsafe
        {
            code = MidiNativeMethods.ownaudio_midi_v1_clock_start(
                _handle, &OnPulse, GCHandle.ToIntPtr(_selfPin));
        }

        if (code != (int)MidiErrorCode.Success)
        {
            _selfPin.Free();
            MidiErrorCodeMapper.ThrowIfError(code, nameof(Start));
        }

        _isRunning = true;
        _outputPort?.Send(new MidiMessage(transportMessage, 0, 0));
    }

    /// <summary>
    /// Stops the clock thread and sends the MIDI Stop message (0xFC) to the output port.
    /// </summary>
    public void Stop()
    {
        if (!_isRunning)
        {
            return;
        }

        MidiNativeMethods.ownaudio_midi_v1_clock_stop(_handle);
        if (_selfPin.IsAllocated)
        {
            _selfPin.Free();
        }
        _isRunning = false;

        _outputPort?.Send(new MidiMessage(0xFC, 0, 0));
    }

    /// <summary>
    /// Unmanaged callback invoked by the native clock for each timing pulse.
    /// Sends the 0xF8 Timing Clock message to the configured output port.
    /// </summary>
    /// <param name="userData">
    /// Pointer to the pinned <see cref="MidiClock"/> instance.
    /// </param>
    [UnmanagedCallersOnly]
    private static void OnPulse(IntPtr userData)
    {
        if (GCHandle.FromIntPtr(userData).Target is not MidiClock clock)
        {
            return;
        }
        clock._outputPort?.Send(new MidiMessage(0xF8, 0, 0));
    }

    /// <summary>
    /// Stops the clock if running and releases all native resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_isRunning)
        {
            Stop();
        }
        _handle.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Finalizer that ensures resources are released if <see cref="Dispose"/> was not called.
    /// </summary>
    ~MidiClock() => Dispose();
}
