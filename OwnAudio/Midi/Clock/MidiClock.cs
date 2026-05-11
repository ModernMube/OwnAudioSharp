using OwnAudio.Midi.IO;

namespace OwnAudio.Midi.Clock;

public sealed class MidiClock : IDisposable
{
    private const int PulsesPerQuarterNote = 24;

    private readonly IMidiOutputPort? _outputPort;
    private Thread? _clockThread;
    private volatile bool _isRunning;
    private volatile bool _disposed;
    private double _bpm;

    public double Bpm
    {
        get => _bpm;
        set => _bpm = Math.Clamp(value, 20.0, 300.0);
    }

    public bool IsRunning => _isRunning;

    public MidiClock(double bpm = 120.0, IMidiOutputPort? outputPort = null)
    {
        _bpm = Math.Clamp(bpm, 20.0, 300.0);
        _outputPort = outputPort;
    }

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

        // MIDI Start message
        _outputPort?.Send(new MidiMessage(0xFA, 0, 0));
    }

    public void Stop()
    {
        _isRunning = false;
        _clockThread?.Join(TimeSpan.FromSeconds(1));
        _clockThread = null;

        // MIDI Stop message
        _outputPort?.Send(new MidiMessage(0xFC, 0, 0));
    }

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

        // MIDI Continue message
        _outputPort?.Send(new MidiMessage(0xFB, 0, 0));
    }

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
                // MIDI Timing Clock (0xF8)
                _outputPort?.Send(new MidiMessage(0xF8, 0, 0));
                nextPulseTick += ticksPerPulse;
            }

            Thread.SpinWait(10);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_isRunning) Stop();
        GC.SuppressFinalize(this);
    }

    ~MidiClock() => Dispose();
}
