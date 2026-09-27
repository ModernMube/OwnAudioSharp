namespace OwnaudioNET.NetworkSync;

/// <summary>
/// Where the server's monotonic clock stands against ours, NTP style. Only the few fastest
/// round trips of the recent ones count: a slow trip was queued somewhere, and queueing is
/// never symmetric, so its offset is off by half the wait.
/// </summary>
internal sealed class ClockOffsetEstimator
{
    /// <summary>
    /// 10 s of pings at 2 Hz. Longer and the ppm difference of the two crystals starts to
    /// show in the older samples.
    /// </summary>
    public const int Window = 20;

    private const int Best = 3;

    private readonly double[] _offsets = new double[Window];
    private readonly double[] _delays  = new double[Window];
    private readonly bool[]   _picked  = new bool[Window];
    private int _count;
    private int _next;

    /// <summary>
    /// Server time minus local time, seconds.
    /// </summary>
    public double Offset { get; private set; }

    /// <summary>
    /// Fastest round trip in the window, the server's own turnaround left out.
    /// </summary>
    public double RoundTrip { get; private set; }

    /// <summary>
    /// How much slower the average trip is than the fastest one. Wifi shows here.
    /// </summary>
    public double Jitter { get; private set; }

    public bool IsValid => _count > 0;

    /// <summary>
    /// One ping/pong: when we sent, when the server got it and answered, when it came back.
    /// </summary>
    public void Add(double clientSent, double serverReceived, double serverSent, double clientReceived)
    {
        double _delay  = Math.Max(0.0, (clientReceived - clientSent) - (serverSent - serverReceived));
        double _offset = ((serverReceived - clientSent) + (serverSent - clientReceived)) * 0.5;

        _offsets[_next] = _offset;
        _delays[_next]  = _delay;
        _next = (_next + 1) % Window;
        if (_count < Window) _count++;

        _refresh();
    }

    public void Reset()
    {
        _count = 0;
        _next = 0;
        Offset = 0;
        RoundTrip = 0;
        Jitter = 0;
    }

    public double ToServer(double localSeconds) => localSeconds + Offset;

    private void _refresh()
    {
        Array.Clear(_picked);

        int _take = Math.Min(Best, _count);
        double _sum = 0;
        double _fastest = double.MaxValue;

        for (int k = 0; k < _take; k++)
        {
            int _min = -1;
            for (int i = 0; i < _count; i++)
            {
                if (_picked[i]) continue;
                if (_min < 0 || _delays[i] < _delays[_min]) _min = i;
            }

            _picked[_min] = true;
            _sum += _offsets[_min];
            if (_delays[_min] < _fastest) _fastest = _delays[_min];
        }

        double _all = 0;
        for (int i = 0; i < _count; i++) _all += _delays[i];

        Offset    = _sum / _take;
        RoundTrip = _fastest;
        Jitter    = _all / _count - _fastest;
    }
}
