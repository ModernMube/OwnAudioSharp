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
    
    // Lock-free soft sync communication (Mixer -> Decoder thread)
    private volatile float _pendingSoftSyncTempoAdjustment = 0f;

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

    // Adaptive drift correction tracking
    private int _consecutiveUnderruns = 0;  // Counter for post-dropout aggressive recovery
    private double _lastDrift = 0.0;  // Track drift history for velocity detection

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
    /// Valid range: 0.8x to 1.2x (-20% to +20%) - HARD LIMIT enforced for CPU performance.
    /// This property clears the SoundTouch buffer on change - use SetTempoSmooth() for slider adjustments.
    /// </summary>
    public override float Tempo
    {
        get => _tempo;
        set => SetTempoInternal(
            Math.Clamp(value, AudioConstants.MinTempo, AudioConstants.MaxTempo),
            clearBuffer: true,
            setGracePeriod: true);
    }

    /// <summary>
    /// Sets tempo smoothly without clearing buffers - ideal for real-time slider adjustments.
    /// Use this for continuous tempo changes to avoid audio glitches with many tracks.
    /// </summary>
    /// <param name="tempo">The tempo multiplier (0.8x to 1.2x) - HARD LIMIT enforced.</param>
    public void SetTempoSmooth(float tempo)
    {
        SetTempoInternal(
            Math.Clamp(tempo, AudioConstants.MinTempo, AudioConstants.MaxTempo),
            clearBuffer: false,
            setGracePeriod: false);
    }

    /// <summary>
    /// Internal method to set tempo with configurable buffer clearing and grace period.
    /// </summary>
    /// <param name="value">The tempo multiplier.</param>
    /// <param name="clearBuffer">Whether to clear SoundTouch buffer (true for reset, false for smooth slider).</param>
    /// <param name="setGracePeriod">Whether to set sync grace period (true for reset, false for smooth slider).</param>
    private void SetTempoInternal(float value, bool clearBuffer, bool setGracePeriod)
    {
        // HARD LIMIT: Clamp to 0.8x to 1.2x range for CPU performance
        float clamped = Math.Clamp(value, AudioConstants.MinTempo, AudioConstants.MaxTempo);
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
            _gracePeriodEndTime = _trackLocalTime + SyncConfig.GracePeriodSeconds;
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
            _gracePeriodEndTime = _trackLocalTime + SyncConfig.GracePeriodSeconds;
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
        // INCREASED from 2x to 4x to handle extreme time-stretch ratios safely (e.g., 20% speed)
        _soundTouchOutputBuffer = new float[bufferSizeInFrames * _streamInfo.Channels * 4];

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
            Priority = ThreadPriority.Normal  // FIXED: Changed from AboveNormal to prevent thread starvation with 22+ tracks
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

        // Delegate to appropriate strategy based on synchronization mode
        if (_masterClock != null)
            return ReadSamplesSynchronized(buffer, frameCount);

        if (_ghostTrack != null)
            return ReadSamplesLegacy(buffer, frameCount);

        return ReadSamplesStandalone(buffer, frameCount);
    }

    /// <summary>
    /// Reads samples in standalone mode (no synchronization).
    /// </summary>
    private int ReadSamplesStandalone(Span<float> buffer, int frameCount)
    {
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
}
