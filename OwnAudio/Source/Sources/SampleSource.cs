using Ownaudio;
using Ownaudio.Core;
using OwnaudioNET.Core;
using OwnaudioNET.Events;

namespace OwnaudioNET.Sources;

/// <summary>
/// Plays audio straight from an in-memory sample buffer, low latency.
/// In the rust-native chain a native MemoryTrack owns the buffer (see the RustNative partial),
/// managed side is just a controller - no audio flows through managed code.
/// </summary>
public sealed partial class SampleSource : BaseAudioSource
{
    private readonly AudioConfig _config;
    private float[] _sampleData;
    private int _readPosition;
    private readonly object _dataLock = new();
    private double _duration;
    private bool _disposed;

    /// <summary>
    /// Audio config this source runs at.
    /// </summary>
    public override AudioConfig Config => _config;

    /// <summary>
    /// Stream info (channels, rate, length).
    /// </summary>
    public override AudioStreamInfo StreamInfo => new AudioStreamInfo(
        channels: _config.Channels,
        sampleRate: _config.SampleRate,
        duration: TimeSpan.FromSeconds(_duration));

    /// <summary>
    /// Playback pos in seconds. Native path asks the rust track.
    /// </summary>
    public override double Position
    {
        get
        {
            if (_rustNative) return _rustPosition;

            lock (_dataLock)
            {
                if (_sampleData == null || _sampleData.Length == 0) return 0.0;
                return (double)(_readPosition / _config.Channels) / _config.SampleRate;
            }
        }
    }

    /// <summary>
    /// Total length in seconds.
    /// </summary>
    public override double Duration => _duration;

    /// <summary>
    /// True once we've run out of samples and aren't looping.
    /// </summary>
    public override bool IsEndOfStream
    {
        get { lock (_dataLock) return _readPosition >= (_sampleData?.Length ?? 0) && !Loop; }
    }

    /// <summary>
    /// Allow SubmitSamples() to swap the buffer on the fly.
    /// </summary>
    public bool AllowDynamicUpdate { get; set; } = false;

    /// <summary>
    /// Wraps a ready sample buffer.
    /// </summary>
    /// <param name="samples"></param>
    /// <param name="config"></param>
    public SampleSource(float[] samples, AudioConfig config)
    {
        if (samples == null || samples.Length == 0)
            throw new ArgumentNullException(nameof(samples));

        _config = config ?? throw new ArgumentNullException(nameof(config));
        _sampleData = samples;
        _readPosition = 0;
        _duration = (double)(samples.Length / config.Channels) / config.SampleRate;

        _rustNative = OwnaudioNET.Engine.RustNativeChain.Enabled;
    }

    /// <summary>
    /// Empty buffer of the given size, dynamic update on.
    /// </summary>
    /// <param name="bufferSizeInFrames"></param>
    /// <param name="config"></param>
    public SampleSource(int bufferSizeInFrames, AudioConfig config)
    {
        if(bufferSizeInFrames <= 0)
            throw new ArgumentException("Buffer size must be positive.", nameof(bufferSizeInFrames));

        _config = config ?? throw new ArgumentNullException(nameof(config));
        _sampleData = new float[bufferSizeInFrames * config.Channels];
        _readPosition = 0;
        _duration = 0.0;
        AllowDynamicUpdate = true;

        _rustNative = OwnaudioNET.Engine.RustNativeChain.Enabled;
    }

    /// <summary>
    /// Swaps in a fresh buffer while playing. Grows storage if needed, rewinds to start.
    /// </summary>
    /// <param name="samples"></param>
    public void SubmitSamples(ReadOnlySpan<float> samples)
    {
        ThrowIfDisposed();

        if (!AllowDynamicUpdate)
            throw new InvalidOperationException("Dynamic sample submission is not enabled.");

        lock (_dataLock)
        {
            if (samples.Length > _sampleData.Length)
                Array.Resize(ref _sampleData, samples.Length);

            samples.CopyTo(_sampleData.AsSpan());
            _readPosition = 0;
            _duration = (double)(samples.Length / _config.Channels) / _config.SampleRate;
        }

        //Native source owns the audio, so push the new buffer to it (control copy, never on the audio path).
        if (_rustNative) _rustSubmit(samples);
    }

    /// <summary>
    /// Silences the buffer and rewinds.
    /// </summary>
    public void Clear()
    {
        ThrowIfDisposed();

        float[]? _cleared = null;
        lock (_dataLock)
        {
            if (_sampleData != null)
            {
                Array.Clear(_sampleData, 0, _sampleData.Length);
                _cleared = _sampleData;
            }
            _readPosition = 0;
            _duration = 0.0;
        }

        if (_rustNative && _cleared != null) _rustSubmit(_cleared);
    }

    /// <summary>
    /// Pulls frameCount frames into buffer, handles loop/EOS, applies volume. Silence when not playing.
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="frameCount"></param>
    /// <returns></returns>
    public override int ReadSamples(Span<float> buffer, int frameCount)
    {
        ThrowIfDisposed();

        if (State != AudioState.Playing)
        {
            FillWithSilence(buffer, frameCount * _config.Channels);
            return frameCount;
        }

        lock (_dataLock)
        {
            if (_sampleData == null || _sampleData.Length == 0)
            {
                FillWithSilence(buffer, frameCount * _config.Channels);
                return frameCount;
            }

            int wanted = frameCount * _config.Channels;
            int toCopy = Math.Min(wanted, _sampleData.Length - _readPosition);
            int framesRead = 0;

            if (toCopy > 0)
            {
                _sampleData.AsSpan(_readPosition, toCopy).CopyTo(buffer);
                _readPosition += toCopy;
                framesRead = toCopy / _config.Channels;
            }

            if (_readPosition >= _sampleData.Length)
            {
                if (Loop)
                {
                    _readPosition = 0;

                    //We wrap around and keep filling from the start of the buffer
                    if (framesRead < frameCount)
                    {
                        int rest = Math.Min((frameCount - framesRead) * _config.Channels, _sampleData.Length);
                        if (rest > 0)
                        {
                            _sampleData.AsSpan(0, rest).CopyTo(buffer.Slice(framesRead * _config.Channels));
                            _readPosition = rest;
                            framesRead += rest / _config.Channels;
                        }
                    }
                }
                else
                {
                    State = AudioState.EndOfStream;
                    if (framesRead < frameCount)
                        FillWithSilence(buffer.Slice(framesRead * _config.Channels), (frameCount - framesRead) * _config.Channels);
                }
            }
            else if (framesRead < frameCount)
                FillWithSilence(buffer.Slice(framesRead * _config.Channels), (frameCount - framesRead) * _config.Channels);

            ApplyVolume(buffer, frameCount * _config.Channels);
            return framesRead;
        }
    }

    /// <summary>
    /// Jumps to positionInSeconds. Native path hands off to the rust track.
    /// </summary>
    /// <param name="positionInSeconds"></param>
    /// <returns></returns>
    public override bool Seek(double positionInSeconds)
    {
        ThrowIfDisposed();

        if (positionInSeconds < 0 || positionInSeconds > Duration) return false;

        if (_rustNative) return _rustSeek(positionInSeconds);

        lock (_dataLock)
        {
            if (_sampleData == null) return false;

            long _targetFrame = (long)(positionInSeconds * _config.SampleRate);
            _readPosition = Math.Clamp((int)(_targetFrame * _config.Channels), 0, _sampleData.Length);
            return true;
        }
    }

    /// <inheritdoc/>
    public override void Play()
    {
        ThrowIfDisposed();

        if (_rustNative) { _rustPlay(); return; }

        base.Play();
    }

    /// <inheritdoc/>
    public override void Pause()
    {
        ThrowIfDisposed();

        if (_rustNative) { _rustPause(); return; }

        base.Pause();
    }

    /// <inheritdoc/>
    public override void Stop()
    {
        ThrowIfDisposed();

        if (_rustNative) { _rustStop(); return; }

        base.Stop();
    }

    /// <summary>
    /// Tears down the native backend and drops the buffer.
    /// </summary>
    /// <param name="disposing"></param>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                if (_rustNative) _disposeRustBackend();

                lock (_dataLock) _sampleData = null!;
            }
            _disposed = true;
        }
        base.Dispose(disposing);
    }
}
