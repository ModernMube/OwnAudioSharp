using System.Runtime.CompilerServices;
using System.Threading;
using Ownaudio;
using Ownaudio.Core;
using OwnaudioNET.Core;
using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Sources;

/// <summary>
/// Decorator that bolts an effect chain onto any IAudioSource. Delegates everything to the inner
/// source, intercepts ReadSamples to run the fx. Effect list is thread-safe.
/// </summary>
public sealed class SourceWithEffects : IAudioSource
{
    private readonly IAudioSource _innerSource;
    private readonly List<IEffectProcessor> _effects;
    private readonly object _effectsLock = new();
    private bool _disposed;
    private IEffectProcessor[] _cachedEffects = Array.Empty<IEffectProcessor>();
    private volatile bool _effectsChanged = false;

    /// <summary>
    /// Bumped on every chain change (add/remove/clear). The native control tick diffs this against
    /// its last-seen value, so it only re-snapshots the fx list when something actually changed
    /// instead of allocating an array every tick. Written under _effectsLock, read lock-free.
    /// </summary>
    private int _effectsVersion;

    #region Plugin Delay Compensation Fields

    /// <summary>
    /// Ring buffer for sample-accurate PDC delay. Null when compensation is off.
    /// </summary>
    private float[]? _delayBuffer;

    /// <summary>
    /// Write cursor in the ring buffer.
    /// </summary>
    private int _delayWritePos;

    /// <summary>
    /// Read cursor, lagging the write cursor by _compensationSamples frames.
    /// </summary>
    private int _delayReadPos;

    /// <summary>
    /// Frames of delay applied for PDC. Zero = off, buffer is null.
    /// </summary>
    private int _compensationSamples;

    #endregion

    /// <summary>
    /// Wraps a source for effect processing.
    /// </summary>
    /// <param name="source"></param>
    public SourceWithEffects(IAudioSource source)
    {
        _innerSource = source ?? throw new ArgumentNullException(nameof(source));
        _effects = new List<IEffectProcessor>();
    }

    #region IAudioSource Propertyes (delegated to inner source)

    /// <summary>
    /// The wrapped source. Native mixer facade reaches through this to route fx to the native
    /// per-track chain instead of the managed Process path.
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
    public float Pan
    {
        get => _innerSource.Pan;
        set => _innerSource.Pan = value;
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
    /// Appends an effect. Run in add order. VST3 fx must be ready first.
    /// </summary>
    /// <param name="effect"></param>
    public void AddEffect(IEffectProcessor effect)
    {
        _throwIfDisposed();

        if (effect == null) throw new ArgumentNullException(nameof(effect));

        if (!effect.IsReady)
            throw new InvalidOperationException(
                $"Effect '{effect.Name}' is not ready for audio processing. " +
                $"For VST3 effects call and await VST3PluginHost.InitializeAudioAsync() first.");

        lock (_effectsLock)
        {
            effect.Initialize(Config);
            _effects.Add(effect);
            _effectsChanged = true;
            _effectsVersion++;
        }
    }

    /// <summary>
    /// Drops an effect from the chain.
    /// </summary>
    /// <param name="effect"></param>
    /// <returns></returns>
    public bool RemoveEffect(IEffectProcessor effect)
    {
        _throwIfDisposed();

        lock (_effectsLock)
        {
            bool _removed = _effects.Remove(effect);
            if (_removed)
            {
                _effectsChanged = true;
                _effectsVersion++;
            }
            return _removed;
        }
    }

    /// <summary>
    /// Wipes the whole chain.
    /// </summary>
    public void ClearEffects()
    {
        _throwIfDisposed();

        lock (_effectsLock)
        {
            _effects.Clear();
            _effectsChanged = true;
            _effectsVersion++;
        }
    }

    /// <summary>
    /// Snapshot of the chain.
    /// </summary>
    /// <returns></returns>
    public IEffectProcessor[] GetEffects()
    {
        lock (_effectsLock) return _effects.ToArray();
    }

    /// <summary>
    /// Monotonic chain version, bumped on every add/remove/clear. A consumer that caches its last
    /// value spots a change with one int compare, no list allocation per poll. Read lock-free.
    /// </summary>
    internal int EffectsVersion => Volatile.Read(ref _effectsVersion);

    /// <summary>
    /// How many effects are in the chain.
    /// </summary>
    public int EffectCount
    {
        get { lock (_effectsLock) return _effects.Count; }
    }

    #endregion

    #region Plugin Delay Compensation

    /// <summary>
    /// Total chain latency in samples - sum of each effect's LatencySamples. Zero-latency fx add nothing.
    /// Grabs the effects lock briefly, so don't call from the RT thread.
    /// </summary>
    public int EffectLatencySamples
    {
        get
        {
            lock (_effectsLock)
            {
                int _total = 0;
                foreach (var e in _effects) _total += e.LatencySamples;
                return _total;
            }
        }
    }

    /// <summary>
    /// Sets PDC delay in frames (maxLatency - thisTrackLatency). Allocates a samples*channels ring buffer.
    /// Zero disables it and frees the buffer.
    /// </summary>
    /// <param name="samples"></param>
    public void SetDelayCompensation(int samples)
    {
        _throwIfDisposed();

        if(samples < 0) throw new ArgumentOutOfRangeException(nameof(samples));

        _compensationSamples = samples;

        if (samples > 0)
        {
            _delayBuffer = new float[samples * Config.Channels];
            _delayWritePos = 0;
            _delayReadPos = 0;
        }
        else
            _delayBuffer = null;
    }

    #endregion

    #region IAudioSource Methods (with effect processing)

    /// <summary>
    /// Reads from the inner source then runs the fx chain. Hot path, zero-alloc after warmup.
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="frameCount"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadSamples(Span<float> buffer, int frameCount)
    {
        _throwIfDisposed();

        int framesRead = _innerSource.ReadSamples(buffer, frameCount);

        if (framesRead == 0) return 0;

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
        if (effects.Length == 0) return framesRead;

        foreach (var effect in effects)
        {
            try
            {
                if (effect.Enabled) effect.Process(buffer, framesRead);
            }
            catch {}
        }

        if (_compensationSamples > 0 && _delayBuffer != null)
            framesRead = _applyDelayCompensation(buffer, framesRead);

        return framesRead;
    }

    /// <inheritdoc/>
    public bool Seek(double positionInSeconds)
    {
        _throwIfDisposed();
        _resetEffectsAndDelay();
        return _innerSource.Seek(positionInSeconds);
    }

    /// <inheritdoc/>
    public void Play()
    {
        _throwIfDisposed();
        _innerSource.Play();
    }

    /// <inheritdoc/>
    public void Pause()
    {
        _throwIfDisposed();
        _innerSource.Pause();
    }

    /// <inheritdoc/>
    public void Stop()
    {
        _throwIfDisposed();
        _resetEffectsAndDelay();
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
    /// Resets every effect and clears the PDC ring, used on Seek/Stop.
    /// </summary>
    private void _resetEffectsAndDelay()
    {
        lock (_effectsLock)
        {
            foreach (var effect in _effects)
            {
                try { effect.Reset(); }
                catch {}
            }
        }

        if (_delayBuffer != null)
        {
            Array.Clear(_delayBuffer, 0, _delayBuffer.Length);
            _delayWritePos = 0;
            _delayReadPos = 0;
        }
    }

    /// <summary>
    /// Ring-buffer delay to line this source up with higher-latency tracks. Writes fresh samples in,
    /// reads back ones _compensationSamples frames older. Zero-alloc, zero-lock hot path.
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="framesRead"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int _applyDelayCompensation(Span<float> buffer, int framesRead)
    {
        if (_delayBuffer == null) return framesRead;

        int sampleCount = framesRead * Config.Channels;

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

    /// <summary>
    /// Throws if we're already disposed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _throwIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SourceWithEffects));
    }

    /// <summary>
    /// Disposes the wrapper, every effect, and the inner source.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        lock (_effectsLock)
        {
            foreach (var effect in _effects)
            {
                try { effect?.Dispose(); }
                catch {}
            }
            _effects.Clear();
        }

        try { _innerSource?.Dispose(); }
        catch {}

        _disposed = true;
    }

    #endregion

    /// <summary>
    /// Debug string with inner type and effect count.
    /// </summary>
    /// <returns></returns>
    public override string ToString()
        => $"SourceWithEffects: InnerSource={_innerSource.GetType().Name}, Effects={EffectCount}, State={State}";
}
