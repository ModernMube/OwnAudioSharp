using OwnAudio.Midi.IO;

namespace OwnAudio.Midi.Clock;

/// <summary>
/// High-resolution MIDI clock that generates 24 PPQN timing pulses and optionally
/// forwards them to a MIDI output port via the standard 0xF8 Timing Clock message.
/// </summary>
public sealed class MidiClock : IDisposable
{
    /// <summary>
    /// Standard MIDI timing resolution: 24 pulses per quarter note.
    /// </summary>
    private const int PulsesPerQuarterNote = 24;

    /// <summary>
    /// Optional output port that receives Start, Stop, Continue, and Timing Clock messages.
    /// </summary>
    private readonly IMidiOutputPort? _outputPort;

    /// <summary>
    /// Background thread that drives the timing pulse loop.
    /// </summary>
    private Thread? _clockThread;

    /// <summary>
    /// Indicates whether the clock thread is currently running.
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
        set => _bpm = Math.Clamp(value, 20.0, 300.0);
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
    }

    /// <summary>
    /// Starts the clock thread and sends the MIDI Start message (0xFA) to the output port.
    /// </summary>
    public void Start()
    {
        if (_isRunning) return;
        _isRunning = true;
        _clockThread = new Thread(ClockThreadProc)
        {
            Name = "MidiClock",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };
        _clockThread.Start();

        _outputPort?.Send(new MidiMessage(0xFA, 0, 0));
    }

    /// <summary>
    /// Stops the clock thread and sends the MIDI Stop message (0xFC) to the output port.
    /// </summary>
    public void Stop()
    {
        _isRunning = false;
        _clockThread?.Join(TimeSpan.FromSeconds(1));
        _clockThread = null;

        _outputPort?.Send(new MidiMessage(0xFC, 0, 0));
    }

    /// <summary>
    /// Resumes a stopped clock and sends the MIDI Continue message (0xFB) to the output port.
    /// </summary>
    public void Continue()
    {
        if (_isRunning) return;
        _isRunning = true;
        _clockThread = new Thread(ClockThreadProc)
        {
            Name = "MidiClock",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };
        _clockThread.Start();

        _outputPort?.Send(new MidiMessage(0xFB, 0, 0));
    }

    /// <summary>
    /// Spin-loop that fires MIDI Timing Clock pulses (0xF8) at the interval derived from the current BPM.
    /// Runs at highest thread priority to minimize jitter.
    /// </summary>
    private void ClockThreadProc()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long ticksPerUs = System.Diagnostics.Stopwatch.Frequency / 1_000_000;
        long nextPulseTick = 0;

        while (_isRunning)
        {
            double intervalUs = (60_000_000.0 / _bpm) / PulsesPerQuarterNote;
            long ticksPerPulse = (long)(intervalUs * ticksPerUs);

            if (sw.ElapsedTicks >= nextPulseTick)
            {
                _outputPort?.Send(new MidiMessage(0xF8, 0, 0));
                nextPulseTick += ticksPerPulse;
            }

            Thread.SpinWait(10);
        }
    }

    /// <summary>
    /// Stops the clock if running and releases all managed resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_isRunning) Stop();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Finalizer that ensures resources are released if <see cref="Dispose"/> was not called.
    /// </summary>
    ~MidiClock() => Dispose();
}
