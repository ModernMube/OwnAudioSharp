namespace OwnaudioNET.Sources;

/// <summary>
/// Snapshot of the adaptive sync state for diagnostics, no heap allocation.
/// </summary>
public readonly struct SyncDiagnosticsSnapshot
{
    /// <summary>
    /// Scale on the tolerance thresholds, 1.0 strict up to 4.0 fully relaxed.
    /// Above 1.0 means the engine compensated for processing pressure.
    /// </summary>
    public double AdaptiveScale { get; init; }

    /// <summary>
    /// Green zone threshold in ms after scaling, drift below needs no correction.
    /// </summary>
    public double EffectiveSyncToleranceMs { get; init; }

    /// <summary>
    /// Yellow zone threshold in ms after scaling, drift up to here triggers soft sync.
    /// </summary>
    public double EffectiveSoftSyncToleranceMs { get; init; }

    /// <summary>
    /// Red zone hits counted in the current measurement window.
    /// </summary>
    public int RedZoneHitsInWindow { get; init; }

    /// <summary>
    /// True when the tolerances sit above their baseline values.
    /// </summary>
    public bool IsRelaxed => AdaptiveScale > 1.0;
}
