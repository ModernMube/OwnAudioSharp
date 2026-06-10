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
    /// Uses zero-allocation decoding via IAudioDecoder.ReadFrames().
    /// </summary>
    private void DecoderThreadProc()
    {
        try
        {
            while (!_shouldStop)
            {
                if (!ShouldContinueDecoding())
                {
                    _pauseEvent.Wait(100);
                    continue;
                }

                ApplyPendingSoftSyncAdjustment();

                if (HandleSeekRequest())
                    continue;

                if (!ShouldFillBuffer())
                {
                    _bufferNeedsRefillEvent.WaitOne(10);
                    continue;
                }

                if (!ReadNextFrames())
                    break;
            }
        }
        catch (Exception ex)
        {
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
                    double originalTempoChange = (_tempo - 1.0f) * 100.0f;
                    _soundTouch.TempoChange = (float)originalTempoChange;
                }
                else
                {
                    _soundTouch.TempoChange = pendingAdjustment;
                }
            }
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
                    SetSamplePosition((long)(targetSeconds * _streamInfo.SampleRate));
                    _isEndOfStream = false;

                    _buffer.Clear();
                    lock (_soundTouchLock)
                    {
                        _soundTouch.Clear();
                        _soundTouchAccumulationCount = 0;
                    }

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
        bool isSoundTouchActive = _soundTouch.IsProcessingNeeded();
        int targetFillLevel = isSoundTouchActive
            ? (_buffer.Capacity * 3) / 4     // 75% for SoundTouch (needs more headroom for stretch latency)
            : (_buffer.Capacity * 1) / 2;    // 50% for direct playback

        return _buffer.Available < targetFillLevel;
    }

    /// <summary>
    /// Reads the next frames from the audio file.
    /// </summary>
    /// <returns>True if decoding should continue, false if EOF or error.</returns>
    private bool ReadNextFrames()
    {
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
        _isEndOfStream = true;

        if (_soundTouch.IsProcessingNeeded())
        {
            FlushSoundTouchBuffer();
        }

        if (Loop)
        {
            if (_decoder.TrySeek(TimeSpan.Zero, out string error))
            {
                Interlocked.Exchange(ref _currentPosition, 0.0);
                _isEndOfStream = false;

                lock (_soundTouchLock)
                {
                    _soundTouch.Clear();
                    _soundTouchAccumulationCount = 0;
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
            while (!_shouldStop && _buffer.Available > 0)
            {
                Thread.Sleep(10);
            }
            return false;
        }
    }

    /// <summary>
    /// Processes a successfully decoded frame.
    /// Bypasses SoundTouch at identity settings (tempo=1.0, pitch=0) to avoid latency.
    /// Transitions are handled without Flush() to prevent silence-pad crackling.
    /// </summary>
    private void ProcessDecodedFrame(AudioDecoderResult readResult)
    {
        int bytesRead = readResult.FramesRead * _streamInfo.Channels * sizeof(float);
        var floatSpan = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(_decodeBuffer.AsSpan(0, bytesRead));
        int frameCount = readResult.FramesRead;

        bool isProcessingNeeded = _soundTouch.IsProcessingNeeded();
        HandleSoundTouchTransition(isProcessingNeeded);
        _wasSoundTouchProcessing = isProcessingNeeded;
        
        lock (_timingLock)
        {
            _totalSamplesProcessedFromFile += frameCount;
        }

        if (isProcessingNeeded)
        {
            ProcessWithSoundTouch(floatSpan, frameCount);
        }
        else
        {
            _buffer.Write(floatSpan);
        }
    }

    /// <summary>
    /// Handles ON→OFF and OFF→ON transitions between SoundTouch and the direct path.
    /// ON→OFF: drains whatever samples SoundTouch already has (NO Flush/silence injection).
    ///         This preserves the last real audio and avoids the zero-padding crack.
    /// OFF→ON: clears SoundTouch state so stale data from a previous session doesn't appear.
    /// </summary>
    private void HandleSoundTouchTransition(bool isProcessingNeeded)
    {
        if (_wasSoundTouchProcessing && !isProcessingNeeded)
        {
            DrainSoundTouchBuffer();
        }
        else if (!_wasSoundTouchProcessing && isProcessingNeeded)
        {
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

                int requiredSize = samples.Length;
                if (_soundTouchInputBuffer.Length < requiredSize)
                {
                    OnError(new AudioErrorEventArgs(
                        $"SoundTouch input buffer overflow: required={requiredSize}, available={_soundTouchInputBuffer.Length}. " +
                        "Increase buffer size in constructor.", null));
                    return;
                }

                samples.CopyTo(_soundTouchInputBuffer.AsSpan(0, samples.Length));
                _soundTouch.PutSamples(_soundTouchInputBuffer.AsSpan(0, samples.Length), frameCount);

                int maxFrames = _soundTouchOutputBuffer.Length / _streamInfo.Channels;
                int framesReceived = _soundTouch.ReceiveSamples(_soundTouchOutputBuffer, maxFrames);

                if (framesReceived > 0)
                {
                    int samplesToAdd = framesReceived * _streamInfo.Channels;
                    AddToSoundTouchAccumulationBuffer(_soundTouchOutputBuffer.AsSpan(0, samplesToAdd));
                }
                
                if (_soundTouchAccumulationCount > 0)
                {
                    int samplesWritten = _buffer.Write(_soundTouchAccumulationBuffer.AsSpan(0, _soundTouchAccumulationCount));

                    if (samplesWritten > 0)
                    {
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
            OnError(new AudioErrorEventArgs(
                $"SoundTouch accumulation buffer overflow: required={requiredCapacity}, available={_soundTouchAccumulationBuffer.Length}. " +
                "Increase buffer size in constructor.", null));
            return;
        }

        samples.CopyTo(_soundTouchAccumulationBuffer.AsSpan(_soundTouchAccumulationCount, samples.Length));
        _soundTouchAccumulationCount += samples.Length;
    }

    /// <summary>
    /// Drains all samples that SoundTouch has already processed, WITHOUT injecting silence.
    /// Used during SoundTouch ON→OFF transitions to recover the last real audio frames.
    ///
    /// WHY NOT Flush()?
    /// <see cref="SoundTouchProcessor.Flush"/> internally feeds up to 200 batches of
    /// 128 silent (zero) frames to push residual samples out of the time-stretch pipeline.
    /// Those zero frames get written into the circular buffer and produce a clearly audible
    /// "click" or "pop" at the exact moment the user changes pitch/tempo back to identity.
    ///
    /// DrainSoundTouchBuffer only calls ReceiveSamples in a loop – it never adds input.
    /// Any samples that SoundTouch has not yet emitted stay in its internal buffer and
    /// are simply abandoned (a few milliseconds of audio at most, imperceptible).
    /// </summary>
    private void DrainSoundTouchBuffer()
    {
        lock (_soundTouchLock)
        {
            try
            {
                while (true)
                {
                    int maxFrames = _soundTouchOutputBuffer.Length / _streamInfo.Channels;
                    int framesReceived = _soundTouch.ReceiveSamples(_soundTouchOutputBuffer, maxFrames);
                    if (framesReceived == 0)
                        break;

                    int samplesToAdd = framesReceived * _streamInfo.Channels;
                    AddToSoundTouchAccumulationBuffer(_soundTouchOutputBuffer.AsSpan(0, samplesToAdd));
                }

                if (_soundTouchAccumulationCount > 0)
                {
                    int samplesWritten = _buffer.Write(_soundTouchAccumulationBuffer.AsSpan(0, _soundTouchAccumulationCount));
                    int remainingSamples = _soundTouchAccumulationCount - samplesWritten;
                    if (remainingSamples > 0)
                    {
                        _soundTouchAccumulationBuffer.AsSpan(samplesWritten, remainingSamples)
                            .CopyTo(_soundTouchAccumulationBuffer.AsSpan(0, remainingSamples));
                    }
                    _soundTouchAccumulationCount = remainingSamples;
                }

                _soundTouch.Clear();
                _soundTouchAccumulationCount = 0;
            }
            catch (Exception ex)
            {
                OnError(new AudioErrorEventArgs($"SoundTouch drain error: {ex.Message}", ex));
            }
        }
    }

    /// <summary>
    /// Flushes the SoundTouch buffer to retrieve all remaining processed samples.
    /// Used at EOF only – NOT used for ON/OFF transitions (use DrainSoundTouchBuffer instead).
    /// </summary>
    private void FlushSoundTouchBuffer()
    {
        lock (_soundTouchLock)
        {
            try
            {
                _soundTouch.Flush();

                while (true)
                {
                    int maxFrames = _soundTouchOutputBuffer.Length / _streamInfo.Channels;
                    int framesReceived = _soundTouch.ReceiveSamples(_soundTouchOutputBuffer, maxFrames);
                    if (framesReceived == 0)
                        break;

                    int samplesToAdd = framesReceived * _streamInfo.Channels;
                    AddToSoundTouchAccumulationBuffer(_soundTouchOutputBuffer.AsSpan(0, samplesToAdd));
                }

                if (_soundTouchAccumulationCount > 0)
                {
                    int samplesWritten = _buffer.Write(_soundTouchAccumulationBuffer.AsSpan(0, _soundTouchAccumulationCount));

                    int remainingSamples = _soundTouchAccumulationCount - samplesWritten;
                    if (remainingSamples > 0)
                    {
                        _soundTouchAccumulationBuffer.AsSpan(samplesWritten, remainingSamples)
                            .CopyTo(_soundTouchAccumulationBuffer.AsSpan(0, remainingSamples));
                    }
                    _soundTouchAccumulationCount = remainingSamples;
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
