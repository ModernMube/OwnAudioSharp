using System;

namespace Ownaudio.Safe;

/// <summary>
/// How hard the audio callback is working. Underruns say a block was already late,
/// these say how close the rest came.
/// </summary>
public readonly struct AudioStreamLoad
{
    /// <summary>
    /// Callbacks since the last reset.
    /// </summary>
    public ulong BlockCount { get; init; }

    /// <summary>Longest single callback.</summary>
    public TimeSpan PeakBlock { get; init; }

    /// <summary>Mean callback duration.</summary>
    public TimeSpan AverageBlock { get; init; }

    /// <summary>
    /// Frames that came out silent because the ring ran dry. 0 in callback mode.
    /// </summary>
    public ulong UnderrunFrames { get; init; }

    /// <summary>
    /// Mean share of the block period spent in the callback, 1.0 = late.
    /// </summary>
    public float AverageLoad { get; init; }

    /// <summary>
    /// Worst single block. This is what predicts dropouts, not the average.
    /// </summary>
    public float PeakLoad { get; init; }

    /// <summary>
    /// A block has already overrun its period.
    /// </summary>
    public bool HasOverrun => PeakLoad >= 1.0f;

    /// <summary>
    /// Log-friendly one-liner.
    /// </summary>
    public override string ToString()
        => $"load avg {AverageLoad:P1} peak {PeakLoad:P1}, block avg {AverageBlock.TotalMilliseconds:F2}ms " +
           $"peak {PeakBlock.TotalMilliseconds:F2}ms over {BlockCount} blocks, {UnderrunFrames} underrun frames";
}
