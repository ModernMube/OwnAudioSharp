using System.Runtime.InteropServices;
using OwnAudio.Midi.Internal;
using OwnAudio.Midi.Interop;
using OwnAudio.Midi.IO;

namespace OwnAudio.Midi.Clock;

/// <summary>
/// 24 PPQN clock ticking on the native timing thread. Every pulse comes back
/// through an unmanaged callback and goes out as 0xF8, so any IMidiOutputPort
/// works here — test doubles included.
/// </summary>
public sealed class MidiClock : IDisposable
{
    /// <summary>
    /// Gets Start / Stop / Continue and the pulses, if we were given one.
    /// </summary>
    private readonly IMidiOutputPort? _outputPort;

    /// <summary>
    /// The native clock.
    /// </summary>
    private readonly MidiClockHandle _handle;

    /// <summary>
    /// Pin so the pulse callback can find us; alive while we run.
    /// </summary>
    private GCHandle _selfPin;

    /// <summary>
    /// Running or not.
    /// </summary>
    private volatile bool _isRunning;

    /// <summary>
    /// Double-dispose guard.
    /// </summary>
    private volatile bool _disposed;

    /// <summary>
    /// Tempo backing field.
    /// </summary>
    private double _bpm;

    /// <summary>
    /// Tempo in BPM, clamped to 20..300. Setting it retunes a running clock too.
    /// </summary>
    public double Bpm
    {
        get => _bpm;
        set
        {
            _bpm = Math.Clamp(value, 20.0, 300.0);
            if (!_handle.IsInvalid) { MidiNativeMethods.ownaudio_midi_v1_clock_set_bpm(_handle, _bpm); }
        }
    }

    /// <summary>
    /// True while the timing thread is up.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Creates a stopped clock. The output port is optional.
    /// </summary>
    public MidiClock(double bpm = 120.0, IMidiOutputPort? outputPort = null)
    {
        _bpm = Math.Clamp(bpm, 20.0, 300.0);
        _outputPort = outputPort;

        int code = MidiNativeMethods.ownaudio_midi_v1_clock_create(_bpm, out _handle);
        MidiErrorCodeMapper.ThrowIfError(code, nameof(MidiClock));
    }

    /// <summary>
    /// Starts from the top and sends Start (0xFA).
    /// </summary>
    public void Start() => _startInternal(0xFA);

    /// <summary>
    /// Picks up where we stopped and sends Continue (0xFB).
    /// </summary>
    public void Continue() => _startInternal(0xFB);

    /// <summary>
    /// Spins up the native thread, then sends the transport byte (0xFA or 0xFB).
    /// </summary>
    private void _startInternal(byte transportMessage)
    {
        if (_isRunning) return;

        _selfPin = GCHandle.Alloc(this);

        int code;
        unsafe
        {
            code = MidiNativeMethods.ownaudio_midi_v1_clock_start(_handle, &_onPulse, GCHandle.ToIntPtr(_selfPin));
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
    /// Kills the timing thread and sends Stop (0xFC).
    /// </summary>
    public void Stop()
    {
        if (!_isRunning) return;

        MidiNativeMethods.ownaudio_midi_v1_clock_stop(_handle);
        if (_selfPin.IsAllocated) { _selfPin.Free(); }
        _isRunning = false;

        _outputPort?.Send(new MidiMessage(0xFC, 0, 0));
    }

    /// <summary>
    /// Pulse callback off the native thread; userData is the pinned clock.
    /// </summary>
    [UnmanagedCallersOnly]
    private static void _onPulse(IntPtr userData)
    {
        if (GCHandle.FromIntPtr(userData).Target is not MidiClock _clock) return;
        _clock._outputPort?.Send(new MidiMessage(0xF8, 0, 0));
    }

    /// <summary>
    /// Stops if needed, then lets the native clock go.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_isRunning) Stop();
        _handle.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Last resort if nobody called Dispose.
    /// </summary>
    ~MidiClock() => Dispose();
}
