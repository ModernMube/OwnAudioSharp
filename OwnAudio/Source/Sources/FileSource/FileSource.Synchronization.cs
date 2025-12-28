using Ownaudio;
using Ownaudio.Core;
using Ownaudio.Synchronization;
using OwnaudioNET.Core;
using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;

using Logger;

namespace OwnaudioNET.Sources;

/// <summary>
/// FileSource partial class containing MasterClock synchronization logic.
/// </summary>
public partial class FileSource
{
    #region Synchronization Configuration

    /// <summary>
    /// Synchronization configuration constants.
    /// </summary>
    private static class SyncConfig
    {
        public const int MaxSeeksPerWindow = 10;
        public const double SeekWindowSeconds = 5.0;
        public const double GracePeriodSeconds = 1.0;
        public const double InitialGracePeriodSeconds = 0.5;
    }

    #endregion

    #region MasterClock Synchronization

    /// <inheritdoc/>
    public void AttachToClock(MasterClock clock)
    {
        if (clock == null)
            throw new ArgumentNullException(nameof(clock));

        // Detach from GhostTrack if attached
        if (_ghostTrack != null)
        {
            DetachFromGhostTrack();
        }

        // Detach from previous clock if any
        DetachFromClock();

        _masterClock = clock;

        // Initialize _trackLocalTime to match current clock position
        double currentClockTime = _masterClock.CurrentTimestamp;
        // Adjust for track start offset
        _trackLocalTime = currentClockTime - _startOffset; 
        _fractionalFrameAccumulator = 0.0;

        // Reset input-driven timing counter
        lock (_timingLock)
        {
            _totalSamplesProcessedFromFile = (long)(_trackLocalTime * _streamInfo.SampleRate);
        }

        // Set short initial grace period at AttachToClock to prevent immediate Seek
        _gracePeriodEndTime = currentClockTime + SyncConfig.InitialGracePeriodSeconds;

        // Mark as synchronized
        IsSynchronized = true;
    }

    /// <inheritdoc/>
    public void DetachFromClock()
    {
        if (_masterClock != null)
        {
            _masterClock = null;
            _trackLocalTime = 0.0;

            // Mark as not synchronized (unless attached to GhostTrack)
            if (_ghostTrack == null)
            {
                IsSynchronized = false;
            }
        }
    }

    #endregion

    #region Soft Sync Methods

    /// <summary>
    /// Applies soft synchronization by adjusting tempo to gradually correct drift.
    /// This is used in the Yellow Zone (drift between SyncTolerance and SoftSyncTolerance).
    /// LOCK-FREE: This method no longer acquires _soundTouchLock to prevent blocking the Mixer thread.
    /// Instead, it writes to a volatile field that the Decoder thread reads and applies.
    /// </summary>
    /// <param name="drift">The absolute drift value in seconds.</param>
    /// <param name="targetTrackTime">The target track time we should be at.</param>
    private void ApplySoftSync(double drift, double targetTrackTime)
    {
        // Calculate tempo adjustment based on drift magnitude
        // Scale linearly from 0% at SyncTolerance to MaxTempoAdjustment at SoftSyncTolerance
        double driftRange = SoftSyncTolerance - SyncTolerance;
        double driftInRange = drift - SyncTolerance;
        double adjustmentFactor = Math.Min(driftInRange / driftRange, 1.0);
        double adjustment = adjustmentFactor * SoftSyncMaxTempoAdjustment;

        // Determine direction: are we behind or ahead?
        bool isBehind = targetTrackTime > _trackLocalTime;
        
        // Calculate the new tempo change percentage
        float newTempoChange;
        if (isBehind)
        {
            // We're behind - speed up slightly
            newTempoChange = (float)((_tempo - 1.0f) * 100.0f + (adjustment * 100.0f));
        }
        else
        {
            // We're ahead - slow down slightly
            newTempoChange = (float)((_tempo - 1.0f) * 100.0f - (adjustment * 100.0f));
        }

        // LOCK-FREE: Write to volatile field (atomic operation)
        // The Decoder thread will pick this up and apply it safely within its own lock context
        _pendingSoftSyncTempoAdjustment = newTempoChange;

        // ADAPTIVE CORRECTION: Adjust rate based on drift magnitude and recovery state
        double correctionRate;
        string correctionMode;
        
        // Check if we're in post-dropout recovery (more aggressive)
        if (_consecutiveUnderruns > 0)
        {
            correctionRate = 0.05;  // 5% during recovery (fast but stable)
            correctionMode = "Recovery";
            _consecutiveUnderruns--;
        }
        // Check drift magnitude (larger drift = faster correction)
        else if (drift > 0.100)  // >100ms - large drift
        {
            correctionRate = 0.10;  // 10% for large drift (aggressive)
            correctionMode = "Large";
        }
        else if (drift > 0.050)  // 50-100ms - medium drift
        {
            correctionRate = 0.05;  // 5% for medium drift (moderate)
            correctionMode = "Medium";
        }
        else  // 10-50ms - small drift
        {
            correctionRate = 0.01;  // 1% for small drift (gentle, stable)
            correctionMode = "Small";
        }

        // Apply the correction
        double correctionAmount = drift * correctionRate;
        if (isBehind)
        {
            _trackLocalTime += correctionAmount;
        }
        else
        {
            _trackLocalTime -= correctionAmount;
        }

        _lastDrift = drift;

        #if DEBUG
        Log.Debug($"[SoftSync-{correctionMode}] Drift={drift:F4}s, Rate={correctionRate:P0}, Correction={correctionAmount:F6}s ({(isBehind ? "speed up" : "slow down")})");
        #endif
    }

    /// <summary>
    /// Resets soft sync by restoring the original tempo setting.
    /// Called when drift returns to Green Zone.
    /// LOCK-FREE: Uses volatile field to communicate with Decoder thread.
    /// </summary>
    private void ResetSoftSync()
    {
        // Signal to Decoder thread to reset to original tempo
        // Use a special sentinel value (float.NaN) to indicate "reset to original"
        _pendingSoftSyncTempoAdjustment = float.NaN;
    }

    #endregion

    #region Time-Based Reading

    /// <inheritdoc/>
    public bool ReadSamplesAtTime(double masterTimestamp, Span<float> buffer, int frameCount, out ReadResult result)
    {
        ThrowIfDisposed();

        // Calculate track-local timestamp
        double relativeTimestamp = masterTimestamp - _startOffset;

        // Before track start return silence
        if (relativeTimestamp < 0)
        {
            FillWithSilence(buffer, frameCount * _streamInfo.Channels);
            result = ReadResult.CreateSuccess(frameCount);
            return true;
        }

        // Target physical time
        double targetTrackTime = relativeTimestamp;

        // Grace period handling
        bool gracePeriodActive = targetTrackTime < _gracePeriodEndTime;

        if (gracePeriodActive)
        {
            // Force _trackLocalTime to sync with target to prevent re-seeking
            _trackLocalTime = targetTrackTime;
        }

        // Drift correction comparing target and local time
        double drift = Math.Abs(targetTrackTime - _trackLocalTime);

        // Three-Zone Drift Correction System
        if (!gracePeriodActive)
        {
            if (drift <= SyncTolerance)
            {
                // GREEN ZONE: No correction needed
                // Reset soft sync ONLY if it was active
                if (_isSoftSyncActive)
                {
                    ResetSoftSync();
                    _isSoftSyncActive = false;
                }

                #if DEBUG
                // Console.WriteLine($"[GreenZone] Drift={drift:F4}s - No correction");
                #endif
            }
            else if (drift <= SoftSyncTolerance)
            {
                // YELLOW ZONE: Apply soft sync (tempo adjustment)
                ApplySoftSync(drift, targetTrackTime);
                _isSoftSyncActive = true;

                #if DEBUG
                Console.WriteLine($"[YellowZone] Drift={drift:F4}s - Soft sync active");
                #endif
            }
            else
            {
                // RED ZONE: Hard sync required
                // Reset soft sync before correction
                if (_isSoftSyncActive)
                {
                    ResetSoftSync();
                    _isSoftSyncActive = false;
                }

                // SMART BUFFER SKIP: Try to skip samples in buffer before seeking
                // Calculate drift in frames
                double driftInSeconds = targetTrackTime - _trackLocalTime;
                bool isBehind = driftInSeconds > 0;
                
                if (isBehind)
                {
                    // We are behind - check if we can skip samples in buffer
                    long driftFrames = (long)(Math.Abs(driftInSeconds) * _streamInfo.SampleRate);
                    int driftSamples = (int)(driftFrames * _streamInfo.Channels);
                    
                    // Check if buffer has enough data to skip
                    if (driftSamples > 0 && driftSamples <= _buffer.Available)
                    {
                        // INSTANT RESYNC: Skip samples in buffer
                        _buffer.Skip(driftSamples);
                        
                        // Update track time to target
                        _trackLocalTime = targetTrackTime;
                        
                        // Update position tracking
                        double exactSourceFrames = driftFrames * _tempo;
                        _fractionalFrameAccumulator += exactSourceFrames;
                        int sourceFramesAdvanced = (int)_fractionalFrameAccumulator;
                        _fractionalFrameAccumulator -= sourceFramesAdvanced;
                        UpdateSamplePosition(sourceFramesAdvanced);
                        
                        double newPosition = _currentPosition + (driftInSeconds * _tempo);
                        Interlocked.Exchange(ref _currentPosition, newPosition);
                        
                        #if DEBUG
                        Console.WriteLine($"[RedZone-BufferSkip] Drift={drift:F4}s - Skipped {driftFrames} frames in buffer (instant resync)");
                        #endif
                        
                        // Continue to normal read below (we're now in sync)
                        // Fall through to read samples
                    }
                    else
                    {
                        // Buffer doesn't have enough data - must seek
                        // Check if we're seeking too frequently
                        double timeSinceLastSeek = targetTrackTime - _lastSeekTime;

                        if (timeSinceLastSeek > SyncConfig.SeekWindowSeconds)
                        {
                            // Reset counter if outside the window
                            _seekCount = 0;
                            _lastSeekTime = targetTrackTime;
                        }

                        _seekCount++;

                        if (_seekCount > SyncConfig.MaxSeeksPerWindow)
                        {
                            // Too many Seeks - disable drift correction for this track
                            FillWithSilence(buffer, frameCount * _streamInfo.Channels);
                            // Force sync to prevent further attempts
                            _trackLocalTime = targetTrackTime;
                            result = ReadResult.CreateFailure(0, "Seek cascade detected - sync disabled");

                            #if DEBUG
                            Console.WriteLine($"[RedZone] Seek cascade detected - sync disabled");
                            #endif

                            return false;
                        }

                        // PREDICTIVE SEEK: Seek slightly ahead to account for decoding latency
                        // This prevents the "instant stale" problem where data arrives too late
                        double seekLatencyCompensation = 0.100; // 100ms ahead
                        double filePosition = (targetTrackTime + seekLatencyCompensation) * _tempo;

                        #if DEBUG
                        Console.WriteLine($"[RedZone-Seek] Drift={drift:F4}s - Hard sync (seek to {filePosition:F4}s, +{seekLatencyCompensation:F3}s compensation)");
                        #endif

                        if (!Seek(filePosition))
                        {
                            // Seek failed - fill with silence and report failure
                            FillWithSilence(buffer, frameCount * _streamInfo.Channels);
                            result = ReadResult.CreateFailure(0, "Seek failed during drift correction");
                            return false;
                        }

                        // Set grace period to prevent immediate re-seek
                        _gracePeriodEndTime = targetTrackTime + SyncConfig.GracePeriodSeconds;

                        // Update track time to compensated position
                        _trackLocalTime = targetTrackTime + seekLatencyCompensation;

                        // Return silence immediately as SUCCESS to prevent cascade
                        FillWithSilence(buffer, frameCount * _streamInfo.Channels);
                        result = ReadResult.CreateSuccess(frameCount);
                        return true;
                    }
                }
                else
                {
                    // We are ahead - this is unusual, just force sync
                    _trackLocalTime = targetTrackTime;
                    
                    #if DEBUG
                    Console.WriteLine($"[RedZone-Ahead] Drift={drift:F4}s - Track ahead of clock, force sync");
                    #endif
                }
            }
        }

        // Read from circular buffer
        int samplesToRead = frameCount * _streamInfo.Channels;
        int samplesRead = _buffer.Read(buffer.Slice(0, samplesToRead));
        int framesRead = samplesRead / _streamInfo.Channels;

        // Update track local time using simple output-driven approach
        // This is simpler and more stable than input-driven timing
        if (framesRead > 0)
        {
            double frameDuration = 1.0 / _streamInfo.SampleRate;
            _trackLocalTime += framesRead * frameDuration;

            // Also update the base position tracking with FRACTIONAL ACCUMULATION
            double exactSourceFrames = framesRead * _tempo;
            _fractionalFrameAccumulator += exactSourceFrames;
            int sourceFramesAdvanced = (int)_fractionalFrameAccumulator;
            _fractionalFrameAccumulator -= sourceFramesAdvanced;
            
            UpdateSamplePosition(sourceFramesAdvanced);

            double newPosition = _currentPosition + (framesRead * frameDuration * _tempo);
            Interlocked.Exchange(ref _currentPosition, newPosition);
        }

        // Underrun check
        if (framesRead < frameCount && !_isEndOfStream)
        {
            // Fill remaining with silence
            int remainingSamples = (frameCount - framesRead) * _streamInfo.Channels;
            FillWithSilence(buffer.Slice(samplesRead), remainingSamples);

            // NOTE: Do NOT advance _trackLocalTime here - it was already advanced above for framesRead
            // We only need to update position tracking for the silence frames
            int silenceFrames = frameCount - framesRead;
            double exactSilenceFrames = silenceFrames * _tempo;
            _fractionalFrameAccumulator += exactSilenceFrames;
            int silenceSourceFrames = (int)_fractionalFrameAccumulator;
            _fractionalFrameAccumulator -= silenceSourceFrames;
            UpdateSamplePosition(silenceSourceFrames);

            double frameDuration = 1.0 / _streamInfo.SampleRate;
            double silenceSeconds = silenceFrames * frameDuration;
            double newPos = _currentPosition + (silenceSeconds * _tempo);
            Interlocked.Exchange(ref _currentPosition, newPos);

            // ADAPTIVE CORRECTION: Trigger aggressive recovery for next 5 frames
            _consecutiveUnderruns = 5;

            // Report dropout
            long currentFramePosition = (long)(Position * _streamInfo.SampleRate);
            OnBufferUnderrun(new BufferUnderrunEventArgs(
                frameCount - framesRead,
                currentFramePosition));

            // Return failure for underrun
            result = ReadResult.CreateFailure(frameCount, "Buffer underrun");
            return false; 
        }

        // Apply volume
        ApplyVolume(buffer, frameCount * _streamInfo.Channels);

        // Check for end of stream with looping
        if (_isEndOfStream && _buffer.IsEmpty)
        {
            if (Loop)
            {
                Seek(0);
                _trackLocalTime = 0.0;
            }
            else
            {
                State = AudioState.EndOfStream;
                result = ReadResult.CreateSuccess(framesRead);
                return true;
            }
        }

        result = ReadResult.CreateSuccess(framesRead);
        return true;
    }

    /// <summary>
    /// Reads samples when attached to MasterClock (synchronized mode).
    /// </summary>
    private int ReadSamplesSynchronized(Span<float> buffer, int frameCount)
    {
        bool success = ReadSamplesAtTime(
            _masterClock!.CurrentTimestamp,
            buffer,
            frameCount,
            out ReadResult result);

        return result.FramesRead;
    }

    #endregion
}
