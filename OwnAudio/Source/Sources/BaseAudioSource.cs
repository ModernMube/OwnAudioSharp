using Ownaudio;
using Ownaudio.Core;
using OwnaudioNET.Core;
using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;
using System.Numerics;

namespace OwnaudioNET.Sources;

/// <summary>
/// Common base for audio sources - state machine, volume/pan, events, buffer helpers.
/// </summary>
public abstract partial class BaseAudioSource : IAudioSource
{
    private AudioState _state;
    private float _volume;
    private float _pan;
    private bool _loop;
    private bool _disposed;

    /// <inheritdoc/>
    public Guid Id { get; }

    /// <inheritdoc/>
    public AudioState State
    {
        get => _state;
        protected set
        {
            if (_state != value)
            {
                var _old = _state;
                _state = value;
                OnStateChanged(new AudioStateChangedEventArgs(_old, value));
            }
        }
    }

    /// <inheritdoc/>
    public abstract AudioConfig Config { get; }

    /// <inheritdoc/>
    public abstract AudioStreamInfo StreamInfo { get; }

    /// <inheritdoc/>
    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0.0f, 20.0f);
    }

    /// <inheritdoc/>
    public float Pan
    {
        get => _pan;
        set => _pan = Math.Clamp(value, -1.0f, 1.0f);
    }

    /// <inheritdoc/>
    public bool Loop
    {
        get => _loop;
        set => _loop = value;
    }

    /// <inheritdoc/>
    public abstract double Position { get; }

    /// <inheritdoc/>
    public abstract double Duration { get; }

    /// <inheritdoc/>
    public abstract bool IsEndOfStream { get; }

    /// <inheritdoc/>
    public virtual float Tempo { get; set; } = 1.0f;

    /// <inheritdoc/>
    public virtual float PitchShift { get; set; } = 0.0f;

    /// <inheritdoc/>
    public event EventHandler<AudioStateChangedEventArgs>? StateChanged;

    /// <inheritdoc/>
    public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;

    /// <inheritdoc/>
    public event EventHandler<AudioErrorEventArgs>? Error;

    /// <summary>
    /// Fresh id, stopped state, unity volume.
    /// </summary>
    protected BaseAudioSource()
    {
        Id = Guid.NewGuid();
        _state = AudioState.Stopped;
        _volume = 1.0f;
    }

    /// <inheritdoc/>
    public abstract int ReadSamples(Span<float> buffer, int frameCount);

    /// <inheritdoc/>
    public abstract bool Seek(double positionInSeconds);

    /// <inheritdoc/>
    public virtual void Play()
    {
        ThrowIfDisposed();

        if (State == AudioState.Stopped || State == AudioState.Paused || State == AudioState.EndOfStream)
            State = AudioState.Playing;
    }

    /// <inheritdoc/>
    public virtual void Pause()
    {
        ThrowIfDisposed();

        if (State == AudioState.Playing) State = AudioState.Paused;
    }

    /// <inheritdoc/>
    public virtual void Stop()
    {
        ThrowIfDisposed();

        if (State != AudioState.Stopped)
        {
            State = AudioState.Stopped;
            Seek(0);
        }
    }

    /// <summary>
    /// Fires StateChanged.
    /// </summary>
    /// <param name="e"></param>
    protected virtual void OnStateChanged(AudioStateChangedEventArgs e) => StateChanged?.Invoke(this, e);

    /// <summary>
    /// Fires BufferUnderrun.
    /// </summary>
    /// <param name="e"></param>
    protected virtual void OnBufferUnderrun(BufferUnderrunEventArgs e) => BufferUnderrun?.Invoke(this, e);

    /// <summary>
    /// Fires Error and flips state to Error.
    /// </summary>
    /// <param name="e"></param>
    protected virtual void OnError(AudioErrorEventArgs e)
    {
        Error?.Invoke(this, e);
        State = AudioState.Error;
    }

    /// <summary>
    /// Multiplies the buffer by volume, SIMD where the hw allows. Skips when volume ~= 1.
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="sampleCount"></param>
    protected void ApplyVolume(Span<float> buffer, int sampleCount)
    {
        if (Math.Abs(_volume - 1.0f) < 0.001f) return;

        var _span = buffer.Slice(0, sampleCount);
        float _vol = _volume;

        int i = 0;
        int _simd = Vector<float>.Count;

        if (Vector.IsHardwareAccelerated && _span.Length >= _simd)
        {
            var _volVec = new Vector<float>(_vol);
            for (; i <= _span.Length - _simd; i += _simd)
            {
                var _vec = new Vector<float>(_span.Slice(i, _simd));
                _vec *= _volVec;
                _vec.CopyTo(_span.Slice(i, _simd));
            }
        }

        for (; i < _span.Length; i++) _span[i] *= _vol;
    }

    /// <summary>
    /// Zeros the first sampleCount samples.
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="sampleCount"></param>
    protected static void FillWithSilence(Span<float> buffer, int sampleCount) => buffer.Slice(0, sampleCount).Clear();

    /// <summary>
    /// Short linear fade-out on the buffer tail, kills the click when we drop to silence
    /// on an underrun. In-place, zero-alloc, any length is fine.
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="fadeSamples"></param>
    protected static void FadeOutTail(Span<float> buffer, int fadeSamples)
    {
        if(buffer.IsEmpty || fadeSamples <= 0) return;

        int _count = Math.Min(fadeSamples, buffer.Length);
        int _start = buffer.Length - _count;

        for (int i = 0; i < _count; i++)
            buffer[_start + i] *= (float)(_count - 1 - i) / _count;
    }

    /// <summary>
    /// Short linear fade-in on the buffer head, kills the crack after a hard buffer skip
    /// (Red Zone drift correction). In-place, zero-alloc, any length is fine.
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="fadeSamples"></param>
    protected static void FadeInHead(Span<float> buffer, int fadeSamples)
    {
        if(buffer.IsEmpty || fadeSamples <= 0) return;

        int _count = Math.Min(fadeSamples, buffer.Length);

        for (int i = 0; i < _count; i++)
            buffer[i] *= (float)i / _count;
    }


    #region Channel Routing

    private int[]? _outputChannelMapping = null;

    /// <summary>
    /// Which logical mix-buffer channels this source writes into - per-source routing to output
    /// channel pairs. Length must equal Config.Channels, indices are zero-based logical channels.
    /// E.g. stereo music on [0,1], metronome on [2,3] over an 8ch device.
    /// </summary>
    public int[]? OutputChannelMapping
    {
        get => _outputChannelMapping;
        set
        {
            if (value != null && value.Length != Config.Channels)
                throw new ArgumentException(
                    $"OutputChannelMapping length ({value.Length}) must match source channel count ({Config.Channels})");
            _outputChannelMapping = value;
        }
    }

    /// <summary>
    /// Fluent shortcut for OutputChannelMapping, returns this.
    /// </summary>
    /// <param name="channels"></param>
    /// <returns></returns>
    public BaseAudioSource RouteToChannels(params int[] channels)
    {
        OutputChannelMapping = channels;
        return this;
    }

    #endregion

    /// <summary>
    /// Throws if we're already disposed.
    /// </summary>
    protected void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(GetType().Name);
    }

    /// <summary>
    /// Stops playback and marks the source disposed.
    /// </summary>
    /// <param name="disposing"></param>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing) Stop();
            _disposed = true;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
