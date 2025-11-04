using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Sources;

/// <summary>
/// Partial class of BaseAudioSource that implements synchronization support.
/// This file contains all synchronization-related functionality separate from the core implementation.
/// </summary>
public abstract partial class BaseAudioSource : ISynchronizable
{
    private long _samplePosition;
    private string? _syncGroupId;
    private bool _isSynchronized;
    private readonly object _syncLock = new();

    /// <inheritdoc/>
    public long SamplePosition
    {
        get
        {
            lock (_syncLock)
            {
                return _samplePosition;
            }
        }
        protected set
        {
            lock (_syncLock)
            {
                _samplePosition = value;
            }
        }
    }

    /// <inheritdoc/>
    public string? SyncGroupId
    {
        get
        {
            lock (_syncLock)
            {
                return _syncGroupId;
            }
        }
        set
        {
            lock (_syncLock)
            {
                _syncGroupId = value;
            }
        }
    }

    /// <inheritdoc/>
    public bool IsSynchronized
    {
        get
        {
            lock (_syncLock)
            {
                return _isSynchronized;
            }
        }
        set
        {
            lock (_syncLock)
            {
                _isSynchronized = value;
            }
        }
    }

    /// <inheritdoc/>
    public virtual void ResyncTo(long samplePosition)
    {
        lock (_syncLock)
        {
            // Calculate tolerance (100ms at current sample rate)
            long tolerance = (long)(0.1 * Config.SampleRate);
            long currentPosition = _samplePosition;
            long drift = Math.Abs(currentPosition - samplePosition);

            // Only resync if drift exceeds tolerance
            if (drift > tolerance)
            {
                // Convert sample position to seconds for seeking
                double targetPositionInSeconds = (double)samplePosition / Config.SampleRate;

                // Perform the seek operation
                if (Seek(targetPositionInSeconds))
                {
                    _samplePosition = samplePosition;
                }
            }
        }
    }

    /// <summary>
    /// Updates the sample position after reading samples.
    /// Should be called by derived classes after each successful read operation.
    /// </summary>
    /// <param name="framesRead">The number of frames read in the last operation.</param>
    protected void UpdateSamplePosition(int framesRead)
    {
        lock (_syncLock)
        {
            _samplePosition += framesRead;
        }
    }

    /// <summary>
    /// Resets the sample position to zero.
    /// Should be called by derived classes when stopping or seeking to beginning.
    /// </summary>
    protected void ResetSamplePosition()
    {
        lock (_syncLock)
        {
            _samplePosition = 0;
        }
    }

    /// <summary>
    /// Sets the sample position to a specific value.
    /// Should be called by derived classes after seek operations.
    /// </summary>
    /// <param name="position">The new sample position.</param>
    protected void SetSamplePosition(long position)
    {
        lock (_syncLock)
        {
            _samplePosition = position;
        }
    }

    /// <summary>
    /// Gets the current sample position based on the time position and sample rate.
    /// </summary>
    /// <param name="timeInSeconds">The time position in seconds.</param>
    /// <returns>The corresponding sample position.</returns>
    protected long CalculateSamplePosition(double timeInSeconds)
    {
        return (long)(timeInSeconds * Config.SampleRate);
    }

    /// <summary>
    /// Gets the time position in seconds based on the sample position.
    /// </summary>
    /// <param name="samplePos">The sample position.</param>
    /// <returns>The corresponding time in seconds.</returns>
    protected double CalculateTimePosition(long samplePos)
    {
        return (double)samplePos / Config.SampleRate;
    }
}
