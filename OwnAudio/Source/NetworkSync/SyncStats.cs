using System.Globalization;

namespace OwnaudioNET.NetworkSync;

/// <summary>
/// What a following client saw over the last minute: how far off it ran, how often the
/// controller trimmed or jumped, and the network under it. One log line a minute is how a
/// long session gets read back afterwards. Adding is a few sums, nothing allocates until the
/// line is written.
/// </summary>
internal sealed class SyncStats
{
    public const double IntervalSeconds = 60.0;

    private double _since = double.NaN;
    private int _readings;
    private double _sumAbs;
    private double _maxAbs;
    private int _within5;
    private int _trims;
    private int _seeks;

    public int Readings => _readings;
    public double MaxAbsSeconds => _maxAbs;
    public int Trims => _trims;
    public int Seeks => _seeks;

    public void Add(double now, double error)
    {
        if (double.IsNaN(_since)) _since = now;

        double gap = Math.Abs(error);
        _readings++;
        _sumAbs += gap;
        if (gap > _maxAbs) _maxAbs = gap;
        if (gap <= 0.005) _within5++;
    }

    public void Trimmed() => _trims++;

    public void Jumped() => _seeks++;

    public bool Due(double now) => !double.IsNaN(_since) && now - _since >= IntervalSeconds;

    /// <summary>
    /// The line for the log, and a fresh start. Jitter and round trip are the estimator's at
    /// the moment of writing.
    /// </summary>
    public string Take(double now, double jitter, double roundTrip)
    {
        var c = CultureInfo.InvariantCulture;
        double span = double.IsNaN(_since) ? 0 : now - _since;
        double avg = _readings > 0 ? _sumAbs / _readings : 0;
        double share = _readings > 0 ? 100.0 * _within5 / _readings : 0;

        string line = string.Format(c,
            "[NetSync] {0:F0}s: error avg {1:F1} ms, max {2:F1} ms, {3:F0}% within 5 ms, trims {4}, seeks {5}, jitter {6:F1} ms, rtt {7:F1} ms",
            span, avg * 1000, _maxAbs * 1000, share, _trims, _seeks, jitter * 1000, roundTrip * 1000);

        Reset();
        return line;
    }

    public void Reset()
    {
        _since = double.NaN;
        _readings = 0;
        _sumAbs = 0;
        _maxAbs = 0;
        _within5 = 0;
        _trims = 0;
        _seeks = 0;
    }
}
