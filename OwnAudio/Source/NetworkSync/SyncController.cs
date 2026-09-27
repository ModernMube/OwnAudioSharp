namespace OwnaudioNET.NetworkSync;

internal enum SyncActionKind
{
    None,
    Trim,
    Seek,
}

/// <summary>
/// What the follower should do now: nothing, run at a new tempo trim, or jump.
/// </summary>
internal readonly record struct SyncAction(SyncActionKind Kind, double Trim)
{
    public static readonly SyncAction None = new SyncAction(SyncActionKind.None, 1.0);
}

/// <summary>
/// Turns "how far behind the server are we" into a tempo trim, and into a seek only when the
/// gap is too big to run off. The clock itself is never written — so letting go of the server
/// is just a trim of 1.0, and nothing can jump.
/// </summary>
internal sealed class SyncController
{
    /// <summary>
    /// Inside this nobody hears a thing, and chasing it would only chase the noise.
    /// </summary>
    public const double DeadbandSeconds = 0.003;

    /// <summary>
    /// ±0.5% tempo. Two machines' crystals differ by 20–100 ppm, this is fifty times that.
    /// </summary>
    public const double MaxTrim = 0.005;

    /// <summary>
    /// A gap closes in about this long while it is under the trim's ceiling.
    /// </summary>
    public const double CatchUpSeconds = 4.0;

    public const double SeekAboveSeconds = 0.080;

    /// <summary>
    /// Every trim is a native call per track, so small changes wait.
    /// </summary>
    public const double MinTrimStep = 0.0002;

    public const double MinIntervalSeconds = 0.5;

    /// <summary>
    /// After a jump the fits restart; no decision until they had time to settle.
    /// </summary>
    public const double SettleSeconds = 1.5;

    private double _lastChange = double.NegativeInfinity;
    private double _settleUntil = double.NegativeInfinity;
    private int _bigStreak;

    public double Trim { get; private set; } = 1.0;

    public void Reset()
    {
        Trim = 1.0;
        _lastChange = double.NegativeInfinity;
        _settleUntil = double.NegativeInfinity;
        _bigStreak = 0;
    }

    /// <summary>
    /// Holds off like after a jump — for jumps made by someone else (a play, a server seek).
    /// </summary>
    public void Settle(double now)
    {
        _settleUntil = now + SettleSeconds;
        _bigStreak = 0;
    }

    /// <summary>
    /// One look at the gap. Error is server minus us in song seconds: positive means we lag.
    /// A jump needs two big readings in a row, one can be a stray.
    /// </summary>
    public SyncAction Evaluate(double now, double error)
    {
        if (now < _settleUntil) return SyncAction.None;

        double _gap = Math.Abs(error);

        if (_gap > SeekAboveSeconds)
        {
            if (++_bigStreak < 2) return SyncAction.None;

            Settle(now);
            Trim = 1.0;
            _lastChange = now;
            return new SyncAction(SyncActionKind.Seek, 1.0);
        }

        _bigStreak = 0;
        if (now - _lastChange < MinIntervalSeconds) return SyncAction.None;

        double _want = _gap < DeadbandSeconds
            ? 1.0
            : 1.0 + Math.Clamp(error / CatchUpSeconds, -MaxTrim, MaxTrim);

        if (_want == Trim) return SyncAction.None;
        if (_want != 1.0 && Math.Abs(_want - Trim) < MinTrimStep) return SyncAction.None;

        Trim = _want;
        _lastChange = now;
        return new SyncAction(SyncActionKind.Trim, _want);
    }
}
