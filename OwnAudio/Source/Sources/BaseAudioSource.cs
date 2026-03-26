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

        if (State == AudioState.Stopped || State == AudioState.Paused || State == AudioState.EndOfStream)
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
    /// Applies a short linear fade-out to the tail of the buffer.
    /// Prevents audible click/pop artifacts when the buffer transitions abruptly to silence
    /// during a buffer underrun (data → 0.0 discontinuity).
    /// Zero-allocation, in-place; safe to call with any buffer length.
    /// </summary>
    /// <param name="buffer">The audio buffer whose tail will be faded.</param>
    /// <param name="fadeSamples">Number of samples to fade (from full volume to silence).</param>
    protected static void FadeOutTail(Span<float> buffer, int fadeSamples)
    {
        if (buffer.IsEmpty || fadeSamples <= 0)
            return;

        int count = Math.Min(fadeSamples, buffer.Length);
        int startIdx = buffer.Length - count;

        for (int i = 0; i < count; i++)
        {
            // Linear ramp: full volume at startIdx, silence at last sample
            float t = (float)(count - 1 - i) / count;
            buffer[startIdx + i] *= t;
        }
    }

    /// <summary>
    /// Applies a short linear fade-in to the head of the buffer.
    /// Prevents audible click/pop artifacts when audio resumes after a hard buffer skip
    /// (Red Zone drift correction) caused a waveform discontinuity: the first sample after
    /// the skip may be far from the last sample before it, producing a loud crack.
    /// Zero-allocation, in-place; safe to call with any buffer length.
    /// </summary>
    /// <param name="buffer">The audio buffer whose head will be faded in.</param>
    /// <param name="fadeSamples">Number of samples to ramp from silence to full volume.</param>
    protected static void FadeInHead(Span<float> buffer, int fadeSamples)
    {
        if (buffer.IsEmpty || fadeSamples <= 0)
            return;

        int count = Math.Min(fadeSamples, buffer.Length);

        for (int i = 0; i < count; i++)
        {
            // Linear ramp: silence at index 0, full volume at index count-1
            float t = (float)i / count;
            buffer[i] *= t;
        }
    }


    #region Channel Routing

    private int[]? _outputChannelMapping = null;

    /// <summary>
    /// Specifies which logical mix-buffer channels this source writes its audio into,
    /// enabling per-source routing to distinct physical output channel pairs.
    /// Works for any source type (FileSource, SampleSource, InputSource, custom sources).
    /// <para>
    /// The mapping array length must equal the source's <c>Config.Channels</c>.
    /// Indices must be valid within the mixer's total logical channel count.
    /// Channels are zero-indexed.
    /// </para>
    /// <example>
    /// <code>
    /// // 8-channel device: stereo music on ch 0+1, metronome on ch 2+3
    /// // AudioConfig: Channels=4, OutputChannelSelectors=[0,1,2,3] (physical routing)
    /// music.OutputChannelMapping    = new[] { 0, 1 };
    /// metronome.OutputChannelMapping = new[] { 2, 3 };
    /// </code>
    /// </example>
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when the array length does not match <c>Config.Channels</c>.
    /// </exception>
    public int[]? OutputChannelMapping
    {
        get => _outputChannelMapping;
        set
        {
            if (value != null && value.Length != Config.Channels)
            {
                throw new ArgumentException(
                    $"OutputChannelMapping length ({value.Length}) must match source channel count ({Config.Channels})");
            }
            _outputChannelMapping = value;
        }
    }

    /// <summary>
    /// Routes this source to the specified logical channel pair and returns <c>this</c>
    /// for fluent configuration.
    /// </summary>
    /// <param name="channels">Logical channel indices (length must equal <c>Config.Channels</c>).</param>
    /// <returns>This source instance (fluent API).</returns>
    public BaseAudioSource RouteToChannels(params int[] channels)
    {
        OutputChannelMapping = channels;
        return this;
    }

    #endregion

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
