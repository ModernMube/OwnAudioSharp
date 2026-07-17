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
/// File based audio source. Playback runs on the native chain, the managed
/// decoder only feeds analysis reads.
/// </summary>
public partial class FileSource : BaseAudioSource, ISynchronizable, IMasterClockSource
{
    #region Fields

    /// <summary>
    /// Managed decoder, kept only for ReadSamples analysis pulls.
    /// </summary>
    private readonly IAudioDecoder _decoder;

    /// <summary>
    /// Guards the analysis decoder and its pending/seek state.
    /// </summary>
    private readonly object _seekLock = new object();

    /// <summary>
    /// Requested buffer size in frames.
    /// </summary>
    private readonly int _bufferSizeInFrames;

    /// <summary>
    /// Config derived from the source stream.
    /// </summary>
    private readonly AudioConfig _config;

    /// <summary>
    /// Format and duration info from the decoder.
    /// </summary>
    private readonly AudioStreamInfo _streamInfo;

    /// <summary>
    /// Scratch buffer for one decode call.
    /// </summary>
    private readonly byte[] _decodeBuffer;

    /// <summary>
    /// Decoded but not yet consumed samples, decoder granularity rarely matches the request.
    /// </summary>
    private readonly float[] _pendingDecoded;

    /// <summary>
    /// First unconsumed sample in _pendingDecoded.
    /// </summary>
    private int _pendingOffset;

    /// <summary>
    /// Unconsumed sample count in _pendingDecoded.
    /// </summary>
    private int _pendingCount;

    /// <summary>
    /// Pending analysis seek in seconds, negative = none. Applied lazily on the next
    /// read so a transport seek never decodes on the control thread.
    /// </summary>
    private double _analysisSeekTarget = -1.0;

    /// <summary>
    /// Analysis cursor position in seconds.
    /// </summary>
    private double _currentPosition;

    /// <summary>
    /// Analysis decoder hit end of stream.
    /// </summary>
    private volatile bool _isEndOfStream;

    /// <summary>
    /// Tempo multiplier, mirrored to the native track.
    /// </summary>
    private float _tempo = 1.0f;

    /// <summary>
    /// Pitch shift in semitones, mirrored to the native track.
    /// </summary>
    private float _pitchShift = 0.0f;

    /// <summary>
    /// Disposed flag.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Attached master clock or null, native playback keeps its own timing.
    /// </summary>
    private MasterClock? _masterClock = null;

    /// <summary>
    /// Start offset in seconds on the master clock timeline.
    /// </summary>
    private double _startOffset = 0.0;

    /// <summary>
    /// Source local time while attached to a clock.
    /// </summary>
    private double _trackLocalTime = 0.0;

    #endregion

    #region Propertyes

    /// <summary>
    /// Green zone threshold in seconds, drift below this needs no correction.
    /// </summary>
    public double SyncTolerance { get; set; } = 0.005;

    /// <summary>
    /// Yellow zone threshold in seconds, drift here gets a tempo nudge.
    /// </summary>
    public double SoftSyncTolerance { get; set; } = 0.025;

    /// <summary>
    /// Max tempo adjustment for soft sync.
    /// </summary>
    public double SoftSyncMaxTempoAdjustment { get; set; } = 0.02;

    /// <summary>
    /// Diagnostic snapshot of the sync state, allocation free.
    /// </summary>
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
    public override double Position => RustTrack is not null
        ? _rustNativePosition
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
    /// Tempo multiplier, 1.0 = normal. Clamped to 0.8x..1.2x.
    /// </summary>
    public override float Tempo
    {
        get => _tempo;
        set => _setTempo(value);
    }

    /// <summary>
    /// Smooth tempo set for slider use.
    /// </summary>
    /// <param name="tempo"></param>
    public void SetTempoSmooth(float tempo) { _setTempo(tempo); }

    /// <summary>
    /// Sets tempo then reseeks to the current position so leftover old-tempo audio
    /// cannot keep a small permanent drift. Call once after the slider settled.
    /// </summary>
    /// <param name="tempo"></param>
    public void SetTempoSynced(float tempo)
    {
        _setTempo(tempo);
        if (_masterClock == null) return;

        Seek(Position);
    }

    /// <summary>
    /// Stores the clamped tempo and mirrors it to the native track.
    /// </summary>
    /// <param name="value"></param>
    private void _setTempo(float value)
    {
        _tempo = Math.Clamp(value, AudioConstants.MinTempo, AudioConstants.MaxTempo);

        if(_rustNative)
        {
            lock (_rustBackendLock)
            {
                if (_rustTrack is not null) _rustTrack.Tempo = _tempo;
            }
        }
    }

    /// <summary>
    /// Pitch shift in semitones, -12..+12.
    /// </summary>
    public override float PitchShift
    {
        get => _pitchShift;
        set => _setPitch(value);
    }

    /// <summary>
    /// Smooth pitch set for slider use.
    /// </summary>
    /// <param name="pitchSemitones"></param>
    public void SetPitchSmooth(float pitchSemitones) { _setPitch(pitchSemitones); }

    /// <summary>
    /// Stores the clamped pitch and mirrors it to the native track.
    /// </summary>
    /// <param name="value"></param>
    private void _setPitch(float value)
    {
        float _clamped = Math.Clamp(value, -12.0f, 12.0f);
        if (Math.Abs(_pitchShift - _clamped) < 0.001f) return;

        _pitchShift = _clamped;

        if(_rustNative)
        {
            lock (_rustBackendLock)
            {
                if (_rustTrack is not null) _rustTrack.PitchSemitones = _pitchShift;
            }
        }
    }

    #endregion

    #region Constructor

    /// <summary>
    /// Opens the file and builds the analysis decoder, format is detected automatically.
    /// </summary>
    /// <param name="filePath"></param>
    /// <param name="bufferSizeInFrames"></param>
    /// <param name="targetSampleRate"></param>
    /// <param name="targetChannels"></param>
    public FileSource(
        string filePath,
        int bufferSizeInFrames = 8192,
        int targetSampleRate = 48000,
        int targetChannels = 2)
    {
        if (string.IsNullOrEmpty(filePath))
            throw new ArgumentNullException(nameof(filePath));

        _filePath = filePath;
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

        if (_masterClock != null) return _readSamplesSynchronized(buffer, frameCount);

        return _decodeSynchronously(buffer, frameCount);
    }

    /// <inheritdoc/>
    public override bool Seek(double positionInSeconds)
    {
        ThrowIfDisposed();

        if (positionInSeconds < 0 || positionInSeconds > Duration) return false;

        lock (_seekLock)
        {
            _analysisSeekTarget = positionInSeconds;
            _isEndOfStream = false;

            Interlocked.Exchange(ref _currentPosition, positionInSeconds);
            SetSamplePosition((long)(positionInSeconds * _streamInfo.SampleRate));
        }

        return _rustNativeSeek(positionInSeconds);
    }

    #endregion

    #region Playback Control

    /// <summary>
    /// Kept for API compatibility, the native engine buffers on its own so this is a no-op.
    /// </summary>
    public void PreBuffer()
    {
        ThrowIfDisposed();
    }

    /// <inheritdoc/>
    public override void Play()
    {
        ThrowIfDisposed();
        _rustNativePlay();
    }

    /// <inheritdoc/>
    public override void Pause()
    {
        ThrowIfDisposed();
        _rustNativePause();
    }

    /// <inheritdoc/>
    public override void Stop()
    {
        ThrowIfDisposed();
        _rustNativeStop();
    }

    #endregion

    /// <summary>
    /// Drops the native backend and the analysis decoder.
    /// </summary>
    /// <param name="disposing"></param>
    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;

        _disposed = true;
        base.Dispose(disposing);

        if (disposing)
        {
            _disposeRustBackend();
            _decoder?.Dispose();
        }
    }

    /// <inheritdoc/>
    public override void ResyncTo(long targetSamplePosition)
    {
        long _driftFrames = targetSamplePosition - SamplePosition;
        if (Math.Abs(_driftFrames) < 512) return;

        base.ResyncTo(targetSamplePosition);
    }
}
