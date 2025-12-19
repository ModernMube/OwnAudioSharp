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
    private bool _wasUsingSoundTouch = false; // Tracks if SoundTouch was active in previous frame

    private volatile bool _shouldStop;
    private volatile bool _seekRequested;
    private double _seekTargetSeconds;
    private double _currentPosition;
    private volatile bool _isEndOfStream;
    private float _tempo = 1.0f;
    private float _pitchShift = 0.0f;
    private bool _disposed;

    // ZERO-ALLOC: Reusable buffer for the decoder.
    private readonly byte[] _decodeBuffer = null!;

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
        
        _decoder = AudioDecoderFactory.Create(filePath, targetSampleRate, targetChannels);
        _streamInfo = _decoder.StreamInfo;

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

        // ZERO-ALLOC: Pre-allocate a reusable buffer for the decoder.
        // The decoder will write directly into this buffer instead of allocating a new byte[] on every frame.
        _decodeBuffer = new byte[bufferSizeInFrames * _streamInfo.Channels * sizeof(float)];

        // Initialize synchronization primitives
        _pauseEvent = new ManualResetEventSlim(false);
        _shouldStop = false;
        _seekRequested = false;
        _currentPosition = 0.0;
        _isEndOfStream = false;

        // Create decoder thread but DON'T start it yet
        _decoderThread = new Thread(DecoderThreadProc)
        {
            Name = $"FileSource-Decoder-{Id}",
            IsBackground = true,
            Priority = ThreadPriority.Normal // Changed from AboveNormal to reduce CPU usage
        };
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
        
        if (_ghostTrack != null)
        {
            if (_buffer.Available > 0)
            {
                // Get ghost track position and check drift
                long ghostPosition = _ghostTrack.CurrentFrame;
                long myPosition = SamplePosition;
                long drift = Math.Abs(ghostPosition - myPosition);

                if (drift > 512)
                {
                    // Drift detected - resync immediately
                    ResyncTo(ghostPosition);
                }
            }
        }

        int samplesToRead = frameCount * _streamInfo.Channels;
        int samplesRead = _buffer.Read(buffer.Slice(0, samplesToRead));
        int framesRead = samplesRead / _streamInfo.Channels;

        // Update position
        if (framesRead > 0)
        {
            int sourceFramesAdvanced = (int)(framesRead * _tempo);
            UpdateSamplePosition(sourceFramesAdvanced);

            double frameDuration = 1.0 / _streamInfo.SampleRate;
            double newPosition;
            double currentPosition;
            do
            {
                currentPosition = Interlocked.CompareExchange(ref _currentPosition, 0, 0);
                // For double position, we also need to account for effective speed through the file
                newPosition = currentPosition + (framesRead * frameDuration * _tempo);
            } while (Math.Abs(Interlocked.CompareExchange(ref _currentPosition, newPosition, currentPosition) - currentPosition) > double.Epsilon);
        }

        // Check for buffer underrun
        if (framesRead < frameCount && !_isEndOfStream)
        {
            // Fill remaining with silence
            int remainingSamples = (frameCount - framesRead) * _streamInfo.Channels;
            FillWithSilence(buffer.Slice(samplesRead), remainingSamples);

            int silenceFrames = frameCount - framesRead;
            // Scale by Tempo here as well
            int silenceSourceFrames = (int)(silenceFrames * _tempo);
            UpdateSamplePosition(silenceSourceFrames);
            
            double frameDuration = 1.0 / _streamInfo.SampleRate;
            double silenceSeconds = silenceFrames * frameDuration * _tempo;
            double newPos, curPos;
            do
            {
                curPos = Interlocked.CompareExchange(ref _currentPosition, 0, 0);
                newPos = curPos + silenceSeconds;
            } while (Math.Abs(Interlocked.CompareExchange(ref _currentPosition, newPos, curPos) - curPos) > double.Epsilon);

            long currentFramePosition = (long)(Position * _streamInfo.SampleRate);
            OnBufferUnderrun(new BufferUnderrunEventArgs(
                frameCount - framesRead,
                currentFramePosition));
            
            return frameCount;
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
                if (!_decoderThread.IsAlive)
                {
                    // Direct seek on decoder (no thread involved)
                    var targetTimeSpan = TimeSpan.FromSeconds(positionInSeconds);
                    if (_decoder.TrySeek(targetTimeSpan, out string error))
                    {
                        Interlocked.Exchange(ref _currentPosition, positionInSeconds);
                        SetSamplePosition((long)(positionInSeconds * _streamInfo.SampleRate));
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
        
        base.Play();
        _pauseEvent.Set(); // Resume decoder thread (in case it's paused)

        // Start decoder thread if not already running (lazy start)
        if (!_decoderThread.IsAlive)
        {
            _decoderThread.Start();
            
            int bufferSizeInSamples = _bufferSizeInFrames * _streamInfo.Channels * 4;
            int minBufferLevel = bufferSizeInSamples / 4;
            int waitCount = 0;
            while (_buffer.Available < minBufferLevel && waitCount < 1000) // Max 1000ms wait
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
                            // IMPORTANT: Sync SamplePosition with new time position
                            SetSamplePosition((long)(targetSeconds * _streamInfo.SampleRate));
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

                            // ZERO-ALLOC: Decode into the reusable buffer instead of allocating a new frame.
                            var readResult = _decoder.ReadFrames(_decodeBuffer);
                
                            if (readResult.IsEOF)
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
                            else if (readResult.IsSucceeded && readResult.FramesRead > 0)
                            {
                                int bytesRead = readResult.FramesRead * _streamInfo.Channels * sizeof(float);
                                var floatSpan = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(_decodeBuffer.AsSpan(0, bytesRead));
                                int frameCount = readResult.FramesRead;
                
                                // Check if SoundTouch processing is needed (cache check without lock for performance)
                                bool needsSoundTouch = Math.Abs(_tempo - 1.0f) > 0.001f || Math.Abs(_pitchShift) > 0.001f;

                                if (_wasUsingSoundTouch && !needsSoundTouch)
                                {
                                    lock (_soundTouchLock)
                                    {
                                        _soundTouch.Flush();
                                        
                                        // Drain SoundTouch completely
                                        while (true)
                                        {
                                            int maxFrames = _soundTouchOutputBuffer.Length / _streamInfo.Channels;
                                            int framesReceived = _soundTouch.ReceiveSamples(_soundTouchOutputBuffer, maxFrames);
                                            if (framesReceived == 0) break;
                                            
                                            AddToSoundTouchAccumulationBuffer(_soundTouchOutputBuffer.AsSpan(0, framesReceived * _streamInfo.Channels));
                                        }

                                        // Write remaining accumulation to main buffer
                                        if (_soundTouchAccumulationCount > 0)
                                        {
                                            // Write directly, retry if buffer full
                                            int samplesToWrite = _soundTouchAccumulationCount;
                                            int offset = 0;
                                            while (samplesToWrite > 0 && !_shouldStop)
                                            {
                                                int written = _buffer.Write(_soundTouchAccumulationBuffer.AsSpan(offset, samplesToWrite));
                                                samplesToWrite -= written;
                                                offset += written;
                                                if (samplesToWrite > 0) Thread.Sleep(1);
                                            }
                                            _soundTouchAccumulationCount = 0;
                                        }
                                        
                                        _soundTouch.Clear();
                                    }
                                }
                                
                                _wasUsingSoundTouch = needsSoundTouch;

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
                            else if (!readResult.IsSucceeded)
                            {
                                // Decode error
                                OnError(new AudioErrorEventArgs($"Decode error: {readResult.ErrorMessage}", null));
                                _isEndOfStream = true;
                                break;
                            }            }
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
                int requiredSize = samples.Length;
                if (_soundTouchInputBuffer.Length < requiredSize)
                {
                    _soundTouchInputBuffer = new float[requiredSize * 2]; // 2x for growth
                }

                // Copy to pre-allocated buffer (avoid ToArray allocation)
                samples.CopyTo(_soundTouchInputBuffer.AsSpan(0, samples.Length));

                // Put samples into SoundTouch
                _soundTouch.PutSamples(_soundTouchInputBuffer.AsSpan(0, samples.Length), frameCount);

                int maxFrames = _soundTouchOutputBuffer.Length / _streamInfo.Channels;
                int framesReceived = _soundTouch.ReceiveSamples(_soundTouchOutputBuffer, maxFrames);

                if (framesReceived > 0)
                {
                    // Add to accumulation buffer (like working code's AddToSoundTouchBuffer line 377)
                    int samplesToAdd = framesReceived * _streamInfo.Channels;
                    AddToSoundTouchAccumulationBuffer(_soundTouchOutputBuffer.AsSpan(0, samplesToAdd));
                }

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

    /// <inheritdoc/>
    public override void ResyncTo(long targetSamplePosition)
    {
        long currentPosition = SamplePosition;
        long driftFrames = targetSamplePosition - currentPosition; // Positive = we are behind (need to skip forward)

        // Tolerance check (same as in ReadSamples, ~10ms)
        if (Math.Abs(driftFrames) < 512) return;

        // CASE 1: We are BEHIND (GhostTrack is ahead) -> Skip samples
        if (driftFrames > 0)
        {
            int samplesToSkip = (int)(driftFrames * _streamInfo.Channels);

            if (samplesToSkip < _buffer.Available)
            {
                _buffer.Skip(samplesToSkip);

                SetSamplePosition(targetSamplePosition);
                double newPositionSec = (double)targetSamplePosition / _streamInfo.SampleRate;
                Interlocked.Exchange(ref _currentPosition, newPositionSec);

                return; // Soft sync successful!
            }
        }

        base.ResyncTo(targetSamplePosition);
    }
}
