using Ownaudio;
using Ownaudio.Core;
using OwnaudioNET.Core;
using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;
using System.Numerics;

namespace OwnaudioNET.Sources;

/// <summary>
/// Base implementation for audio sources providing common functionality.
/// </summary>
public abstract partial class BaseAudioSource : IAudioSource
{
    private AudioState _state;
    private float _volume;
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
                var oldState = _state;
                _state = value;
                OnStateChanged(new AudioStateChangedEventArgs(oldState, value));
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
        set => _volume = Math.Clamp(value, 0.0f, 1.0f);
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
    /// Initializes a new instance of the BaseAudioSource class.
    /// </summary>
    protected BaseAudioSource()
    {
        Id = Guid.NewGuid();
        _state = AudioState.Stopped;
        _volume = 1.0f;
        _loop = false;
        _disposed = false;
    }

    /// <inheritdoc/>
    public abstract int ReadSamples(Span<float> buffer, int frameCount);

    /// <inheritdoc/>
    public abstract bool Seek(double positionInSeconds);

    /// <inheritdoc/>
    public virtual void Play()
    {
        ThrowIfDisposed();

        if (State == AudioState.Stopped || State == AudioState.Paused)
        {
            State = AudioState.Playing;
        }
    }

    /// <inheritdoc/>
    public virtual void Pause()
    {
        ThrowIfDisposed();

        if (State == AudioState.Playing)
        {
            State = AudioState.Paused;
        }
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
    /// Raises the StateChanged event.
    /// </summary>
    protected virtual void OnStateChanged(AudioStateChangedEventArgs e)
    {
        StateChanged?.Invoke(this, e);
    }

    /// <summary>
    /// Raises the BufferUnderrun event.
    /// </summary>
    protected virtual void OnBufferUnderrun(BufferUnderrunEventArgs e)
    {
        BufferUnderrun?.Invoke(this, e);
    }

    /// <summary>
    /// Raises the Error event.
    /// </summary>
    protected virtual void OnError(AudioErrorEventArgs e)
    {
        Error?.Invoke(this, e);
        State = AudioState.Error;
    }

    /// <summary>
    /// Applies volume to the buffer using SIMD vectorization for optimal performance.
    /// </summary>
    protected void ApplyVolume(Span<float> buffer, int sampleCount)
    {
        if (Math.Abs(_volume - 1.0f) < 0.001f)
            return; // Skip if volume is essentially 1.0

        // Use Span slicing for bounds checking and better JIT optimization
        var targetSpan = buffer.Slice(0, sampleCount);
        float vol = _volume; // Cache field in local variable

        int i = 0;
        int simdLength = Vector<float>.Count;

        // SIMD vectorized processing (4-8x faster on modern CPUs)
        if (Vector.IsHardwareAccelerated && targetSpan.Length >= simdLength)
        {
            var volumeVec = new Vector<float>(vol);

            // Process in SIMD chunks (typically 4 or 8 floats at once)
            for (; i <= targetSpan.Length - simdLength; i += simdLength)
            {
                var vec = new Vector<float>(targetSpan.Slice(i, simdLength));
                vec *= volumeVec;
                vec.CopyTo(targetSpan.Slice(i, simdLength));
            }
        }

        // Process remaining samples (scalar fallback)
        for (; i < targetSpan.Length; i++)
        {
            targetSpan[i] *= vol;
        }
    }

    /// <summary>
    /// Fills the buffer with silence.
    /// </summary>
    protected static void FillWithSilence(Span<float> buffer, int sampleCount)
    {
        buffer.Slice(0, sampleCount).Clear();
    }

    /// <summary>
    /// Throws ObjectDisposedException if the object has been disposed.
    /// </summary>
    protected void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(GetType().Name);
    }

    /// <summary>
    /// Releases the unmanaged resources used by the BaseAudioSource and optionally releases the managed resources.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                Stop();
            }

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
