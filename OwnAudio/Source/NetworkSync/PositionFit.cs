namespace OwnaudioNET.NetworkSync;

/// <summary>
/// Smooths a stepping song position into a line. The position moves in device blocks and
/// control ticks, so one reading is off by up to 15 ms; the average of a second and a half of
/// them is well under one.
/// The slope is not fitted, it is the known rate — a free slope on that little data is noisier
/// than the ppm it would find. A new rate starts the fit over.
/// </summary>
internal sealed class PositionFit
{
    public const double WindowSeconds = 1.5;

    /// <summary>
    /// A reading this far off the line was a hiccup (a late tick, a GC), not the clock.
    /// </summary>
    public const double OutlierSeconds = 0.020;

    private const int Capacity = 64;

    private readonly double[] _times     = new double[Capacity];
    private readonly double[] _positions = new double[Capacity];
    private int _count;
    private int _next;

    private double _rate = double.NaN;
    private double _anchorTime;
    private double _anchorPosition;

    public int Count => _count;

    public double Rate => _rate;

    public void Reset()
    {
        _count = 0;
        _next = 0;
        _rate = double.NaN;
    }

    /// <summary>
    /// One reading, with the rate the position was moving at (0 when stopped).
    /// </summary>
    public void Add(double time, double position, double rate)
    {
        if (rate != _rate)
        {
            _count = 0;
            _next = 0;
            _rate = rate;
        }

        _times[_next]     = time;
        _positions[_next] = position;
        _next = (_next + 1) % Capacity;
        if (_count < Capacity) _count++;

        _fit(time);
    }

    /// <summary>
    /// Where the line says the position is at the given time. False before the first reading.
    /// </summary>
    public bool TryPredict(double time, out double position)
    {
        if (_count == 0)
        {
            position = 0;
            return false;
        }

        position = _anchorPosition + (time - _anchorTime) * _rate;
        return true;
    }

    private void _fit(double newest)
    {
        double _from = newest - WindowSeconds;

        double _mean = _intercept(_from, newest, double.MaxValue);
        _anchorPosition = _intercept(_from, newest, OutlierSeconds, _mean);
        _anchorTime = newest;
    }

    /// <summary>
    /// Mean of the readings carried to the newest time along the rate; around a guess when
    /// one is given, the ones further from it than the limit left out.
    /// </summary>
    private double _intercept(double from, double newest, double limit, double guess = 0)
    {
        double _sum = 0;
        int _n = 0;

        for (int i = 0; i < _count; i++)
        {
            if (_times[i] < from) continue;

            double _at = _positions[i] + (newest - _times[i]) * _rate;
            if (limit != double.MaxValue && Math.Abs(_at - guess) > limit) continue;

            _sum += _at;
            _n++;
        }

        return _n > 0 ? _sum / _n : guess;
    }
}
