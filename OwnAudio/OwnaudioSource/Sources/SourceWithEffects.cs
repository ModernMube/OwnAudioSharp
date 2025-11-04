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
///
/// Performance:
/// - Zero allocation in hot path (after initialization)
/// - Effects processed in order they were added
/// - Minimal overhead when no effects are active
/// </summary>
public sealed class SourceWithEffects : IAudioSource
{
    private readonly IAudioSource _innerSource;
    private readonly List<IEffectProcessor> _effects;
    private readonly object _effectsLock = new();
    private bool _disposed;

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

        lock (_effectsLock)
        {
            // Initialize effect with source's audio config
            effect.Initialize(Config);
            _effects.Add(effect);
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
            return _effects.Remove(effect);
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

        // 1. Read from inner source
        int framesRead = _innerSource.ReadSamples(buffer, frameCount);

        if (framesRead == 0)
            return 0;

        // 2. Apply effect chain (if any effects exist)
        lock (_effectsLock)
        {
            if (_effects.Count == 0)
                return framesRead; // Fast path: no effects

            // Process each effect in order
            foreach (var effect in _effects)
            {
                try
                {
                    if (effect.Enabled)
                    {
                        effect.Process(buffer, framesRead);
                    }
                }
                catch
                {
                    // Effect processing error - skip this effect and continue
                    // In production, log via ILogger
                }
            }
        }

        return framesRead;
    }

    /// <inheritdoc/>
    public bool Seek(double positionInSeconds)
    {
        ThrowIfDisposed();

        // When seeking, reset all effects to clear their buffers
        lock (_effectsLock)
        {
            foreach (var effect in _effects)
            {
                try
                {
                    effect.Reset();
                }
                catch
                {
                    // Ignore reset errors
                }
            }
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

        // Reset effects when stopping
        lock (_effectsLock)
        {
            foreach (var effect in _effects)
            {
                try
                {
                    effect.Reset();
                }
                catch
                {
                    // Ignore reset errors
                }
            }
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

        // Dispose all effects
        lock (_effectsLock)
        {
            foreach (var effect in _effects)
            {
                try
                {
                    effect?.Dispose();
                }
                catch
                {
                    // Ignore disposal errors
                }
            }
            _effects.Clear();
        }

        // Dispose inner source
        try
        {
            _innerSource?.Dispose();
        }
        catch
        {
            // Ignore disposal errors
        }

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
