using System.Runtime.CompilerServices;
using Ownaudio;
using Ownaudio.Core;
using OwnaudioNET.Core;
using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Sources;

/// <summary>
/// Wrapper class that adds effect processing capabilities to any IAudioSource.
/// This is an optional decorator that applies effects to a source's audio output.
///
/// Usage:
/// <code>
/// var fileSource = new FileSource("music.mp3", engine);
/// var sourceWithEffects = new SourceWithEffects(fileSource);
/// sourceWithEffects.AddEffect(new LowPassFilter(0.5f));
/// sourceWithEffects.AddEffect(new DelayEffect(300f, 0.3f));
/// mixer.AddSource(sourceWithEffects);
/// </code>
///
/// Architecture:
/// - Delegates all IAudioSource methods to the inner source
/// - Intercepts ReadSamples() to apply effect chain
/// - Thread-safe effect management
/// </summary>
public sealed class SourceWithEffects : IAudioSource
{
    private readonly IAudioSource _innerSource;
    private readonly List<IEffectProcessor> _effects;
    private readonly object _effectsLock = new();
    private bool _disposed;
    private IEffectProcessor[] _cachedEffects = Array.Empty<IEffectProcessor>();
    private volatile bool _effectsChanged = false;

    #region Plugin Delay Compensation Fields

    /// <summary>
    /// Ring buffer used to introduce a sample-accurate delay for PDC alignment.
    /// Null when no delay compensation is active.
    /// </summary>
    private float[]? _delayBuffer;

    /// <summary>
    /// Current write position inside the circular delay buffer.
    /// Advances by one sample each call to <see cref="ApplyDelayCompensation"/>.
    /// </summary>
    private int _delayWritePos;

    /// <summary>
    /// Current read position inside the circular delay buffer.
    /// Lags <see cref="_delayWritePos"/> by exactly <see cref="_compensationSamples"/> frames.
    /// </summary>
    private int _delayReadPos;

    /// <summary>
    /// Number of frames of delay currently applied to this source for PDC.
    /// Zero means compensation is disabled and <see cref="_delayBuffer"/> is null.
    /// </summary>
    private int _compensationSamples;

    #endregion

    /// <summary>
    /// Initializes a new instance of the SourceWithEffects class.
    /// </summary>
    /// <param name="source">The source to wrap with effect processing.</param>
    /// <exception cref="ArgumentNullException">Thrown when source is null.</exception>
    public SourceWithEffects(IAudioSource source)
    {
        _innerSource = source ?? throw new ArgumentNullException(nameof(source));
        _effects = new List<IEffectProcessor>();
    }

    #region IAudioSource Properties (delegated to inner source)

    /// <summary>
    /// Gets the wrapped inner source. Used by the Rust-native mixer facade to reach the underlying
    /// <c>FileSource</c> (and its native track) so the wrapper's effects can be routed to the native
    /// per-track effect chain instead of the managed <see cref="Process"/> path.
    /// </summary>
    internal IAudioSource InnerSource => _innerSource;

    /// <inheritdoc/>
    public Guid Id => _innerSource.Id;

    /// <inheritdoc/>
    public AudioState State => _innerSource.State;

    /// <inheritdoc/>
    public AudioConfig Config => _innerSource.Config;

    /// <inheritdoc/>
    public AudioStreamInfo StreamInfo => _innerSource.StreamInfo;

    /// <inheritdoc/>
    public float Volume
    {
        get => _innerSource.Volume;
        set => _innerSource.Volume = value;
    }

    /// <inheritdoc/>
    public bool Loop
    {
        get => _innerSource.Loop;
        set => _innerSource.Loop = value;
    }

    /// <inheritdoc/>
    public double Position => _innerSource.Position;

    /// <inheritdoc/>
    public double Duration => _innerSource.Duration;

    /// <inheritdoc/>
    public bool IsEndOfStream => _innerSource.IsEndOfStream;

    /// <inheritdoc/>
    public float Tempo
    {
        get => _innerSource.Tempo;
        set => _innerSource.Tempo = value;
    }

    /// <inheritdoc/>
    public float PitchShift
    {
        get => _innerSource.PitchShift;
        set => _innerSource.PitchShift = value;
    }

    #endregion

    #region Effect Management

    /// <summary>
    /// Adds an effect to this source's effect chain.
    /// Effects are processed in the order they are added.
    /// </summary>
    /// <param name="effect">The effect to add.</param>
    /// <exception cref="ArgumentNullException">Thrown when effect is null.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if wrapper is disposed.</exception>
    public void AddEffect(IEffectProcessor effect)
    {
        ThrowIfDisposed();

        if (effect == null)
            throw new ArgumentNullException(nameof(effect));

        if (!effect.IsReady)
            throw new InvalidOperationException(
                $"Effect '{effect.Name}' is not ready for audio processing. " +
                $"For VST3 effects call and await VST3PluginHost.InitializeAudioAsync() first.");

        lock (_effectsLock)
        {
            effect.Initialize(Config);
            _effects.Add(effect);
            _effectsChanged = true;
        }
    }

    /// <summary>
    /// Removes an effect from this source's effect chain.
    /// </summary>
    /// <param name="effect">The effect to remove.</param>
    /// <returns>True if removed successfully, false if not found.</returns>
    /// <exception cref="ArgumentNullException">Thrown when effect is null.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if wrapper is disposed.</exception>
    public bool RemoveEffect(IEffectProcessor effect)
    {
        ThrowIfDisposed();

        if (effect == null)
            throw new ArgumentNullException(nameof(effect));

        lock (_effectsLock)
        {
            bool removed = _effects.Remove(effect);
            if (removed)
                _effectsChanged = true;
            return removed;
        }
    }

    /// <summary>
    /// Clears all effects from this source's effect chain.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if wrapper is disposed.</exception>
    public void ClearEffects()
    {
        ThrowIfDisposed();

        lock (_effectsLock)
        {
            _effects.Clear();
            _effectsChanged = true;
        }
    }

    /// <summary>
    /// Gets all effects in this source's effect chain.
    /// </summary>
    /// <returns>Array of effects (snapshot).</returns>
    public IEffectProcessor[] GetEffects()
    {
        lock (_effectsLock)
        {
            return _effects.ToArray();
        }
    }

    /// <summary>
    /// Gets the number of effects in this source's chain.
    /// </summary>
    public int EffectCount
    {
        get
        {
            lock (_effectsLock)
            {
                return _effects.Count;
            }
        }
    }

    #endregion

    #region Plugin Delay Compensation

    /// <summary>
    /// Gets the total effect chain latency in samples.
    /// </summary>
    /// <remarks>
    /// Sums the <see cref="IEffectProcessor.LatencySamples"/> value of every effect
    /// currently in the chain. Zero-latency effects (EQ, compressor, reverb) return 0
    /// and do not contribute. Call this before <see cref="AudioMixer.ApplyPluginDelayCompensation"/>
    /// to determine the per-track latency.
    /// This property acquires the effects lock briefly and must NOT be called from
    /// the real-time audio thread.
    /// </remarks>
    public int EffectLatencySamples
    {
        get
        {
            lock (_effectsLock)
            {
                int total = 0;
                foreach (var e in _effects)
                    total += e.LatencySamples;
                return total;
            }
        }
    }

    /// <summary>
    /// Sets the delay compensation for this source in samples.
    /// </summary>
    /// <remarks>
    /// Called by <see cref="AudioMixer.ApplyPluginDelayCompensation"/> before playback
    /// starts. The compensation equals
    /// <c>maxLatencyAcrossAllTracks − thisTrack.EffectLatencySamples</c>.
    /// A ring buffer of <paramref name="samples"/> × <see cref="Config.Channels"/>
    /// floats is allocated. Passing 0 disables compensation and releases the buffer.
    /// </remarks>
    /// <param name="samples">
    /// Number of delay frames to apply. Must be ≥ 0.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="samples"/> is negative.
    /// </exception>
    /// <exception cref="ObjectDisposedException">Thrown if the wrapper is disposed.</exception>
    public void SetDelayCompensation(int samples)
    {
        ThrowIfDisposed();

        if (samples < 0)
            throw new ArgumentOutOfRangeException(nameof(samples));

        _compensationSamples = samples;

        if (samples > 0)
        {
            int bufferSize = samples * Config.Channels;
            _delayBuffer = new float[bufferSize];
            _delayWritePos = 0;
            _delayReadPos = 0;
        }
        else
        {
            _delayBuffer = null;
        }
    }

    #endregion

    #region IAudioSource Methods (with effect processing)

    /// <summary>
    /// Reads audio samples and applies effect chain.
    /// This is the hot path - zero allocation after initialization.
    /// </summary>
    /// <param name="buffer">The buffer to fill with audio data.</param>
    /// <param name="frameCount">The number of frames to read.</param>
    /// <returns>The actual number of frames read.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadSamples(Span<float> buffer, int frameCount)
    {
        ThrowIfDisposed();

        int framesRead = _innerSource.ReadSamples(buffer, frameCount);

        if (framesRead == 0)
            return 0;

        if (_effectsChanged)
        {
            lock (_effectsLock)
            {
                if (_effectsChanged)
                {
                    _cachedEffects = _effects.ToArray();
                    _effectsChanged = false;
                }
            }
        }

        var effects = _cachedEffects;
        if (effects.Length == 0)
            return framesRead;

        foreach (var effect in effects)
        {
            try
            {
                if (effect.Enabled)
                    effect.Process(buffer, framesRead);
            }
            catch {}
        }

        if (_compensationSamples > 0 && _delayBuffer != null)
            framesRead = ApplyDelayCompensation(buffer, framesRead);

        return framesRead;
    }

    /// <inheritdoc/>
    public bool Seek(double positionInSeconds)
    {
        ThrowIfDisposed();

        lock (_effectsLock)
        {
            foreach (var effect in _effects)
            {
                try
                {
                    effect.Reset();
                }
                catch {}
            }
        }

        if (_delayBuffer != null)
        {
            Array.Clear(_delayBuffer, 0, _delayBuffer.Length);
            _delayWritePos = 0;
            _delayReadPos = 0;
        }

        return _innerSource.Seek(positionInSeconds);
    }

    /// <inheritdoc/>
    public void Play()
    {
        ThrowIfDisposed();
        _innerSource.Play();
    }

    /// <inheritdoc/>
    public void Pause()
    {
        ThrowIfDisposed();
        _innerSource.Pause();
    }

    /// <inheritdoc/>
    public void Stop()
    {
        ThrowIfDisposed();

        lock (_effectsLock)
        {
            foreach (var effect in _effects)
            {
                try
                {
                    effect.Reset();
                }
                catch {}
            }
        }

        if (_delayBuffer != null)
        {
            Array.Clear(_delayBuffer, 0, _delayBuffer.Length);
            _delayWritePos = 0;
            _delayReadPos = 0;
        }

        _innerSource.Stop();
    }

    #endregion

    #region Events (delegated to inner source)

    /// <inheritdoc/>
    public event EventHandler<AudioStateChangedEventArgs>? StateChanged
    {
        add => _innerSource.StateChanged += value;
        remove => _innerSource.StateChanged -= value;
    }

    /// <inheritdoc/>
    public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun
    {
        add => _innerSource.BufferUnderrun += value;
        remove => _innerSource.BufferUnderrun -= value;
    }

    /// <inheritdoc/>
    public event EventHandler<AudioErrorEventArgs>? Error
    {
        add => _innerSource.Error += value;
        remove => _innerSource.Error -= value;
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Applies ring-buffer delay compensation to align this source with
    /// higher-latency tracks in the mix.
    /// </summary>
    /// <remarks>
    /// Writes incoming samples into the circular delay buffer and simultaneously
    /// reads back samples that are <c>_compensationSamples</c> frames older,
    /// effectively introducing a fixed delay equal to the compensation amount.
    /// This is a zero-allocation, zero-lock hot path.
    /// </remarks>
    /// <param name="buffer">The audio buffer to delay in-place.</param>
    /// <param name="framesRead">The number of valid frames in the buffer.</param>
    /// <returns>The number of frames written back to <paramref name="buffer"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ApplyDelayCompensation(Span<float> buffer, int framesRead)
    {
        if (_delayBuffer == null)
            return framesRead;

        int channels = Config.Channels;
        int sampleCount = framesRead * channels;

        for (int i = 0; i < sampleCount; i++)
        {
            _delayBuffer[_delayWritePos] = buffer[i];
            _delayWritePos = (_delayWritePos + 1) % _delayBuffer.Length;

            buffer[i] = _delayBuffer[_delayReadPos];
            _delayReadPos = (_delayReadPos + 1) % _delayBuffer.Length;
        }

        return framesRead;
    }

    #endregion

    #region Dispose

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SourceWithEffects));
    }

    /// <summary>
    /// Disposes the wrapper and all effects.
    /// The inner source is also disposed.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        lock (_effectsLock)
        {
            foreach (var effect in _effects)
            {
                try
                {
                    effect?.Dispose();
                }
                catch {}
            }
            _effects.Clear();
        }

        try
        {
            _innerSource?.Dispose();
        }
        catch {}

        _disposed = true;
    }

    #endregion

    /// <summary>
    /// Returns a string representation of the source with effect info.
    /// </summary>
    public override string ToString()
    {
        int effectCount = EffectCount;
        return $"SourceWithEffects: InnerSource={_innerSource.GetType().Name}, Effects={effectCount}, State={State}";
    }
}
