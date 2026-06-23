using Ownaudio;
using Ownaudio.Core;
using OwnaudioNET.Synchronization;
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
        public const double InitialGracePeriodSeconds = 0.1;
        public const double AdaptiveWindowSeconds = 3.0;
        public const int RedZoneHitsToScaleUp = 5;
        public const double GreenStreakToScaleDown = 8.0;
        public const double AdaptiveScaleStep = 0.5;
        public const double AdaptiveScaleMin = 1.0;
        public const double AdaptiveScaleMax = 4.0;
    }

    #endregion

    #region MasterClock Synchronization

    /// <inheritdoc/>
    public void AttachToClock(MasterClock clock)
    {
        if (clock == null)
            throw new ArgumentNullException(nameof(clock));
        
        DetachFromClock();

        _masterClock = clock;

        double currentClockTime = _masterClock.CurrentTimestamp;
        double targetTrackPosition = currentClockTime - _startOffset;

        if (targetTrackPosition > 0)
        {
            if (Seek(targetTrackPosition))
            {
                _trackLocalTime = targetTrackPosition;
            }
            else
            {
                Seek(0);
                _trackLocalTime = 0.0;
            }
        }
        else
        {
            Seek(0);
            _trackLocalTime = 0.0;
        }

        _fractionalFrameAccumulator = 0.0;

        lock (_timingLock)
        {
            _totalSamplesProcessedFromFile = (long)(Math.Max(0, _trackLocalTime) * _streamInfo.SampleRate);
            _soundTouchOutputFramesTotal = (long)(Math.Max(0, _trackLocalTime) * _streamInfo.SampleRate);
        }

        _gracePeriodEndTime = currentClockTime + SyncConfig.InitialGracePeriodSeconds;
        IsSynchronized = true;
    }

    /// <inheritdoc/>
    public void DetachFromClock()
    {
        if (_masterClock != null)
        {
            _masterClock = null;
            _trackLocalTime = 0.0;

            IsSynchronized = false;
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
        double driftRange = SoftSyncTolerance - SyncTolerance;
        double driftInRange = drift - SyncTolerance;
        double adjustmentFactor = Math.Min(driftInRange / driftRange, 1.0);
        double adjustment = adjustmentFactor * SoftSyncMaxTempoAdjustment;

        bool isBehind = targetTrackTime > _trackLocalTime;
        
        float newTempoChange;
        if (isBehind)
        {
            newTempoChange = (float)((_tempo - 1.0f) * 100.0f + (adjustment * 100.0f));
        }
        else
        {
            newTempoChange = (float)((_tempo - 1.0f) * 100.0f - (adjustment * 100.0f));
        }

        _pendingSoftSyncTempoAdjustment = newTempoChange;

        double correctionRate;
        string correctionMode;

        if (_consecutiveUnderruns > 0)
        {
            correctionRate = 0.05;  // 5% during recovery (fast but stable)
            correctionMode = "Recovery";
            _consecutiveUnderruns--;
        }
        else if (drift > 0.025)  // >25ms - large drift
        {
            correctionRate = 0.10;  // 10% for large drift (aggressive)
            correctionMode = "Large";
        }
        else if (drift > 0.013)  // 13-25ms - medium drift
        {
            correctionRate = 0.05;  // 5% for medium drift (moderate)
            correctionMode = "Medium";
        }
        else  // 5-13ms - small drift
        {
            correctionRate = 0.01;  // 1% for small drift (gentle, stable)
            correctionMode = "Small";
        }

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
        _pendingSoftSyncTempoAdjustment = float.NaN;
    }

    /// <summary>
    /// Updates the adaptive scale factor based on recent synchronization performance.
    /// Increases scale on frequent red zone hits; decreases on sustained green zone streaks.
    /// </summary>
    /// <param name="inRedZone">Whether the current frame triggered red zone correction.</param>
    /// <param name="masterTimestamp">Current master clock timestamp for window tracking.</param>
    private void UpdateAdaptiveScale(bool inRedZone, double masterTimestamp)
    {
        if (inRedZone)
        {
            _greenZoneStreak = 0.0;

            if (_redZoneWindowStart <= 0.0)
                _redZoneWindowStart = masterTimestamp;

            _redZoneHits++;

            double elapsed = masterTimestamp - _redZoneWindowStart;

            if (elapsed >= SyncConfig.AdaptiveWindowSeconds)
            {
                if (_redZoneHits >= SyncConfig.RedZoneHitsToScaleUp)
                {
                    _adaptiveScale = Math.Min(_adaptiveScale + SyncConfig.AdaptiveScaleStep, SyncConfig.AdaptiveScaleMax);
#if DEBUG
                    Log.Debug($"[AdaptiveScale] Scale UP → {_adaptiveScale:F1}x (RedZoneHits={_redZoneHits} in {elapsed:F1}s)");
#endif
                }
                _redZoneHits = 0;
                _redZoneWindowStart = masterTimestamp;
            }
        }
        else
        {
            if (_redZoneWindowStart > 0.0)
            {
                double elapsed = masterTimestamp - _redZoneWindowStart;
                if (elapsed >= SyncConfig.AdaptiveWindowSeconds)
                {
                    _redZoneHits = 0;
                    _redZoneWindowStart = 0.0;
                }
            }

            if (_adaptiveScale > SyncConfig.AdaptiveScaleMin)
            {
                _greenZoneStreak += (1.0 / 48000.0) * 512;

                if (_greenZoneStreak >= SyncConfig.GreenStreakToScaleDown)
                {
                    _adaptiveScale = Math.Max(_adaptiveScale - SyncConfig.AdaptiveScaleStep, SyncConfig.AdaptiveScaleMin);
                    _greenZoneStreak = 0.0;
#if DEBUG
                    Log.Debug($"[AdaptiveScale] Scale DOWN → {_adaptiveScale:F1}x (GreenStreak={SyncConfig.GreenStreakToScaleDown}s)");
#endif
                }
            }
            else
            {
                _greenZoneStreak = 0.0;
            }
        }
    }

    #endregion

    #region Time-Based Reading

    /// <inheritdoc/>
    public bool ReadSamplesAtTime(double masterTimestamp, Span<float> buffer, int frameCount, out ReadResult result)
    {
        ThrowIfDisposed();

        double relativeTimestamp = masterTimestamp - _startOffset;
        
        if (relativeTimestamp < 0)
        {
            FillWithSilence(buffer, frameCount * _streamInfo.Channels);
            result = ReadResult.CreateSuccess(frameCount);
            return true;
        }

        double targetTrackTime = relativeTimestamp;
        double effectiveSyncTolerance = SyncTolerance * _adaptiveScale;
        double effectiveSoftSyncTolerance = SoftSyncTolerance * _adaptiveScale;

        bool gracePeriodActive = targetTrackTime < _gracePeriodEndTime;

        if (gracePeriodActive)
        {
            double signedDrift = targetTrackTime - _trackLocalTime;
            if (signedDrift < -effectiveSoftSyncTolerance)
            {
                gracePeriodActive = false;
                _gracePeriodEndTime = 0.0;
            }
            else
            {
                _trackLocalTime = targetTrackTime;
            }
        }

        double drift = Math.Abs(targetTrackTime - _trackLocalTime);

        if (!gracePeriodActive)
        {
            if (drift <= effectiveSyncTolerance)
            {
                // GREEN ZONE: No correction needed
                if (_consecutiveUnderruns > 0)
                {
                    _consecutiveUnderruns = 0;
                }

                if (_isSoftSyncActive)
                {
                    ResetSoftSync();
                    _isSoftSyncActive = false;
                }

                UpdateAdaptiveScale(false, masterTimestamp);

#if DEBUG
                // Log.Info($"[GreenZone] Drift={drift:F4}s - No correction");
#endif
            }
            else if (drift <= effectiveSoftSyncTolerance && _consecutiveUnderruns == 0)
            {
                // YELLOW ZONE: Apply soft sync (tempo adjustment)
                ApplySoftSync(drift, targetTrackTime);
                _isSoftSyncActive = true;

                UpdateAdaptiveScale(false, masterTimestamp);

#if DEBUG
                Log.Info($"[YellowZone] Drift={drift:F4}s - Soft sync active");
#endif
            }
            else
            {
                // RED ZONE: Hard sync required
                if (_isSoftSyncActive)
                {
                    ResetSoftSync();
                    _isSoftSyncActive = false;
                }

                double driftInSeconds = targetTrackTime - _trackLocalTime;
                bool isBehind = driftInSeconds > 0;

                if (isBehind)
                {
                    long driftFrames = (long)(Math.Abs(driftInSeconds) * _streamInfo.SampleRate);
                    int driftSamples = (int)(driftFrames * _streamInfo.Channels);

                    if (driftSamples > 0 && driftSamples <= _buffer.Available)
                    {
                        _buffer.Skip(driftSamples);
                        _needsFadeIn = true;
                        _trackLocalTime = targetTrackTime;
                        double exactSourceFrames = driftFrames * _tempo;
                        _fractionalFrameAccumulator += exactSourceFrames;
                        int sourceFramesAdvanced = (int)_fractionalFrameAccumulator;
                        _fractionalFrameAccumulator -= sourceFramesAdvanced;
                        UpdateSamplePosition(sourceFramesAdvanced);

                        double newPosition = _currentPosition + driftInSeconds;
                        Interlocked.Exchange(ref _currentPosition, newPosition);

                        UpdateAdaptiveScale(true, masterTimestamp);

#if DEBUG
                        Log.Info($"[RedZone-BufferSkip] Drift={drift:F4}s - Skipped {driftFrames} frames in buffer (instant resync)");
#endif
                    }
                    else
                    {
                        double timeSinceLastSeek = targetTrackTime - _lastSeekTime;

                        if (timeSinceLastSeek > SyncConfig.SeekWindowSeconds)
                        {
                            _seekCount = 0;
                            _lastSeekTime = targetTrackTime;
                        }

                        _seekCount++;

                        if (_seekCount > SyncConfig.MaxSeeksPerWindow)
                        {
                            PerformHardReset(targetTrackTime);
                            FillWithSilence(buffer, frameCount * _streamInfo.Channels);
                            result = ReadResult.CreateSuccess(frameCount);

                            UpdateAdaptiveScale(true, masterTimestamp);

#if DEBUG
                            Log.Info($"[RedZone] Seek cascade detected - Triggered HARD RESET");
#endif

                            return true;
                        }

                        double seekLatencyCompensation = _consecutiveUnderruns > 0 ? 0.300 : 0.100;
                        double filePosition = (targetTrackTime + seekLatencyCompensation) * _tempo;
#if DEBUG
                        Log.Info($"[RedZone-Seek] Drift={drift:F4}s - Hard sync (seek to {filePosition:F4}s, +{seekLatencyCompensation:F3}s compensation)");
#endif

                        if (!Seek(filePosition))
                        {
                            FillWithSilence(buffer, frameCount * _streamInfo.Channels);
                            result = ReadResult.CreateFailure(0, "Seek failed during drift correction");
                            UpdateAdaptiveScale(true, masterTimestamp);
                            return false;
                        }
                        _gracePeriodEndTime = targetTrackTime + SyncConfig.GracePeriodSeconds;
                        _trackLocalTime = targetTrackTime + seekLatencyCompensation;
                        FillWithSilence(buffer, frameCount * _streamInfo.Channels);
                        result = ReadResult.CreateSuccess(frameCount);
                        UpdateAdaptiveScale(true, masterTimestamp);
                        return true;
                    }
                }
                else
                {
                    double filePosition = targetTrackTime * _tempo;
                    if (Seek(filePosition))
                    {
                        _trackLocalTime = targetTrackTime;
                        _gracePeriodEndTime = targetTrackTime + SyncConfig.GracePeriodSeconds;
                    }
                    else
                    {
                        _trackLocalTime = targetTrackTime;
                    }

                    UpdateAdaptiveScale(true, masterTimestamp);

#if DEBUG
                    Log.Info($"[RedZone-Ahead] Drift={drift:F4}s - Clock jumped back, seeked decoder to {filePosition:F4}s");
#endif
                }
            }
        }

        int samplesToRead = frameCount * _streamInfo.Channels;
        int samplesRead = _buffer.Read(buffer.Slice(0, samplesToRead));
        int framesRead = samplesRead / _streamInfo.Channels;

        if (_needsFadeIn && samplesRead > 0)
        {
            _needsFadeIn = false;
            FadeInHead(buffer.Slice(0, samplesRead), Math.Min(128, samplesRead));
        }

        if (_buffer.Available < _buffer.Capacity / 2)
        {
            _bufferNeedsRefillEvent.Set();
        }
        
        if (framesRead > 0)
        {
            double frameDuration = 1.0 / _streamInfo.SampleRate;
            _trackLocalTime += framesRead * frameDuration;

            double exactSourceFrames = framesRead * _tempo;
            _fractionalFrameAccumulator += exactSourceFrames;
            int sourceFramesAdvanced = (int)_fractionalFrameAccumulator;
            _fractionalFrameAccumulator -= sourceFramesAdvanced;

            UpdateSamplePosition(sourceFramesAdvanced);

            double newPosition = _currentPosition + (framesRead * frameDuration);
            Interlocked.Exchange(ref _currentPosition, newPosition);

            if (gracePeriodActive && _consecutiveUnderruns > 0)
            {
                _consecutiveUnderruns--;
            }
        }

        if (framesRead < frameCount && !_isEndOfStream)
        {
            if (samplesRead > 0)
            {
                ApplyVolume(buffer.Slice(0, samplesRead), samplesRead);
                FadeOutTail(buffer.Slice(0, samplesRead), Math.Min(64, samplesRead));
            }

            int remainingSamples = (frameCount - framesRead) * _streamInfo.Channels;
            FillWithSilence(buffer.Slice(samplesRead), remainingSamples);
            int silenceFrames = frameCount - framesRead;
            double exactSilenceFrames = silenceFrames * _tempo;
            _fractionalFrameAccumulator += exactSilenceFrames;
            int silenceSourceFrames = (int)_fractionalFrameAccumulator;
            _fractionalFrameAccumulator -= silenceSourceFrames;
            UpdateSamplePosition(silenceSourceFrames);

            double frameDuration = 1.0 / _streamInfo.SampleRate;
            double silenceSeconds = silenceFrames * frameDuration;
            double newPos = _currentPosition + silenceSeconds;
            Interlocked.Exchange(ref _currentPosition, newPos);

            _consecutiveUnderruns = 5;
            long currentFramePosition = (long)(Position * _streamInfo.SampleRate);
            OnBufferUnderrun(new BufferUnderrunEventArgs(
                frameCount - framesRead,
                currentFramePosition));

            result = ReadResult.CreateFailure(frameCount, "Buffer underrun");
            return false;
        }

        ApplyVolume(buffer, frameCount * _streamInfo.Channels);

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

    /// <summary>
    /// Performs a "Hard Reset" of the synchronization state.
    /// Used when the system detects a seek cascade or unrecoverable drift.
    /// Mimics the behavior of the manual Reset button.
    /// </summary>
    private void PerformHardReset(double targetTime)
    {
#if DEBUG
        Log.Info($"[HardReset] Triggered at {targetTime:F4}s - Clearing all buffers and state");
#endif

        // 1. Clear SoundTouch state (Critical for fixing "chaos")
        lock (_soundTouchLock)
        {
            _soundTouch.Clear();
            _soundTouchAccumulationCount = 0;
        }

        // 2. Seek to target (clears circular buffer)
        Seek(targetTime);

        // 3. Reset logic state
        _consecutiveUnderruns = 0;
        _seekCount = 0;
        _lastSeekTime = targetTime;
        _gracePeriodEndTime = targetTime + SyncConfig.GracePeriodSeconds;
        _trackLocalTime = targetTime; // Force alignment

        // 4. Ensure Tempo is clean (if we had soft sync adjustment pending)
        ResetSoftSync();
    }

    #endregion
}
