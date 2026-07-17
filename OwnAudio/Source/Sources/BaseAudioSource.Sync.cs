using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Sources;

/// <summary>
/// Sync support for BaseAudioSource. Lock-free: volatile fields + Interlocked, zero overhead
/// in the hot path when sync isn't used.
/// </summary>
public abstract partial class BaseAudioSource : ISynchronizable
{
    private long _samplePosition;
    private volatile string? _syncGroupId;
    private volatile bool _isSynchronized;

    /// <inheritdoc/>
    public long SamplePosition
    {
        get => Interlocked.Read(ref _samplePosition);
        protected set => Interlocked.Exchange(ref _samplePosition, value);
    }

    /// <inheritdoc/>
    public string? SyncGroupId
    {
        get => _syncGroupId;
        set => _syncGroupId = value;
    }

    /// <inheritdoc/>
    public bool IsSynchronized
    {
        get => _isSynchronized;
        set => _isSynchronized = value;
    }

    /// <summary>
    /// Seeks to the given sample position if we drifted more than ~10ms from it.
    /// </summary>
    /// <param name="samplePosition"></param>
    public virtual void ResyncTo(long samplePosition)
    {
        long _tolerance = (long)(0.01 * Config.SampleRate);
        long _drift = Math.Abs(Interlocked.Read(ref _samplePosition) - samplePosition);

        if (_drift > _tolerance)
            if (Seek((double)samplePosition / Config.SampleRate))
                Interlocked.Exchange(ref _samplePosition, samplePosition);
    }

    /// <summary>
    /// Bumps the sample position after a read.
    /// </summary>
    /// <param name="framesRead"></param>
    protected void UpdateSamplePosition(int framesRead) => Interlocked.Add(ref _samplePosition, framesRead);

    /// <summary>
    /// Sample position back to zero (stop / rewind).
    /// </summary>
    protected void ResetSamplePosition() => Interlocked.Exchange(ref _samplePosition, 0);

    /// <summary>
    /// Sets the sample position after a seek.
    /// </summary>
    /// <param name="position"></param>
    protected void SetSamplePosition(long position) => Interlocked.Exchange(ref _samplePosition, position);

    /// <summary>
    /// Seconds to samples.
    /// </summary>
    /// <param name="timeInSeconds"></param>
    /// <returns></returns>
    protected long CalculateSamplePosition(double timeInSeconds) => (long)(timeInSeconds * Config.SampleRate);

    /// <summary>
    /// Samples to seconds.
    /// </summary>
    /// <param name="samplePos"></param>
    /// <returns></returns>
    protected double CalculateTimePosition(long samplePos) => (double)samplePos / Config.SampleRate;
}
