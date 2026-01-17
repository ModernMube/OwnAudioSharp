using Ownaudio;
using Ownaudio.Core;
using Ownaudio.Decoders;
using OwnaudioNET.Core;
using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Sources;

/// <summary>
/// FileSource partial class containing decoder thread and SoundTouch processing logic.
/// </summary>
public partial class FileSource
{
    #region Decoder Thread

    /// <summary>
    /// Decoder thread procedure - runs in background and fills the buffer.
    /// Uses frame-based decoding via IAudioDecoder.DecodeNextFrame().
    /// </summary>
    private void DecoderThreadProc()
    {
        try
        {
            while (!_shouldStop)
            {
                // Wait if paused (and not pre-buffering)
                if (!ShouldContinueDecoding())
                {
                    _pauseEvent.Wait(100);
                    continue;
                }

                // Apply pending soft sync adjustment
                ApplyPendingSoftSyncAdjustment();

                // Handle seek request
                if (HandleSeekRequest())
                    continue;

                // Check if buffer needs filling
                if (!ShouldFillBuffer())
                {
                    // OPTIMIZATION (Phase 2): Event-driven waiting instead of polling
                    // Replaces Thread.Sleep(1) which causes ~1000 context switches/sec/track
                    // WaitOne(10) waits for signal or 10ms timeout, whichever comes first
                    // This reduces CPU overhead dramatically with 20+ decoder threads
                    _bufferNeedsRefillEvent.WaitOne(10);
                    continue;
                }

                // Decode next frame
                if (!DecodeNextFrame())
                    break;
            }
        }
        catch (Exception ex)
        {
            // Report error to main thread
            OnError(new AudioErrorEventArgs($"Decoder thread error: {ex.Message}", ex));
        }
    }

    /// <summary>
    /// Checks if decoder should continue decoding.
    /// </summary>
    private bool ShouldContinueDecoding()
    {
        return State == AudioState.Playing || _isPreBuffering;
    }

    /// <summary>
    /// Applies pending soft sync tempo adjustment from the Mixer thread.
    /// LOCK-FREE: Reads from volatile field and applies within decoder thread's lock context.
    /// </summary>
    private void ApplyPendingSoftSyncAdjustment()
    {
        float pendingAdjustment = _pendingSoftSyncTempoAdjustment;
        if (!float.IsNaN(pendingAdjustment) && pendingAdjustment != 0f)
        {
            lock (_soundTouchLock)
            {
                if (float.IsNaN(pendingAdjustment))
                {
                    // Reset to original tempo (sentinel value)
                    double originalTempoChange = (_tempo - 1.0f) * 100.0f;
                    _soundTouch.TempoChange = (float)originalTempoChange;
                }
                else
                {
                    // Apply the soft sync tempo adjustment
                    _soundTouch.TempoChange = pendingAdjustment;
                }
            }
            // Clear the pending adjustment (atomic write)
            _pendingSoftSyncTempoAdjustment = 0f;
        }
    }

    /// <summary>
    /// Handles seek requests from the main thread.
    /// </summary>
    /// <returns>True if seek was handled, false otherwise.</returns>
    private bool HandleSeekRequest()
    {
        if (_seekRequested)
        {
            lock (_seekLock)
            {
                double targetSeconds = Interlocked.CompareExchange(ref _seekTargetSeconds, 0, 0);
                var targetTimeSpan = TimeSpan.FromSeconds(targetSeconds);

                if (_decoder.TrySeek(targetTimeSpan, out string error))
                {
                    Interlocked.Exchange(ref _currentPosition, targetSeconds);
                    // Sync SamplePosition with new time position
                    SetSamplePosition((long)(targetSeconds * _streamInfo.SampleRate));
                    _isEndOfStream = false;

                    // Reset input-driven timing counter
                    lock (_timingLock)
                    {
                        _totalSamplesProcessedFromFile = (long)(targetSeconds * _streamInfo.SampleRate);
                    }
                }
                else
                {
                    OnError(new AudioErrorEventArgs($"Seek failed: {error}", null));
                }
                _seekRequested = false;
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// Checks if buffer needs filling.
    /// </summary>
    /// <returns>True if buffer should be filled, false otherwise.</returns>
    private bool ShouldFillBuffer()
    {
        // BUGFIX: Reduced target fill level to prevent accumulation buffer overflow.
        // Previous 87.5% (7/8) left only 1/8 (4096 frames) free space, but we read
        // 8192 frames at once. With SoundTouch time-stretch, this can expand even more.
        // This caused the accumulation buffer to grow indefinitely until overflow,
        // resulting in audio dropout after a short time with 15+ tracks at <95% tempo.
        // Solution: Use 50% (1/2) threshold to ensure at least 2x read chunk space.
        bool isSoundTouchActive = _soundTouch.IsProcessingNeeded();
        int targetFillLevel = isSoundTouchActive
            ? (_buffer.Capacity * 1) / 2     // 50% for SoundTouch (was 87.5%)
            : (_buffer.Capacity * 3) / 4;    // 75% for direct playback

        return _buffer.Available < targetFillLevel;
    }

    /// <summary>
    /// Decodes the next frame from the audio file.
    /// </summary>
    /// <returns>True if decoding should continue, false if EOF or error.</returns>
    private bool DecodeNextFrame()
    {
        // ZERO-ALLOC: Decode into the reusable buffer
        var readResult = _decoder.ReadFrames(_decodeBuffer);

        if (readResult.IsEOF)
        {
            return HandleEndOfStream();
        }
        else if (readResult.IsSucceeded && readResult.FramesRead > 0)
        {
            ProcessDecodedFrame(readResult);
            return true;
        }
        else if (!readResult.IsSucceeded)
        {
            // Decode error
            OnError(new AudioErrorEventArgs($"Decode error: {readResult.ErrorMessage}", null));
            _isEndOfStream = true;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Handles end of stream condition.
    /// </summary>
    /// <returns>True if decoding should continue (looping), false otherwise.</returns>
    private bool HandleEndOfStream()
    {
        // End of file reached
        _isEndOfStream = true;

        // Only flush SoundTouch if it was actually used
        if (_soundTouch.IsProcessingNeeded())
        {
            FlushSoundTouchBuffer();
        }

        // If looping, seek to beginning
        if (Loop)
        {
            if (_decoder.TrySeek(TimeSpan.Zero, out string error))
            {
                Interlocked.Exchange(ref _currentPosition, 0.0);
                _isEndOfStream = false;

                // Clear SoundTouch on loop
                lock (_soundTouchLock)
                {
                    _soundTouch.Clear();
                    // Clear accumulation buffer
                    _soundTouchAccumulationCount = 0;
                    // Reset transition tracking
                    _wasSoundTouchProcessing = false;
                }
                return true;
            }
            else
            {
                OnError(new AudioErrorEventArgs($"Loop seek failed: {error}", null));
                return false;
            }
        }
        else
        {
            // Wait for buffer to drain
            while (!_shouldStop && _buffer.Available > 0)
            {
                Thread.Sleep(10);
            }
            return false;
        }
    }

    /// <summary>
    /// Processes a successfully decoded frame.
    /// </summary>
    private void ProcessDecodedFrame(AudioDecoderResult readResult)
    {
        int bytesRead = readResult.FramesRead * _streamInfo.Channels * sizeof(float);
        var floatSpan = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(_decodeBuffer.AsSpan(0, bytesRead));
        int frameCount = readResult.FramesRead;

        // Bypass SoundTouch when tempo=1.0 AND pitch=0
        // OPTIMIZATION: Check outside lock to reduce contention with MixThread
        bool isProcessingNeeded = _soundTouch.IsProcessingNeeded();

        // Handle transitions between SoundTouch processing and direct write
        HandleSoundTouchTransition(isProcessingNeeded);

        // Update transition tracking
        _wasSoundTouchProcessing = isProcessingNeeded;

        // Track total samples processed from file for input-driven timing
        lock (_timingLock)
        {
            _totalSamplesProcessedFromFile += frameCount;
        }

        if (isProcessingNeeded)
        {
            // Process through SoundTouch
            ProcessWithSoundTouch(floatSpan, frameCount);
        }
        else
        {
            // Direct write to buffer
            _buffer.Write(floatSpan);
        }
    }

    /// <summary>
    /// Handles transitions between SoundTouch processing and direct write.
    /// </summary>
    private void HandleSoundTouchTransition(bool isProcessingNeeded)
    {
        // Detect transitions between SoundTouch processing and direct write
        if (_wasSoundTouchProcessing && !isProcessingNeeded)
        {
            // SoundTouch OFF (was ON) - Flush all remaining data
            FlushSoundTouchBuffer();
        }
        else if (!_wasSoundTouchProcessing && isProcessingNeeded)
        {
            // SoundTouch ON (was OFF) - Clear SoundTouch state
            lock (_soundTouchLock)
            {
                _soundTouch.Clear();
                _soundTouchAccumulationCount = 0;
            }
        }
    }

    #endregion

    #region SoundTouch Processing

    /// <summary>
    /// Processes audio samples through SoundTouch for pitch/tempo effects.
    /// Uses accumulation buffer pattern to ensure stable timing.
    /// OPTIMIZATION: This should only be called when tempo != 1.0 OR pitch != 0.
    /// </summary>
    /// <param name="samples">Input audio samples (interleaved if stereo).</param>
    /// <param name="frameCount">Number of input frames.</param>
    private void ProcessWithSoundTouch(ReadOnlySpan<float> samples, int frameCount)
    {
        lock (_soundTouchLock)
        {
            try
            {
                // Caller should check IsProcessingNeeded() before calling this method

                int requiredSize = samples.Length;
                if (_soundTouchInputBuffer.Length < requiredSize)
                {
                    // CRITICAL: This should NEVER happen if buffers are pre-allocated correctly
                    // Log error instead of reallocating to avoid GC in hot path
                    OnError(new AudioErrorEventArgs(
                        $"SoundTouch input buffer overflow: required={requiredSize}, available={_soundTouchInputBuffer.Length}. " +
                        "Increase buffer size in constructor.", null));
                    return;
                }

                // Copy to pre-allocated buffer
                samples.CopyTo(_soundTouchInputBuffer.AsSpan(0, samples.Length));

                // Put samples into SoundTouch
                _soundTouch.PutSamples(_soundTouchInputBuffer.AsSpan(0, samples.Length), frameCount);

                int maxFrames = _soundTouchOutputBuffer.Length / _streamInfo.Channels;
                int framesReceived = _soundTouch.ReceiveSamples(_soundTouchOutputBuffer, maxFrames);

                if (framesReceived > 0)
                {
                    // Add to accumulation buffer
                    int samplesToAdd = framesReceived * _streamInfo.Channels;
                    AddToSoundTouchAccumulationBuffer(_soundTouchOutputBuffer.AsSpan(0, samplesToAdd));
                }

                // Write IMMEDIATELY whatever we have accumulated
                if (_soundTouchAccumulationCount > 0)
                {
                    // Write ALL accumulated samples immediately
                    int samplesWritten = _buffer.Write(_soundTouchAccumulationBuffer.AsSpan(0, _soundTouchAccumulationCount));

                    if (samplesWritten > 0)
                    {
                        // Successfully wrote some/all samples - shift remaining
                        int remainingSamples = _soundTouchAccumulationCount - samplesWritten;
                        if (remainingSamples > 0)
                        {
                            _soundTouchAccumulationBuffer.AsSpan(samplesWritten, remainingSamples)
                                .CopyTo(_soundTouchAccumulationBuffer.AsSpan(0, remainingSamples));
                        }
                        _soundTouchAccumulationCount = remainingSamples;
                    }
                }
            }
            catch (Exception ex)
            {
                // Log error but don't crash decoder thread
                OnError(new AudioErrorEventArgs($"SoundTouch processing error: {ex.Message}", ex));
            }
        }
    }

    /// <summary>
    /// Adds samples to the SoundTouch accumulation buffer (similar to working code's AddToSoundTouchBuffer).
    /// This ensures samples are written in fixed-size chunks for stable timing.
    /// Zero-allocation in steady state with 8x pre-allocated buffer.
    /// </summary>
    /// <param name="samples">Audio samples to add to accumulation buffer.</param>
    private void AddToSoundTouchAccumulationBuffer(ReadOnlySpan<float> samples)
    {
        int requiredCapacity = _soundTouchAccumulationCount + samples.Length;
        if (requiredCapacity > _soundTouchAccumulationBuffer.Length)
        {
            // CRITICAL: This should NEVER happen if buffers are pre-allocated correctly
            // Log error instead of reallocating to avoid GC in hot path
            OnError(new AudioErrorEventArgs(
                $"SoundTouch accumulation buffer overflow: required={requiredCapacity}, available={_soundTouchAccumulationBuffer.Length}. " +
                "Increase buffer size in constructor.", null));
            return;
        }

        // Add samples to accumulation buffer
        samples.CopyTo(_soundTouchAccumulationBuffer.AsSpan(_soundTouchAccumulationCount, samples.Length));
        _soundTouchAccumulationCount += samples.Length;
    }

    /// <summary>
    /// Flushes the SoundTouch buffer to retrieve all remaining processed samples.
    /// Common pattern used at EOF and during transitions.
    /// </summary>
    private void FlushSoundTouchBuffer()
    {
        lock (_soundTouchLock)
        {
            try
            {
                _soundTouch.Flush();

                // Retrieve all remaining samples from SoundTouch and add to accumulation buffer
                while (true)
                {
                    int maxFrames = _soundTouchOutputBuffer.Length / _streamInfo.Channels;
                    int framesReceived = _soundTouch.ReceiveSamples(_soundTouchOutputBuffer, maxFrames);
                    if (framesReceived == 0)
                        break;

                    int samplesToAdd = framesReceived * _streamInfo.Channels;
                    AddToSoundTouchAccumulationBuffer(_soundTouchOutputBuffer.AsSpan(0, samplesToAdd));
                }

                // Flush accumulation buffer to CircularBuffer
                if (_soundTouchAccumulationCount > 0)
                {
                    _buffer.Write(_soundTouchAccumulationBuffer.AsSpan(0, _soundTouchAccumulationCount));
                    _soundTouchAccumulationCount = 0;
                }
            }
            catch (Exception ex)
            {
                OnError(new AudioErrorEventArgs($"SoundTouch flush error: {ex.Message}", ex));
            }
        }
    }

    #endregion
}
