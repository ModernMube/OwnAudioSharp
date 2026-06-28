namespace OwnaudioNET.RustNext.Sources;

/// <summary>
/// Provides a snapshot of the current adaptive synchronization state for diagnostic purposes.
/// Returned by <see cref="FileSource.SyncDiagnostics"/> without any heap allocation.
/// </summary>
/// <remarks>
/// Use this struct to detect whether the adaptive tolerance system has elevated its thresholds,
/// which may indicate an underlying processing performance issue that would otherwise be hidden.
/// A value of <see cref="AdaptiveScale"/> greater than <c>1.0</c> means the engine has
/// automatically relaxed its sync tolerances due to repeated red-zone drift events.
/// </remarks>
public readonly struct SyncDiagnosticsSnapshot
{
    /// <summary>
    /// Gets the current adaptive scale factor applied to all tolerance thresholds.
    /// Range is <c>1.0</c> (strict, optimal) to <c>4.0</c> (fully relaxed).
    /// Any value above <c>1.0</c> indicates the system compensated for processing pressure.
    /// </summary>
    public double AdaptiveScale { get; init; }

    /// <summary>
    /// Gets the effective green zone threshold in milliseconds after adaptive scaling.
    /// Drift below this value requires no correction.
    /// </summary>
    public double EffectiveSyncToleranceMs { get; init; }

    /// <summary>
    /// Gets the effective yellow zone threshold in milliseconds after adaptive scaling.
    /// Drift between <see cref="EffectiveSyncToleranceMs"/> and this value triggers soft sync.
    /// </summary>
    public double EffectiveSoftSyncToleranceMs { get; init; }

    /// <summary>
    /// Gets the number of red zone hits counted in the current measurement window.
    /// Resets when the window expires (<c>AdaptiveWindowSeconds</c>).
    /// </summary>
    public int RedZoneHitsInWindow { get; init; }

    /// <summary>
    /// Gets a value indicating whether the adaptive system has relaxed the tolerances
    /// above their configured baseline values.
    /// </summary>
    public bool IsRelaxed => AdaptiveScale > 1.0;
}
