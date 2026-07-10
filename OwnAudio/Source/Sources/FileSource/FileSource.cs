using Ownaudio;
using Ownaudio.Core;
using Ownaudio.Decoders;
using OwnaudioNET.BufferManagement;
using OwnaudioNET.Core;
using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Synchronization;

namespace OwnaudioNET.Sources;

/// <summary>
/// Audio source that plays audio from a file with advanced features:
/// - Background decoding thread for smooth playback
/// - Circular buffer for zero-copy audio streaming
/// - SoundTouch integration for real-time pitch/tempo control
/// - Master clock synchronization for multi-track alignment
/// - Soft sync system for gradual drift correction
/// </summary>
public partial class FileSource : BaseAudioSource, ISynchronizable, IMasterClockSource
{
    #region Fields

    /// <summary>
    /// Managed decoder retained only so external callers can pull raw samples for analysis through
    /// <see cref="ReadSamples"/>. Playback, tempo/pitch and mixing all run natively.
    /// </summary>
    private readonly IAudioDecoder _decoder;

    /// <summary>
    /// Serializes access to the managed analysis decoder and its pending-seek/carry state.
    /// </summary>
    private readonly object _seekLock = new();

    /// <summary>
    /// The requested buffer size in frames, used to size the decode and carry buffers.
    /// </summary>
    private readonly int _bufferSizeInFrames;

    /// <summary>
    /// The audio configuration (sample rate, channels, buffer size) derived from the source stream.
    /// </summary>
    private readonly AudioConfig _config;

    /// <summary>
    /// Format and duration information reported by the decoder for this source.
    /// </summary>
    private readonly AudioStreamInfo _streamInfo;

    /// <summary>
    /// Scratch byte buffer that receives one decode call's worth of interleaved float samples.
    /// </summary>
    private readonly byte[] _decodeBuffer = null!;

    /// <summary>
    /// Carry buffer holding samples decoded but not yet consumed by the synchronous analysis cursor,
    /// because the decoder's frame granularity rarely matches the requested frame count.
    /// </summary>
    private readonly float[] _pendingDecoded;

    /// <summary>
    /// Offset of the first unconsumed sample within <see cref="_pendingDecoded"/>.
    /// </summary>
    private int _pendingOffset;

    /// <summary>
    /// Number of unconsumed samples currently held in <see cref="_pendingDecoded"/>.
    /// </summary>
    private int _pendingCount;

    /// <summary>
    /// A pending analysis-decoder seek target in seconds, or a negative value when none is queued.
    /// </summary>
    /// <remarks>
    /// Set by <see cref="Seek"/> and applied lazily on the next <see cref="ReadSamples"/> so a
    /// transport seek (including the one issued from clock attachment during add-to-mixer) never runs
    /// managed decode work on the control thread, which would starve the native output stream's first
    /// buffer and click at playback start.
    /// </remarks>
    private double _analysisSeekTarget = -1.0;

    /// <summary>
    /// The analysis cursor position in seconds, advanced by <see cref="ReadSamples"/>. Reported by
    /// <see cref="Position"/> only when there is no native backend.
    /// </summary>
    private double _currentPosition;

    /// <summary>
    /// Whether the managed analysis decoder has reached end of stream.
    /// </summary>
    private volatile bool _isEndOfStream;

    /// <summary>
    /// The current playback tempo multiplier, mirrored to the native track.
    /// </summary>
    private float _tempo = 1.0f;

    /// <summary>
    /// The current pitch shift in semitones, mirrored to the native track.
    /// </summary>
    private float _pitchShift = 0.0f;

    /// <summary>
    /// Whether this source has been disposed.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// The attached master clock, or <see langword="null"/> when detached. Retained for the
    /// <see cref="OwnaudioNET.Interfaces.ISynchronizable"/> / <see cref="OwnaudioNET.Interfaces.IMasterClockSource"/>
    /// API surface; native playback keeps its own timing.
    /// </summary>
    private MasterClock? _masterClock = null;

    /// <summary>
    /// The source's start offset in seconds relative to the master clock timeline.
    /// </summary>
    private double _startOffset = 0.0;

    /// <summary>
    /// The source-local time in seconds tracked while attached to a master clock.
    /// </summary>
    private double _trackLocalTime = 0.0;

    #endregion
    
    #region Properties

    /// <summary>
    /// Gets or sets the synchronization tolerance in seconds (Green Zone threshold).
    /// Drift below this value requires no correction.
    /// </summary>
    public double SyncTolerance { get; set; } = 0.005; // 5ms (was 20ms)

    /// <summary>
    /// Soft sync tolerance in seconds (Yellow Zone threshold).
    /// Drift between SyncTolerance and SoftSyncTolerance triggers tempo adjustment.
    /// </summary>
    public double SoftSyncTolerance { get; set; } = 0.025; // 25ms (was 100ms)

    /// <summary>
    /// Maximum tempo adjustment percentage for soft sync.
    /// Default is 0.02 (2%).
    /// </summary>
    public double SoftSyncMaxTempoAdjustment { get; set; } = 0.02;

    /// <summary>
    /// Gets a diagnostic snapshot of the current adaptive synchronization state.
    /// Returns a stack-allocated value type — safe to call from any thread without allocation.
    /// </summary>
    /// <remarks>
    /// Monitor <see cref="SyncDiagnosticsSnapshot.AdaptiveScale"/> to detect whether the
    /// adaptive tolerance system has elevated its thresholds. A scale above <c>1.0</c> may
    /// indicate an underlying audio processing performance issue.
    /// </remarks>
    public SyncDiagnosticsSnapshot SyncDiagnostics => new SyncDiagnosticsSnapshot
    {
        AdaptiveScale                = 1.0,
        EffectiveSyncToleranceMs     = SyncTolerance * 1000.0,
        EffectiveSoftSyncToleranceMs = SoftSyncTolerance * 1000.0,
        RedZoneHitsInWindow          = 0,
    };

    /// <inheritdoc/>
    public override AudioConfig Config => _config;

    /// <inheritdoc/>
    public override AudioStreamInfo StreamInfo => _streamInfo;

    /// <inheritdoc/>
    /// <remarks>
    /// File-backed sources report the native playback position; sources without a native backend
    /// (constructed from a Stream/decoder, or before attachment) report the managed analysis cursor
    /// advanced by <see cref="ReadSamples"/>.
    /// </remarks>
    public override double Position => RustTrack is not null
        ? RustNativePosition
        : Interlocked.CompareExchange(ref _currentPosition, 0, 0);

    /// <inheritdoc/>
    public override double Duration => _streamInfo.Duration.TotalSeconds;

    /// <inheritdoc/>
    public override bool IsEndOfStream => _isEndOfStream;

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
    /// Sets tempo and flushes all internal buffers by seeking to the current decoded
    /// content position. Unlike <see cref="SetTempoSmooth"/>, this eliminates the
    /// residual drift caused by old-tempo audio remaining in the SoundTouch pipeline
    /// and circular buffer after a tempo change.
    /// <para>
    /// The drift mechanism: <see cref="SetTempoSmooth"/> leaves ~50 ms of pre-buffered
    /// old-tempo audio in the pipeline. When that audio drains, the source position is
    /// ~5–15 ms off from what the <see cref="MasterClock"/> expects. Because this drift
    /// falls below the 20 ms <c>SyncTolerance</c> green-zone threshold, the soft-sync
    /// engine never corrects it, so the offset persists indefinitely.
    /// </para>
    /// <para>
    /// Seeks to <c>Position</c> (actual decoder content position in seconds), not to the
    /// clock-relative <c>_trackLocalTime</c>. These two values diverge when the initial
    /// playback tempo is not 1.0, so using <c>_trackLocalTime</c> would jump the audio
    /// to the wrong file location.
    /// </para>
    /// <para>
    /// Call this method once after a tempo slider has settled (debounced on the caller's
    /// side). A brief buffer-refill gap (~50 ms) may be audible immediately after the
    /// call; use <see cref="PreBuffer"/> to minimise this gap if needed.
    /// </para>
    /// <para>
    /// Has no effect when the source is not attached to a <see cref="MasterClock"/>.
    /// </para>
    /// </summary>
    /// <param name="tempo">The tempo multiplier (0.8x to 1.2x) - HARD LIMIT enforced.</param>
    public void SetTempoSynced(float tempo)
    {
        SetTempoInternal(
            Math.Clamp(tempo, AudioConstants.MinTempo, AudioConstants.MaxTempo),
            clearBuffer: false,
            setGracePeriod: false);

        if (_masterClock == null) return;

        Seek(Position);
    }

    /// <summary>
    /// Internal method to set tempo with configurable buffer clearing and grace period.
    /// </summary>
    /// <param name="value">The tempo multiplier.</param>
    /// <param name="clearBuffer">Whether to clear SoundTouch buffer (true for reset, false for smooth slider).</param>
    /// <param name="setGracePeriod">Whether to set sync grace period (true for reset, false for smooth slider).</param>
    private void SetTempoInternal(float value, bool clearBuffer, bool setGracePeriod)
    {
        _tempo = Math.Clamp(value, AudioConstants.MinTempo, AudioConstants.MaxTempo);

        if (_rustNative)
        {
            lock (_rustBackendLock)
            {
                if (_rustTrack is not null)
                    _rustTrack.Tempo = _tempo;
            }
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
        float clamped = Math.Clamp(value, -12.0f, 12.0f);
        if (Math.Abs(_pitchShift - clamped) < 0.001f)
            return;

        _pitchShift = clamped;

        if (_rustNative)
        {
            lock (_rustBackendLock)
            {
                if (_rustTrack is not null)
                    _rustTrack.PitchSemitones = _pitchShift;
            }
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

        _rustNative = OwnaudioNET.Engine.RustNativeChain.Enabled;
        
        _decoder = AudioDecoderFactory.Create(filePath, targetSampleRate, targetChannels);
        _streamInfo = _decoder.StreamInfo;

        _config = new AudioConfig
        {
            SampleRate = _streamInfo.SampleRate,
            Channels = _streamInfo.Channels,
            BufferSize = bufferSizeInFrames
        };

        _decodeBuffer = new byte[bufferSizeInFrames * _streamInfo.Channels * sizeof(float)];
        _pendingDecoded = new float[bufferSizeInFrames * _streamInfo.Channels];

        _currentPosition = 0.0;
        _isEndOfStream = false;
    }

    #endregion

    #region Core Playback Methods

    /// <inheritdoc/>
    public override int ReadSamples(Span<float> buffer, int frameCount)
    {
        ThrowIfDisposed();

        if (State != AudioState.Playing)
        {
            FillWithSilence(buffer, frameCount * _streamInfo.Channels);
            return frameCount;
        }

        if (_masterClock != null)
            return ReadSamplesSynchronized(buffer, frameCount);


        return ReadSamplesStandalone(buffer, frameCount);
    }

    /// <summary>
    /// Reads samples in standalone mode (no MasterClock) by decoding on demand on the calling thread.
    /// </summary>
    /// <param name="buffer">Destination span for interleaved samples.</param>
    /// <param name="frameCount">Number of frames requested.</param>
    /// <returns>The number of decoded frames produced (silence padding excluded).</returns>
    private int ReadSamplesStandalone(Span<float> buffer, int frameCount)
    {
        return DecodeSynchronously(buffer, frameCount);
    }

    /// <inheritdoc/>
    public override bool Seek(double positionInSeconds)
    {
        ThrowIfDisposed();

        if (positionInSeconds < 0 || positionInSeconds > Duration)
        {
            return false;
        }

        lock (_seekLock)
        {
            _analysisSeekTarget = positionInSeconds;
            _isEndOfStream = false;

            Interlocked.Exchange(ref _currentPosition, positionInSeconds);
            SetSamplePosition((long)(positionInSeconds * _streamInfo.SampleRate));
        }

        return RustNativeSeek(positionInSeconds);
    }


    #endregion

    #region Playback Control

    /// <summary>
    /// Pre-buffering hook retained for API compatibility. Playback is decoded and buffered by the
    /// native engine, so there is no managed buffer to prime and this is a no-op.
    /// </summary>
    public void PreBuffer()
    {
        ThrowIfDisposed();
    }

    /// <inheritdoc/>
    public override void Play()
    {
        ThrowIfDisposed();
        RustNativePlay();
    }

    /// <inheritdoc/>
    public override void Pause()
    {
        ThrowIfDisposed();
        RustNativePause();
    }

    /// <inheritdoc/>
    public override void Stop()
    {
        ThrowIfDisposed();
        RustNativeStop();
    }

    #endregion


    /// <summary>
    /// Releases the resources used by the FileSource, detaching the native backend and disposing the
    /// managed analysis decoder.
    /// </summary>
    /// <param name="disposing"><see langword="true"/> to release managed resources.</param>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            base.Dispose(disposing);

            if (disposing)
            {
                DisposeRustBackend();
                _decoder?.Dispose();
            }
        }
    }

    /// <inheritdoc/>
    public override void ResyncTo(long targetSamplePosition)
    {
        long driftFrames = targetSamplePosition - SamplePosition;
        if (Math.Abs(driftFrames) < 512)
            return;

        base.ResyncTo(targetSamplePosition);
    }
}
