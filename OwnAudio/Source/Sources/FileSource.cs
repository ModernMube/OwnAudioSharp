using Ownaudio;
using Ownaudio.Core;
using Ownaudio.Decoders;
using Ownaudio.Synchronization;
using OwnaudioNET.BufferManagement;
using OwnaudioNET.Core;
using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Processing;
using OwnaudioNET.Synchronization;
//using System.Diagnostics;

namespace OwnaudioNET.Sources;

/// <summary>
/// Audio source that plays audio from a file with advanced features:
/// - Background decoding thread for smooth playback
/// - Circular buffer for zero-copy audio streaming
/// - SoundTouch integration for real-time pitch/tempo control
/// - Master clock synchronization for multi-track alignment
/// - Soft sync system for gradual drift correction
/// </summary>
public partial class FileSource : BaseAudioSource, ISynchronizable, IGhostTrackObserver, IMasterClockSource
{
    #region Fields

    // Core components
    private readonly IAudioDecoder _decoder;
    private readonly CircularBuffer _buffer;
    private Thread _decoderThread;
    private readonly object _seekLock = new();
    private readonly object _soundTouchLock = new();
    private readonly ManualResetEventSlim _pauseEvent;
    private readonly int _bufferSizeInFrames;
    private readonly AudioConfig _config;
    private readonly AudioStreamInfo _streamInfo;
    private readonly SoundTouchProcessor _soundTouch;
    private readonly float[] _soundTouchOutputBuffer;
    private float[] _soundTouchInputBuffer;
    private float[] _soundTouchAccumulationBuffer;
    private int _soundTouchAccumulationCount;
    private bool _wasSoundTouchProcessing = false;

    // Playback state
    private volatile bool _shouldStop;
    private volatile bool _seekRequested;
    private double _seekTargetSeconds;
    private double _currentPosition;
    private volatile bool _isEndOfStream;
    private float _tempo = 1.0f;
    private float _pitchShift = 0.0f;
    private bool _disposed;
    private volatile bool _isPreBuffering = false;

    // Synchronization state
    private double _gracePeriodEndTime = 0.0;
    private int _seekCount = 0;
    private double _lastSeekTime = 0.0;
    private const int MaxSeeksPerWindow = 10;
    private const double SeekWindowSeconds = 5.0;
    private const double GracePeriodSeconds = 1.0;
    private const double InitialGracePeriodSeconds = 0.5;

    // Buffers
    private readonly byte[] _decodeBuffer = null!;

    // Legacy synchronization (deprecated)
    private GhostTrackSource? _ghostTrack = null;

    // Master clock synchronization
    private MasterClock? _masterClock = null;
    private double _startOffset = 0.0;
    private double _trackLocalTime = 0.0;
    private double _fractionalFrameAccumulator = 0.0;

    // Input-driven timing tracking
    private long _totalSamplesProcessedFromFile = 0;
    private readonly object _timingLock = new();
    private bool _isSoftSyncActive = false;  // Track if soft sync is currently active

    #endregion

    #region Properties

    /// <summary>
    /// Gets or sets the synchronization tolerance in seconds (Green Zone threshold).
    /// Default is 0.010 (10ms).
    /// Drift below this value requires no correction.
    /// </summary>
    public double SyncTolerance { get; set; } = 0.010;

    /// <summary>
    /// Soft sync tolerance in seconds (Yellow Zone threshold).
    /// Drift between SyncTolerance and SoftSyncTolerance triggers tempo adjustment.
    /// Default is 0.150 (150ms) - increased from 50ms to reduce hard seek sensitivity with 22+ tracks.
    /// </summary>
    public double SoftSyncTolerance { get; set; } = 0.150;

    /// <summary>
    /// Maximum tempo adjustment percentage for soft sync.
    /// Default is 0.02 (2%).
    /// </summary>
    public double SoftSyncMaxTempoAdjustment { get; set; } = 0.02;

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

    // ========================================
    // IMasterClockSource Implementation (NEW - v2.4.0+)
    // ========================================

    /// <inheritdoc/>
    public double StartOffset
    {
        get => _startOffset;
        set => _startOffset = value;
    }

    /// <inheritdoc/>
    public bool IsAttachedToClock => _masterClock != null;

    #endregion

    #region Tempo and Pitch Control

    /// <summary>
    /// Gets or sets the playback tempo multiplier (1.0 = normal speed).
    /// Valid range: 0.5x to 2.0x (converted to -50% to +100% for SoundTouch).
    /// This property clears the SoundTouch buffer on change - use SetTempoSmooth() for slider adjustments.
    /// </summary>
    public override float Tempo
    {
        get => _tempo;
        set => SetTempoInternal(value, clearBuffer: true, setGracePeriod: true);
    }

    /// <summary>
    /// Sets tempo smoothly without clearing buffers - ideal for real-time slider adjustments.
    /// Use this for continuous tempo changes to avoid audio glitches with many tracks.
    /// </summary>
    /// <param name="tempo">The tempo multiplier (0.5x to 2.0x).</param>
    public void SetTempoSmooth(float tempo)
    {
        SetTempoInternal(tempo, clearBuffer: false, setGracePeriod: false);
    }

    /// <summary>
    /// Internal method to set tempo with configurable buffer clearing and grace period.
    /// </summary>
    /// <param name="value">The tempo multiplier.</param>
    /// <param name="clearBuffer">Whether to clear SoundTouch buffer (true for reset, false for smooth slider).</param>
    /// <param name="setGracePeriod">Whether to set sync grace period (true for reset, false for smooth slider).</param>
    private void SetTempoInternal(float value, bool clearBuffer, bool setGracePeriod)
    {
        // Clamp to 0.5x to 2.0x range
        float clamped = Math.Clamp(value, 0.5f, 2.0f);
        if (Math.Abs(_tempo - clamped) < 0.001f)
            return;

        _tempo = clamped;

        lock (_soundTouchLock)
        {
            // Convert multiplier to percentage for SoundTouch
            float tempoChangePercent = (_tempo - 1.0f) * 100.0f;
            _soundTouch.TempoChange = tempoChangePercent;

            if (clearBuffer)
            {
                // Clear SoundTouch internal buffer to remove old samples
                _soundTouch.Clear();
                // Clear accumulation buffer
                _soundTouchAccumulationCount = 0; 
            }
        }

        if (setGracePeriod)
        {
            // Trigger grace period to prevent chaotic resync
            _gracePeriodEndTime = _trackLocalTime + GracePeriodSeconds;
        }
    }

    /// <summary>
    /// Gets or sets the pitch shift in semitones (0 = no shift).
    /// Valid range: -12 to +12 semitones (1 octave down to 1 octave up).
    /// This property clears the SoundTouch buffer on change - use SetPitchSmooth() for slider adjustments.
    /// </summary>
    public override float PitchShift
    {
        get => _pitchShift;
        set => SetPitchInternal(value, clearBuffer: true, setGracePeriod: true);
    }

    /// <summary>
    /// Sets pitch smoothly without clearing buffers - ideal for real-time slider adjustments.
    /// Use this for continuous pitch changes to avoid audio glitches with many tracks.
    /// </summary>
    /// <param name="pitchSemitones">The pitch shift in semitones (-12 to +12).</param>
    public void SetPitchSmooth(float pitchSemitones)
    {
        SetPitchInternal(pitchSemitones, clearBuffer: false, setGracePeriod: false);
    }

    /// <summary>
    /// Internal method to set pitch with configurable buffer clearing and grace period.
    /// </summary>
    /// <param name="value">The pitch shift in semitones.</param>
    /// <param name="clearBuffer">Whether to clear SoundTouch buffer (true for reset, false for smooth slider).</param>
    /// <param name="setGracePeriod">Whether to set sync grace period (true for reset, false for smooth slider).</param>
    private void SetPitchInternal(float value, bool clearBuffer, bool setGracePeriod)
    {
        // Clamp to -12 to +12 semitones
        float clamped = Math.Clamp(value, -12.0f, 12.0f);
        if (Math.Abs(_pitchShift - clamped) < 0.001f)
            return;

        _pitchShift = clamped;

        lock (_soundTouchLock)
        {
            _soundTouch.PitchSemiTones = _pitchShift;

            if (clearBuffer)
            {
                // Clear SoundTouch internal buffer to remove old pitch-processed samples
                _soundTouch.Clear();
                // Clear accumulation buffer
                _soundTouchAccumulationCount = 0; 
            }
        }

        if (setGracePeriod)
        {
            // Trigger grace period to prevent chaotic resync
            _gracePeriodEndTime = _trackLocalTime + GracePeriodSeconds;
        }
    }

    #endregion

    #region Constructor


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
            SampleRate = _streamInfo.SampleRate,
            Channels = _streamInfo.Channels,
            BufferSize = bufferSizeInFrames
        };

        // Initialize circular buffer with 4x size for better buffering
        int bufferSizeInSamples = bufferSizeInFrames * _streamInfo.Channels * 4;
        _buffer = new CircularBuffer(bufferSizeInSamples);

        // Initialize SoundTouch processor
        _soundTouch = new SoundTouchProcessor(_streamInfo.SampleRate, _streamInfo.Channels);
        _soundTouchOutputBuffer = new float[bufferSizeInFrames * _streamInfo.Channels * 2];

        // Pre-allocate input buffer with generous headroom to prevent GC
        // Worst case: tempo=0.5x means we need 2x the input buffer size
        _soundTouchInputBuffer = new float[bufferSizeInFrames * _streamInfo.Channels * 8];

        // Accumulation buffer with 16x size to handle worst-case tempo changes without reallocation
        // This prevents GC in the hot path which causes sync chaos every 30-40 seconds
        _soundTouchAccumulationBuffer = new float[bufferSizeInFrames * _streamInfo.Channels * 16];
        _soundTouchAccumulationCount = 0;

        // ZERO-ALLOC: Pre-allocate a reusable buffer for the decoder
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
            Priority = ThreadPriority.AboveNormal  // Boosted from Normal to reduce priority inversion with MixThread
        };
    }

    #endregion

    #region Core Playback Methods

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

        // If attached to MasterClock, delegate to ReadSamplesAtTime()
        if (_masterClock != null)
        {
            bool success = ReadSamplesAtTime(
                _masterClock.CurrentTimestamp,
                buffer,
                frameCount,
                out ReadResult result);

            return result.FramesRead;
        }

        // If attached to GhostTrack, use legacy sync logic
        if (_ghostTrack != null)
        {
            if (_buffer.Available > 0)
            {
                // Get ghost track position and check drift
                long ghostPosition = _ghostTrack.CurrentFrame;
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
        }

        // Standalone mode (no sync)

        int samplesToRead = frameCount * _streamInfo.Channels;
        int samplesRead = _buffer.Read(buffer.Slice(0, samplesToRead));
        int framesRead = samplesRead / _streamInfo.Channels;

        // Update position
        if (framesRead > 0)
        {
            // ACCUMULATE FRACTIONAL FRAMES
            double exactSourceFrames = framesRead * _tempo;
            _fractionalFrameAccumulator += exactSourceFrames;
            int sourceFramesAdvanced = (int)_fractionalFrameAccumulator;
            _fractionalFrameAccumulator -= sourceFramesAdvanced;

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
            
            // ACCUMULATE FRACTIONAL FRAMES for silence
            double exactSilenceFrames = silenceFrames * _tempo;
            _fractionalFrameAccumulator += exactSilenceFrames;
            int silenceSourceFrames = (int)_fractionalFrameAccumulator;
            _fractionalFrameAccumulator -= silenceSourceFrames;

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
                    // Direct seek on decoder
                    var targetTimeSpan = TimeSpan.FromSeconds(positionInSeconds);
                    if (_decoder.TrySeek(targetTimeSpan, out string error))
                    {
                        Interlocked.Exchange(ref _currentPosition, positionInSeconds);
                        SetSamplePosition((long)(positionInSeconds * _streamInfo.SampleRate));
                        _isEndOfStream = false;

                        // Clear circular buffer to remove stale data
                        _buffer.Clear();

                        // Clear SoundTouch buffer
                        lock (_soundTouchLock)
                        {
                            _soundTouch.Clear();
                            _soundTouchAccumulationCount = 0;
                        }

                        // Reset input-driven timing counter
                        lock (_timingLock)
                        {
                            _totalSamplesProcessedFromFile = (long)(positionInSeconds * _streamInfo.SampleRate);
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
                    // Clear accumulation buffer
                    _soundTouchAccumulationCount = 0; 
                    // Reset transition tracking after Seek
                    _wasSoundTouchProcessing = false; 
                }

                // Reset input-driven timing counter
                lock (_timingLock)
                {
                    _totalSamplesProcessedFromFile = (long)(positionInSeconds * _streamInfo.SampleRate);
                }

                // Reset EOF flag
                _isEndOfStream = false;

                return true;
            }
            catch (Exception ex)
            {
                OnError(new AudioErrorEventArgs($"Seek failed: {ex.Message}", ex));
                return false;
            }
        }
    }

    #endregion

    #region Synchronization - GhostTrack (Deprecated)

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

    #endregion

    #region Synchronization - MasterClock

    // ========================================
    // IMasterClockSource Methods (NEW - v2.4.0+)
    // ========================================

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
        _gracePeriodEndTime = currentClockTime + InitialGracePeriodSeconds;

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
    /// </summary>
    /// <param name="drift">The absolute drift value in seconds.</param>
    /// <param name="targetTrackTime">The target track time we should be at.</param>
    private void ApplySoftSync(double drift, double targetTrackTime)
    {
        lock (_soundTouchLock)
        {
            // Calculate tempo adjustment based on drift magnitude
            // Scale linearly from 0% at SyncTolerance to MaxTempoAdjustment at SoftSyncTolerance
            double driftRange = SoftSyncTolerance - SyncTolerance;
            double driftInRange = drift - SyncTolerance;
            double adjustmentFactor = Math.Min(driftInRange / driftRange, 1.0);
            double adjustment = adjustmentFactor * SoftSyncMaxTempoAdjustment;

            // Determine direction: are we behind or ahead?
            if (targetTrackTime > _trackLocalTime)
            {
                // We're behind - speed up slightly
                double newTempoChange = (_tempo - 1.0f) * 100.0f + (adjustment * 100.0f);
                _soundTouch.TempoChange = (float)newTempoChange;
            }
            else
            {
                // We're ahead - slow down slightly
                double newTempoChange = (_tempo - 1.0f) * 100.0f - (adjustment * 100.0f);
                _soundTouch.TempoChange = (float)newTempoChange;
            }

            #if DEBUG
            Console.WriteLine($"[SoftSync] Drift={drift:F4}s, Adjustment={adjustment:F4} ({(targetTrackTime > _trackLocalTime ? "speed up" : "slow down")})");
            #endif
        }
    }

    /// <summary>
    /// Resets soft sync by restoring the original tempo setting.
    /// Called when drift returns to Green Zone.
    /// </summary>
    private void ResetSoftSync()
    {
        lock (_soundTouchLock)
        {
            // Restore original tempo
            double originalTempoChange = (_tempo - 1.0f) * 100.0f;
            _soundTouch.TempoChange = (float)originalTempoChange;
        }
    }

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
                // RED ZONE: Hard sync required (seek)
                // Reset soft sync before seeking
                if (_isSoftSyncActive)
                {
                    ResetSoftSync();
                    _isSoftSyncActive = false;
                }

                // Check if we're Seeking too frequently
                double timeSinceLastSeek = targetTrackTime - _lastSeekTime;

                if (timeSinceLastSeek > SeekWindowSeconds)
                {
                    // Reset counter if outside the window
                    _seekCount = 0;
                    _lastSeekTime = targetTrackTime;
                }

                _seekCount++;

                if (_seekCount > MaxSeeksPerWindow)
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

                // Perform seek to resync
                double filePosition = targetTrackTime * _tempo;

                #if DEBUG
                Console.WriteLine($"[RedZone] Drift={drift:F4}s - Hard sync (seek to {filePosition:F4}s)");
                #endif

                if (!Seek(filePosition))
                {
                    // Seek failed - fill with silence and report failure
                    FillWithSilence(buffer, frameCount * _streamInfo.Channels);
                    result = ReadResult.CreateFailure(0, "Seek failed during drift correction");
                    return false;
                }

                // Set grace period to prevent immediate re-seek
                _gracePeriodEndTime = targetTrackTime + GracePeriodSeconds;

                // Force sync to target time immediately
                _trackLocalTime = targetTrackTime;

                // Return silence immediately as SUCCESS to prevent cascade
                FillWithSilence(buffer, frameCount * _streamInfo.Channels);
                // SUCCESS - intentional silence!
                result = ReadResult.CreateSuccess(frameCount);
                return true;
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

    #endregion

    #region Playback Control

    /// <inheritdoc/>
    public override void Play()
    {
        ThrowIfDisposed();

        // Signal decoder to start pre-buffering
        _isPreBuffering = true;
        // Resume decoder thread
        _pauseEvent.Set(); 

        // Recreate decoder thread if it's terminated
        if (!_decoderThread.IsAlive)
        {
            // Check if thread was previously started
            if (_decoderThread.ThreadState != ThreadState.Unstarted)
            {
                // Thread was started before and is now terminated - recreate it
                _decoderThread = new Thread(DecoderThreadProc)
                {
                    Name = $"FileSource-Decoder-{Id}",
                    IsBackground = true,
                    Priority = ThreadPriority.Normal
                };

                // Reset state for fresh playback
                _shouldStop = false;
                _isEndOfStream = false;

                // Reset Seek counter for fresh playback
                _seekCount = 0;
                _lastSeekTime = 0.0;

                // If we ended due to EOF, seek back to beginning
                if (Position >= Duration)
                {
                    _decoder.TrySeek(TimeSpan.Zero, out _);
                    Interlocked.Exchange(ref _currentPosition, 0.0);
                    SetSamplePosition(0);
                    _trackLocalTime = 0.0;

                    // Clear buffers
                    _buffer.Clear();
                    lock (_soundTouchLock)
                    {
                        _soundTouch.Clear();
                        _soundTouchAccumulationCount = 0;
                        // Reset transition tracking
                        _wasSoundTouchProcessing = false; 
                    }
                }
            }
            else
            {
                // First time starting
                _seekCount = 0;
                _lastSeekTime = 0.0;
            }

            _decoderThread.Start();
        }

        // Wait for buffer to fill
        int bufferSizeInSamples = _bufferSizeInFrames * _streamInfo.Channels * 4;
        // Dynamic buffer sizing: higher target when SoundTouch is active
        bool isSoundTouchActive = _soundTouch.IsProcessingNeeded();
        int minBufferLevel = isSoundTouchActive 
            ? (bufferSizeInSamples * 3) / 4  // 75% for SoundTouch
            : bufferSizeInSamples / 2;        // 50% for direct playback
        int waitCount = 0;

        // Reduced timeout from 1000ms to 500ms
        while (_buffer.Available < minBufferLevel && waitCount < 500) 
        {
            Thread.Sleep(1);
            waitCount++;
        }

        _isPreBuffering = false;

        base.Play();
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

    #endregion

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
                if (State != AudioState.Playing && !_isPreBuffering)
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
                    continue;
                }

                // Check if buffer needs filling
                // Dynamic buffer sizing: higher target when SoundTouch is active
                bool isSoundTouchActive = _soundTouch.IsProcessingNeeded();
                int targetFillLevel = isSoundTouchActive
                    ? (_buffer.Capacity * 7) / 8     // 87.5% for SoundTouch
                    : (_buffer.Capacity * 3) / 4;    // 75% for direct playback
                if (_buffer.Available >= targetFillLevel)
                {
                    // Buffer is full enough - sleep briefly
                    // Note: Thread.Sleep(1) timing varies by platform but is acceptable here
                    Thread.Sleep(1);
                    continue;
                }

                // ZERO-ALLOC: Decode into the reusable buffer
                var readResult = _decoder.ReadFrames(_decodeBuffer);

                if (readResult.IsEOF)
                {
                    // End of file reached
                    _isEndOfStream = true;

                    // Only flush SoundTouch if it was actually used
                    if (_soundTouch.IsProcessingNeeded())
                    {
                        // Flush SoundTouch to get any remaining processed samples
                        lock (_soundTouchLock)
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
                                // Clear accumulation buffer
                                _soundTouchAccumulationCount = 0; 
                                // Reset transition tracking
                                _wasSoundTouchProcessing = false; 
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

                    // Bypass SoundTouch when tempo=1.0 AND pitch=0
                    // OPTIMIZATION: Check outside lock to reduce contention with MixThread
                    bool isProcessingNeeded = _soundTouch.IsProcessingNeeded();

                    // Detect transitions between SoundTouch processing and direct write
                    if (_wasSoundTouchProcessing && !isProcessingNeeded)
                    {
                        // SoundTouch OFF (was ON) - Flush all remaining data
                        lock (_soundTouchLock)
                        {
                            try
                            {
                                _soundTouch.Flush();

                                // Extract all flushed samples
                                while (true)
                                {
                                    int maxFrames = _soundTouchOutputBuffer.Length / _streamInfo.Channels;
                                    int framesReceived = _soundTouch.ReceiveSamples(_soundTouchOutputBuffer, maxFrames);
                                    if (framesReceived == 0)
                                        break;

                                    int samplesToAdd = framesReceived * _streamInfo.Channels;
                                    AddToSoundTouchAccumulationBuffer(_soundTouchOutputBuffer.AsSpan(0, samplesToAdd));
                                }

                                // Write ALL accumulated data to buffer
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
                    else if (!_wasSoundTouchProcessing && isProcessingNeeded)
                    {
                        // SoundTouch ON (was OFF) - Clear SoundTouch state
                        lock (_soundTouchLock)
                        {
                            _soundTouch.Clear();
                            _soundTouchAccumulationCount = 0;
                        }
                    }

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
                else if (!readResult.IsSucceeded)
                {
                    // Decode error
                    OnError(new AudioErrorEventArgs($"Decode error: {readResult.ErrorMessage}", null));
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
                // Wake up thread if waiting
                _pauseEvent.Set(); 

                // Wait for decoder thread to exit
                if (_decoderThread.IsAlive)
                {
                    if (!_decoderThread.Join(TimeSpan.FromSeconds(2)))
                    {
                        // Thread didn't exit in time, force interrupt
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

                // Dispose managed resources
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

    #endregion
}
