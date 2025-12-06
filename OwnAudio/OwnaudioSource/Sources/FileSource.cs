using Ownaudio;
using Ownaudio.Core;
using Ownaudio.Decoders;
using OwnaudioNET.BufferManagement;
using OwnaudioNET.Core;
using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Processing;

namespace OwnaudioNET.Sources;

/// <summary>
/// File-based audio source with background decoding and optional SoundTouch processing.
/// Provides smooth playback by decoding audio data in a separate thread and buffering it.
/// Uses Ownaudio.Core decoders (WavDecoder, Mp3Decoder, FlacDecoder) via AudioDecoderFactory.
/// Supports pitch shifting and tempo control via SoundTouch.NET.
/// Implements ISynchronizable for sample-accurate multi-track synchronization with drift correction.
///
/// NEW ARCHITECTURE (GhostTrack Observer Pattern):
/// - Implements IGhostTrackObserver to receive automatic notifications from GhostTrack
/// - Zero overhead when not attached to a GhostTrack (single null check)
/// - Automatic state/position/tempo/pitch synchronization
/// - Continuous drift correction in ReadSamples hot path
/// </summary>
public partial class FileSource : BaseAudioSource, ISynchronizable, IGhostTrackObserver
{
    private readonly IAudioDecoder _decoder;
    private readonly CircularBuffer _buffer;
    private readonly Thread _decoderThread;
    private readonly object _seekLock = new();
    private readonly object _soundTouchLock = new();
    private readonly ManualResetEventSlim _pauseEvent;
    private readonly int _bufferSizeInFrames;
    private readonly AudioConfig _config;
    private readonly AudioStreamInfo _streamInfo;
    private readonly SoundTouchProcessor _soundTouch;
    private readonly float[] _soundTouchOutputBuffer;  // Buffer for receiving from SoundTouch
    private float[] _soundTouchInputBuffer;  // Pre-allocated buffer for PutSamples
    private float[] _soundTouchAccumulationBuffer;  // Accumulation buffer (like working code)
    private int _soundTouchAccumulationCount;  // Tracks samples in accumulation buffer

    private volatile bool _shouldStop;
    private volatile bool _seekRequested;
    private double _seekTargetSeconds;
    private double _currentPosition;
    private volatile bool _isEndOfStream;
    private float _tempo = 1.0f;
    private float _pitchShift = 0.0f;
    private bool _disposed;

    // NEW: GhostTrack observer pattern (replaces old sync gate mechanism)
    private GhostTrackSource? _ghostTrack = null;  // null = not synchronized (zero overhead)

    /// <inheritdoc/>
    public override AudioConfig Config => _config;

    /// <inheritdoc/>
    public override AudioStreamInfo StreamInfo => _streamInfo;

    /// <inheritdoc/>
    public override double Position => Interlocked.CompareExchange(ref _currentPosition, 0, 0);

    /// <inheritdoc/>
    public override double Duration => _streamInfo.Duration.TotalSeconds;

    /// <inheritdoc/>
    public override bool IsEndOfStream => _isEndOfStream;

    /// <summary>
    /// Gets or sets the playback tempo multiplier (1.0 = normal speed).
    /// Valid range: 0.5x to 2.0x (converted to -50% to +100% for SoundTouch).
    /// </summary>
    public override float Tempo
    {
        get => _tempo;
        set
        {
            // Clamp to 0.5x to 2.0x range
            float clamped = Math.Clamp(value, 0.5f, 2.0f);
            if (Math.Abs(_tempo - clamped) < 0.001f)
                return;

            _tempo = clamped;

            // Convert multiplier to percentage for SoundTouch
            // Tempo multiplier: 0.5 = -50%, 1.0 = 0%, 2.0 = +100%
            float tempoChangePercent = (_tempo - 1.0f) * 100.0f;
            _soundTouch.TempoChange = tempoChangePercent;
        }
    }

    /// <summary>
    /// Gets or sets the pitch shift in semitones (0 = no shift).
    /// Valid range: -12 to +12 semitones (1 octave down to 1 octave up).
    /// </summary>
    public override float PitchShift
    {
        get => _pitchShift;
        set
        {
            // Clamp to -12 to +12 semitones
            float clamped = Math.Clamp(value, -12.0f, 12.0f);
            if (Math.Abs(_pitchShift - clamped) < 0.001f)
                return;

            _pitchShift = clamped;
            _soundTouch.PitchSemiTones = _pitchShift;
        }
    }

    /// <summary>
    /// Initializes a new instance of the FileSource class.
    /// Automatically detects audio format and creates appropriate decoder.
    /// </summary>
    /// <param name="filePath">Path to the audio file to load.</param>
    /// <param name="bufferSizeInFrames">Size of the internal buffer in frames (default: 8192).</param>
    /// <param name="targetSampleRate">Target sample rate (0 = use source rate, default: 48000).</param>
    /// <param name="targetChannels">Target channel count (0 = use source channels, default: 2).</param>
    /// <exception cref="ArgumentNullException">Thrown when filePath is null.</exception>
    /// <exception cref="FileNotFoundException">Thrown when file does not exist.</exception>
    /// <exception cref="AudioException">Thrown when the file cannot be opened or format is unsupported.</exception>
    public FileSource(
        string filePath,
        int bufferSizeInFrames = 8192,
        int targetSampleRate = 48000,
        int targetChannels = 2)
    {
        if (string.IsNullOrEmpty(filePath))
            throw new ArgumentNullException(nameof(filePath));

        _filePath = filePath; // Store file path for data extraction
        _bufferSizeInFrames = bufferSizeInFrames;

        // Create decoder using AudioDecoderFactory (auto-detects format)
        // Decoder will handle resampling/channel conversion internally
        _decoder = AudioDecoderFactory.Create(filePath, targetSampleRate, targetChannels);
        _streamInfo = _decoder.StreamInfo;

        // NOTE: Some decoders (like MFMp3Decoder with Media Foundation) may not support
        // resampling and will return audio in source format. This is acceptable -
        // the AudioMixer will validate format compatibility.
        // We just use whatever format the decoder provides.

        _config = new AudioConfig
        {
            SampleRate = _streamInfo.SampleRate,  // Use decoder's actual output format
            Channels = _streamInfo.Channels,      // Use decoder's actual output format
            BufferSize = bufferSizeInFrames
        };

        // Initialize circular buffer with 4x size for better buffering (like Ownaudio SourceManager)
        // This prevents underruns during temporary decoder delays
        int bufferSizeInSamples = bufferSizeInFrames * _streamInfo.Channels * 4;
        _buffer = new CircularBuffer(bufferSizeInSamples);

        // Initialize SoundTouch processor
        _soundTouch = new SoundTouchProcessor(_streamInfo.SampleRate, _streamInfo.Channels);
        _soundTouchOutputBuffer = new float[bufferSizeInFrames * _streamInfo.Channels * 2]; // Buffer for ReceiveSamples

        // Pre-allocate input buffer with generous headroom to avoid reallocations
        // Max decoder frame size is typically ~8192 samples, so 4x is safe
        _soundTouchInputBuffer = new float[bufferSizeInFrames * _streamInfo.Channels * 4]; // Increased from 2x to 4x

        // Accumulation buffer with 8x size to handle tempo changes (2x tempo = 2x samples)
        _soundTouchAccumulationBuffer = new float[bufferSizeInFrames * _streamInfo.Channels * 8]; // Increased from 4x to 8x
        _soundTouchAccumulationCount = 0;

        // Initialize synchronization primitives
        _pauseEvent = new ManualResetEventSlim(false);
        _shouldStop = false;
        _seekRequested = false;
        _currentPosition = 0.0;
        _isEndOfStream = false;

        // Create decoder thread but DON'T start it yet
        // NOTE: Using Normal priority (not AboveNormal) to reduce CPU usage
        // The circular buffer (4x size) provides enough buffering to handle priority fluctuations
        _decoderThread = new Thread(DecoderThreadProc)
        {
            Name = $"FileSource-Decoder-{Id}",
            IsBackground = true,
            Priority = ThreadPriority.Normal // Changed from AboveNormal to reduce CPU usage
        };

        // Start decoder thread only when State == Playing (lazy start in Play() method)
        // This prevents buffer pollution before actual playback
    }

    /// <inheritdoc/>
    public override int ReadSamples(Span<float> buffer, int frameCount)
    {
        ThrowIfDisposed();

        // If not playing, return silence
        if (State != AudioState.Playing)
        {
            FillWithSilence(buffer, frameCount * _streamInfo.Channels);
            return frameCount;
        }

        // NEW: Continuous drift correction (zero overhead if not synchronized)
        // Single null check - branch predictor will optimize this to ~1 cycle when null
        if (_ghostTrack != null)
        {
            // Get ghost track position and check drift
            long ghostPosition = _ghostTrack.CurrentFrame;
            long myPosition = SamplePosition;
            long drift = Math.Abs(ghostPosition - myPosition);

            // Tight tolerance: 512 frames (~10ms @ 48kHz, reduced from 100ms)
            // This ensures sample-accurate sync while avoiding excessive seeking
            if (drift > 512)
            {
                // Drift detected - resync immediately
                ResyncTo(ghostPosition);
            }
        }

        int samplesToRead = frameCount * _streamInfo.Channels;
        int samplesRead = _buffer.Read(buffer.Slice(0, samplesToRead));
        int framesRead = samplesRead / _streamInfo.Channels;

        // Update position
        if (framesRead > 0)
        {
            double frameDuration = 1.0 / _streamInfo.SampleRate;
            double newPosition;
            double currentPosition;
            do
            {
                currentPosition = Interlocked.CompareExchange(ref _currentPosition, 0, 0);
                newPosition = currentPosition + (framesRead * frameDuration);
            } while (Math.Abs(Interlocked.CompareExchange(ref _currentPosition, newPosition, currentPosition) - currentPosition) > double.Epsilon);
        }

        // Check for buffer underrun
        if (framesRead < frameCount && !_isEndOfStream)
        {
            // Fill remaining with silence
            int remainingSamples = (frameCount - framesRead) * _streamInfo.Channels;
            FillWithSilence(buffer.Slice(samplesRead), remainingSamples);

            long currentFramePosition = (long)(Position * _streamInfo.SampleRate);
            OnBufferUnderrun(new BufferUnderrunEventArgs(
                frameCount - framesRead,
                currentFramePosition));
        }

        // Apply volume
        ApplyVolume(buffer, frameCount * _streamInfo.Channels);

        // Check for end of stream
        if (_isEndOfStream && _buffer.IsEmpty)
        {
            if (Loop)
            {
                // Restart from beginning
                Seek(0);
            }
            else
            {
                State = AudioState.EndOfStream;
                return 0;
            }
        }

        return framesRead;
    }

    /// <inheritdoc/>
    public override bool Seek(double positionInSeconds)
    {
        ThrowIfDisposed();

        // Validate position
        if (positionInSeconds < 0 || positionInSeconds > Duration)
        {
            return false;
        }

        lock (_seekLock)
        {
            try
            {
                // OPTIMIZATION: If decoder thread hasn't started yet (lazy start),
                // seek immediately without waiting for thread
                if (!_decoderThread.IsAlive)
                {
                    // Direct seek on decoder (no thread involved)
                    var targetTimeSpan = TimeSpan.FromSeconds(positionInSeconds);
                    if (_decoder.TrySeek(targetTimeSpan, out string error))
                    {
                        Interlocked.Exchange(ref _currentPosition, positionInSeconds);
                        _isEndOfStream = false;

                        // Clear SoundTouch buffer
                        lock (_soundTouchLock)
                        {
                            _soundTouch.Clear();
                            _soundTouchAccumulationCount = 0;
                        }

                        return true;
                    }
                    else
                    {
                        OnError(new AudioErrorEventArgs($"Seek failed: {error}", null));
                        return false;
                    }
                }

                // Set seek request for running decoder thread
                Interlocked.Exchange(ref _seekTargetSeconds, positionInSeconds);
                _seekRequested = true;

                // Clear buffer
                _buffer.Clear();

                // Clear SoundTouch buffer to prevent stale audio
                lock (_soundTouchLock)
                {
                    _soundTouch.Clear();
                    _soundTouchAccumulationCount = 0; // Clear accumulation buffer
                }

                // Reset EOF flag
                _isEndOfStream = false;

                // Wait a bit for decoder thread to process seek (reduced from 10ms to 5ms)
                Thread.Sleep(5);

                return true;
            }
            catch (Exception ex)
            {
                OnError(new AudioErrorEventArgs($"Seek failed: {ex.Message}", ex));
                return false;
            }
        }
    }

    /// <summary>
    /// Attaches this FileSource to a GhostTrack for automatic synchronization.
    /// After attachment, this source will automatically follow the GhostTrack's
    /// state (play/pause/stop), position (seek), tempo, and pitch changes.
    /// </summary>
    /// <param name="ghostTrack">The GhostTrack to attach to.</param>
    /// <exception cref="ArgumentNullException">Thrown when ghostTrack is null.</exception>
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
    /// </summary>
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

    // ========================================
    // IGhostTrackObserver Implementation
    // ========================================

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

    /// <inheritdoc/>
    public override void Play()
    {
        ThrowIfDisposed();

        // CRITICAL: Set state to Playing BEFORE starting decoder thread
        // This allows the decoder thread to immediately start filling the buffer
        base.Play();
        _pauseEvent.Set(); // Resume decoder thread (in case it's paused)

        // Start decoder thread if not already running (lazy start)
        if (!_decoderThread.IsAlive)
        {
            _decoderThread.Start();

            // Pre-buffer some data ONLY on first Play() to ensure clean start
            // Wait for buffer to reach at least 25% capacity
            int bufferSizeInSamples = _bufferSizeInFrames * _streamInfo.Channels * 4;
            int minBufferLevel = bufferSizeInSamples / 4;
            int waitCount = 0;
            while (_buffer.Available < minBufferLevel && waitCount < 50) // Max 50ms wait (reduced from 100ms)
            {
                Thread.Sleep(1);
                waitCount++;
            }
        }
    }

    /// <inheritdoc/>
    public override void Pause()
    {
        ThrowIfDisposed();
        base.Pause();
        _pauseEvent.Reset(); // Pause decoder thread
    }

    /// <inheritdoc/>
    public override void Stop()
    {
        ThrowIfDisposed();
        base.Stop();
        _pauseEvent.Reset(); // Pause decoder thread
        _buffer.Clear();
    }

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
                // Wait if paused
                if (State != AudioState.Playing)
                {
                    _pauseEvent.Wait(100);
                    continue;
                }

                // Handle seek request
                if (_seekRequested)
                {
                    lock (_seekLock)
                    {
                        double targetSeconds = Interlocked.CompareExchange(ref _seekTargetSeconds, 0, 0);
                        var targetTimeSpan = TimeSpan.FromSeconds(targetSeconds);

                        if (_decoder.TrySeek(targetTimeSpan, out string error))
                        {
                            Interlocked.Exchange(ref _currentPosition, targetSeconds);
                            _isEndOfStream = false;
                        }
                        else
                        {
                            OnError(new AudioErrorEventArgs($"Seek failed: {error}", null));
                        }
                        _seekRequested = false;
                    }
                    continue;
                }

                // Check if buffer needs filling (keep buffer at least 75% full for better resilience)
                // Using 75% instead of 50% provides more headroom for temporary decoder delays
                int targetFillLevel = (_buffer.Capacity * 3) / 4;
                if (_buffer.Available >= targetFillLevel)
                {
                    // Buffer is full enough, sleep briefly (1ms like Ownaudio)
                    Thread.Sleep(1);
                    continue;
                }

                // Decode next frame using Ownaudio.Core decoder
                var result = _decoder.DecodeNextFrame();

                if (result.IsEOF)
                {
                    // End of file reached
                    _isEndOfStream = true;

                    // Flush SoundTouch to get any remaining processed samples
                    lock (_soundTouchLock)
                    {
                        if (_soundTouch.IsProcessingNeeded())
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
                                _soundTouchAccumulationCount = 0; // Clear accumulation buffer
                            }
                        }
                        else
                        {
                            OnError(new AudioErrorEventArgs($"Loop seek failed: {error}", null));
                            break;
                        }
                    }
                    else
                    {
                        // Wait for buffer to drain
                        while (!_shouldStop && _buffer.Available > 0)
                        {
                            Thread.Sleep(10);
                        }
                        break;
                    }
                }
                else if (result.IsSucceeded && result.Frame != null && result.Frame.Data != null)
                {
                    // Convert byte[] to float[] (AudioFrame.Data is byte[])
                    var floatSpan = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(result.Frame.Data.AsSpan());
                    int frameCount = floatSpan.Length / _streamInfo.Channels;

                    // Check if SoundTouch processing is needed (cache check without lock for performance)
                    bool needsSoundTouch = Math.Abs(_tempo - 1.0f) > 0.001f || Math.Abs(_pitchShift) > 0.001f;

                    if (needsSoundTouch)
                    {
                        // Process through SoundTouch
                        ProcessWithSoundTouch(floatSpan, frameCount);
                    }
                    else
                    {
                        // Write directly to circular buffer (no processing)
                        int samplesWritten = _buffer.Write(floatSpan);

                        // Check if buffer is full (write failed)
                        if (samplesWritten < floatSpan.Length)
                        {
                            // Buffer full, wait briefly (1ms like Ownaudio)
                            Thread.Sleep(1);
                        }
                    }
                }
                else if (!result.IsSucceeded)
                {
                    // Decode error
                    OnError(new AudioErrorEventArgs($"Decode error: {result.ErrorMessage}", null));
                    _isEndOfStream = true;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            // Report error to main thread
            OnError(new AudioErrorEventArgs($"Decoder thread error: {ex.Message}", ex));
        }
    }

    /// <summary>
    /// Processes audio samples through SoundTouch for pitch/tempo effects.
    /// Uses accumulation buffer pattern (like working code) to ensure stable timing.
    /// </summary>
    /// <param name="samples">Input audio samples (interleaved if stereo).</param>
    /// <param name="frameCount">Number of input frames.</param>
    private void ProcessWithSoundTouch(ReadOnlySpan<float> samples, int frameCount)
    {
        lock (_soundTouchLock)
        {
            try
            {
                // Ensure input buffer is large enough
                // NOTE: This should rarely happen with 4x pre-allocation
                int requiredSize = samples.Length;
                if (_soundTouchInputBuffer.Length < requiredSize)
                {
                    // WARNING: Allocation detected - this indicates buffer was undersized
                    // In production, log this via ILogger for monitoring
                    // This is a rare event and only happens during initialization/format changes
                    _soundTouchInputBuffer = new float[requiredSize * 2]; // 2x for growth
                }

                // Copy to pre-allocated buffer (avoid ToArray allocation)
                samples.CopyTo(_soundTouchInputBuffer.AsSpan(0, samples.Length));

                // Put samples into SoundTouch
                _soundTouch.PutSamples(_soundTouchInputBuffer.AsSpan(0, samples.Length), frameCount);

                // Receive processed samples and accumulate them (just ONE ReceiveSamples like working code)
                // CRITICAL: Don't loop here! Just receive what's available and accumulate
                int maxFrames = _soundTouchOutputBuffer.Length / _streamInfo.Channels;
                int framesReceived = _soundTouch.ReceiveSamples(_soundTouchOutputBuffer, maxFrames);

                if (framesReceived > 0)
                {
                    // Add to accumulation buffer (like working code's AddToSoundTouchBuffer line 377)
                    int samplesToAdd = framesReceived * _streamInfo.Channels;
                    AddToSoundTouchAccumulationBuffer(_soundTouchOutputBuffer.AsSpan(0, samplesToAdd));
                }

                // Now try to write accumulated samples if we have enough (like working code line 333)
                // This is the KEY difference: we check and write in separate step
                int samplesPerFrame = _bufferSizeInFrames * _streamInfo.Channels;
                if (_soundTouchAccumulationCount >= samplesPerFrame)
                {
                    // Try to write ONE chunk at a time (not a loop!)
                    int samplesWritten = _buffer.Write(_soundTouchAccumulationBuffer.AsSpan(0, samplesPerFrame));

                    if (samplesWritten == samplesPerFrame)
                    {
                        // Successfully wrote, shift remaining samples
                        int remainingSamples = _soundTouchAccumulationCount - samplesPerFrame;
                        if (remainingSamples > 0)
                        {
                            _soundTouchAccumulationBuffer.AsSpan(samplesPerFrame, remainingSamples)
                                .CopyTo(_soundTouchAccumulationBuffer.AsSpan(0, remainingSamples));
                        }
                        _soundTouchAccumulationCount = remainingSamples;
                    }
                    // If write failed (buffer full), just keep accumulating and try next iteration
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
        // Ensure accumulation buffer has enough space
        // NOTE: With 8x pre-allocation, this should rarely trigger
        int requiredCapacity = _soundTouchAccumulationCount + samples.Length;
        if (requiredCapacity > _soundTouchAccumulationBuffer.Length)
        {
            // WARNING: Accumulation buffer overflow - this indicates extreme tempo changes
            // In production, log this via ILogger for monitoring
            // This is a rare event (e.g., 2x tempo on very long buffer hold)
            int newCapacity = Math.Max(_soundTouchAccumulationBuffer.Length * 2, requiredCapacity);
            var newBuffer = new float[newCapacity];

            if (_soundTouchAccumulationCount > 0)
            {
                _soundTouchAccumulationBuffer.AsSpan(0, _soundTouchAccumulationCount).CopyTo(newBuffer);
            }

            _soundTouchAccumulationBuffer = newBuffer;
        }

        // Add samples to accumulation buffer (zero-allocation)
        samples.CopyTo(_soundTouchAccumulationBuffer.AsSpan(_soundTouchAccumulationCount, samples.Length));
        _soundTouchAccumulationCount += samples.Length;
    }

    /// <summary>
    /// Releases the unmanaged resources used by the FileSource and optionally releases the managed resources.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                // IMPORTANT: Call base.Dispose() FIRST to ensure Stop() is called
                // while _pauseEvent is still alive (Stop() uses _pauseEvent.Reset())
                // We need to mark as disposed before calling base to prevent recursion
                _disposed = true;
                base.Dispose(disposing);

                // Signal decoder thread to stop
                _shouldStop = true;
                _pauseEvent.Set(); // Wake up thread if waiting

                // Wait for decoder thread to exit (with timeout)
                if (_decoderThread.IsAlive)
                {
                    if (!_decoderThread.Join(TimeSpan.FromSeconds(2)))
                    {
                        // Thread didn't exit in time, force abort (not ideal but necessary)
                        try
                        {
                            _decoderThread.Interrupt();
                        }
                        catch
                        {
                            // Ignore interrupt errors
                        }
                    }
                }

                // Detach from GhostTrack
                DetachFromGhostTrack();

                // Dispose managed resources (now safe to dispose _pauseEvent)
                _pauseEvent?.Dispose();
                _decoder?.Dispose();
                _soundTouch?.Dispose();
            }
            else
            {
                _disposed = true;
                base.Dispose(disposing);
            }
        }
    }

    // ISynchronizable is already implemented in BaseAudioSource
    // IGhostTrackObserver callbacks are implemented above
}
