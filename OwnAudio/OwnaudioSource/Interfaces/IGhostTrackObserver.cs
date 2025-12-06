using OwnaudioNET.Core;

namespace OwnaudioNET.Interfaces;

/// <summary>
/// Observer interface for audio sources that synchronize to a GhostTrack master clock.
/// Implementing this interface allows an audio source to receive automatic notifications
/// when the GhostTrack state changes (play, pause, seek, tempo, pitch, etc.).
///
/// This enables the "GhostTrack Master Pattern" where:
/// - The GhostTrack is the single source of truth for all synchronized sources
/// - Sources automatically follow the GhostTrack's state changes
/// - No manual synchronization calls needed
/// - Zero overhead when not attached to a GhostTrack
/// </summary>
public interface IGhostTrackObserver
{
    /// <summary>
    /// Called when the GhostTrack state changes (Playing, Paused, Stopped, etc.).
    /// The observer should immediately update its own state to match.
    /// </summary>
    /// <param name="newState">The new state of the GhostTrack.</param>
    void OnGhostTrackStateChanged(AudioState newState);

    /// <summary>
    /// Called when the GhostTrack position changes via Seek operation.
    /// The observer should immediately seek to the same position.
    /// </summary>
    /// <param name="newFramePosition">The new frame position of the GhostTrack.</param>
    void OnGhostTrackPositionChanged(long newFramePosition);

    /// <summary>
    /// Called when the GhostTrack tempo changes.
    /// The observer should immediately update its tempo to match.
    /// </summary>
    /// <param name="newTempo">The new tempo multiplier (1.0 = normal speed).</param>
    void OnGhostTrackTempoChanged(float newTempo);

    /// <summary>
    /// Called when the GhostTrack pitch changes.
    /// The observer should immediately update its pitch to match.
    /// </summary>
    /// <param name="newPitch">The new pitch shift in semitones.</param>
    void OnGhostTrackPitchChanged(float newPitch);

    /// <summary>
    /// Called when the GhostTrack loop state changes.
    /// The observer should immediately update its loop state to match.
    /// </summary>
    /// <param name="shouldLoop">True if looping is enabled, false otherwise.</param>
    void OnGhostTrackLoopChanged(bool shouldLoop);
}
