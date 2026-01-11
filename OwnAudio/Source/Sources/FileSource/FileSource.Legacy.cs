using Ownaudio;
using Ownaudio.Core;
using OwnaudioNET.Core;
using OwnaudioNET.Events;
using OwnaudioNET.Synchronization;

namespace OwnaudioNET.Sources;

/// <summary>
/// FileSource partial class containing deprecated GhostTrack synchronization logic.
/// This code is maintained for backward compatibility and will be removed in v3.0.0.
/// </summary>
public partial class FileSource
{
    #region GhostTrack Synchronization (Deprecated)

    /// <summary>
    /// Attaches this FileSource to a GhostTrack for automatic synchronization.
    /// After attachment, this source will automatically follow the GhostTrack's
    /// state (play/pause/stop), position (seek), tempo, and pitch changes.
    ///
    /// DEPRECATED: Use AttachToClock(MasterClock) instead.
    /// This method is maintained for backward compatibility and will be removed in v3.0.0.
    /// </summary>
    /// <param name="ghostTrack">The GhostTrack to attach to.</param>
    /// <exception cref="ArgumentNullException">Thrown when ghostTrack is null.</exception>
    [Obsolete("Use AttachToClock(MasterClock) instead. This method will be removed in v3.0.0.", error: false)]
    internal void AttachToGhostTrack(GhostTrackSource ghostTrack)
    {
        if (ghostTrack == null)
            throw new ArgumentNullException(nameof(ghostTrack));

        // Detach from previous ghost track if any
        DetachFromGhostTrack();

        // Attach to new ghost track
        _ghostTrack = ghostTrack;
        _ghostTrack.Subscribe(this);

        // Mark as synchronized
        IsSynchronized = true;
    }

    /// <summary>
    /// Detaches this FileSource from its GhostTrack.
    /// After detachment, this source operates independently.
    ///
    /// DEPRECATED: Use DetachFromClock() instead.
    /// This method is maintained for backward compatibility and will be removed in v3.0.0.
    /// </summary>
    [Obsolete("Use DetachFromClock() instead. This method will be removed in v3.0.0.", error: false)]
    internal void DetachFromGhostTrack()
    {
        if (_ghostTrack != null)
        {
            _ghostTrack.Unsubscribe(this);
            _ghostTrack = null;
        }

        // Mark as not synchronized
        IsSynchronized = false;
    }

    #endregion

    #region IGhostTrackObserver Implementation

    /// <inheritdoc/>
    public void OnGhostTrackStateChanged(AudioState newState)
    {
        // Automatically follow GhostTrack state changes
        switch (newState)
        {
            case AudioState.Playing:
                if (State != AudioState.Playing)
                    Play();
                break;

            case AudioState.Paused:
                if (State != AudioState.Paused)
                    Pause();
                break;

            case AudioState.Stopped:
                if (State != AudioState.Stopped)
                    Stop();
                break;
        }
    }

    /// <inheritdoc/>
    public void OnGhostTrackPositionChanged(long newFramePosition)
    {
        // Automatically seek to match GhostTrack position
        double targetPositionInSeconds = (double)newFramePosition / _streamInfo.SampleRate;
        Seek(targetPositionInSeconds);
    }

    /// <inheritdoc/>
    public void OnGhostTrackTempoChanged(float newTempo)
    {
        // Automatically update tempo to match GhostTrack
        Tempo = newTempo;
    }

    /// <inheritdoc/>
    public void OnGhostTrackPitchChanged(float newPitch)
    {
        // Automatically update pitch to match GhostTrack
        PitchShift = newPitch;
    }

    /// <inheritdoc/>
    public void OnGhostTrackLoopChanged(bool shouldLoop)
    {
        // Automatically update loop state to match GhostTrack
        Loop = shouldLoop;
    }

    #endregion

    #region Legacy Reading Strategy

    /// <summary>
    /// Reads samples when attached to GhostTrack (legacy mode).
    /// </summary>
    private int ReadSamplesLegacy(Span<float> buffer, int frameCount)
    {
        if (_buffer.Available > 0)
        {
            // Get ghost track position and check drift
            long ghostPosition = _ghostTrack!.CurrentFrame;
            long myPosition = SamplePosition;
            long drift = Math.Abs(ghostPosition - myPosition);

            // Only check drift if we are past the grace period
            long gracePeriodEndFrame = (long)(_gracePeriodEndTime * _streamInfo.SampleRate);
            if (drift > 512 && myPosition > gracePeriodEndFrame)
            {
                // Drift detected - resync immediately
                ResyncTo(ghostPosition);
            }
        }

        // Read from buffer (same as standalone)
        return ReadSamplesStandalone(buffer, frameCount);
    }

    #endregion
}
