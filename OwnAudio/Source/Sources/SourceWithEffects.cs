using System.Runtime.CompilerServices;
using System.Threading;
using Logger;
using Ownaudio;
using Ownaudio.Core;
using OwnaudioNET.Core;
using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Synchronization;

namespace OwnaudioNET.Sources;

/// <summary>
/// Decorator that bolts an effect chain onto any IAudioSource. Delegates everything to the inner
/// source, intercepts ReadSamples to run the fx. Effect list is thread-safe.
/// </summary>
public sealed class SourceWithEffects : IAudioSource, IMasterClockSource
{
    private readonly IAudioSource _innerSource;

    /// <summary>
    /// The inner source when it can ride a master clock, null otherwise. Cached so the
    /// clock members don't type-test on every call.
    /// </summary>
    private readonly IMasterClockSource? _clockInner;

    /// <summary>
    /// The inner source as a BaseAudioSource, null for a hand-rolled IAudioSource. Channel
    /// routing lives there, not on the interface.
    /// </summary>
    private readonly BaseAudioSource? _baseInner;

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

    /// <summary>
    /// Throwing effects are counted, not logged per buffer — the read path runs at audio rate.
    /// First hit gets a line, the rest are summed up and reported on reset/dispose.
    /// </summary>
    private int _effectFaults;

    /// <summary>
    /// Latches the first-hit report above.
    /// </summary>
    private bool _effectFaultLogged;

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
        _clockInner = source as IMasterClockSource;
        _baseInner = source as BaseAudioSource;
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

    #region MasterClock (delegated to inner source)

    /// <summary>
    /// True when the wrapped source can ride a master clock at all. A decorator over a plain
    /// IAudioSource still exposes the clock surface, it just has nothing to hand the calls to.
    /// </summary>
    public bool SupportsMasterClock => _clockInner is not null;

    /// <inheritdoc/>
    public double StartOffset
    {
        get => _clockInner?.StartOffset ?? 0.0;
        set
        {
            if (_clockInner is null) { _warnNoClock(nameof(StartOffset)); return; }
            _clockInner.StartOffset = value;
        }
    }

    /// <inheritdoc/>
    public bool IsAttachedToClock => _clockInner?.IsAttachedToClock ?? false;

    /// <inheritdoc/>
    public void AttachToClock(MasterClock clock)
    {
        _throwIfDisposed();

        if (_clockInner is null) { _warnNoClock(nameof(AttachToClock)); return; }
        _clockInner.AttachToClock(clock);
    }

    /// <inheritdoc/>
    public void DetachFromClock() => _clockInner?.DetachFromClock();

    /// <summary>
    /// Clock-aligned read with the fx chain on top. Falls back to a plain read when the inner
    /// source doesn't do timestamps - the effects still run either way.
    /// </summary>
    /// <param name="masterTimestamp"></param>
    /// <param name="buffer"></param>
    /// <param name="frameCount"></param>
    /// <param name="result"></param>
    public bool ReadSamplesAtTime(double masterTimestamp, Span<float> buffer, int frameCount, out ReadResult result)
    {
        _throwIfDisposed();

        bool _ok = true;

        if (_clockInner is not null)
            _ok = _clockInner.ReadSamplesAtTime(masterTimestamp, buffer, frameCount, out result);
        else
            result = ReadResult.CreateSuccess(_innerSource.ReadSamples(buffer, frameCount));

        result.FramesRead = _runEffectChain(buffer, result.FramesRead);
        return _ok;
    }

    /// <summary>
    /// One line per wrapper about a clock call the inner source can't take.
    /// </summary>
    /// <param name="member"></param>
    private void _warnNoClock(string member)
    {
        Log.Warning($"[SourceFx] {member} ignored on source '{Id}': {_innerSource.GetType().Name} does not ride a master clock");
    }

    #endregion

    #region Channel Routing (delegated to inner source)

    /// <summary>
    /// Per-source output routing, straight through to the wrapped source. Null when the inner
    /// one isn't a BaseAudioSource - there's nowhere to keep a map then.
    /// </summary>
    public int[]? OutputChannelMapping
    {
        get => _baseInner?.OutputChannelMapping;
        set
        {
            if (_baseInner is null)
            {
                Log.Warning($"[SourceFx] Channel map ignored on source '{Id}': {_innerSource.GetType().Name} has no routing");
                return;
            }

            _baseInner.OutputChannelMapping = value;
        }
    }

    /// <summary>
    /// Fluent shortcut for OutputChannelMapping, hands the wrapper back so the fx chain
    /// can keep being built on it.
    /// </summary>
    /// <param name="channels"></param>
    /// <returns></returns>
    public SourceWithEffects RouteToChannels(params int[] channels)
    {
        OutputChannelMapping = channels;
        return this;
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
        {
            Log.Error($"[SourceFx] Effect '{effect.Name}' rejected on source '{Id}': not ready for audio");
            throw new InvalidOperationException(
                $"Effect '{effect.Name}' is not ready for audio processing. " +
                $"For VST3 effects call and await VST3PluginHost.InitializeAudioAsync() first.");
        }

        lock (_effectsLock)
        {
            effect.Initialize(Config);
            _effects.Add(effect);
            _effectsChanged = true;
            _effectsVersion++;

            Log.Info($"[SourceFx] '{effect.Name}' added to source '{Id}' ({_effects.Count} in chain, "
                + $"{effect.LatencySamples} samples latency)");
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
                Log.Info($"[SourceFx] '{effect.Name}' removed from source '{Id}' ({_effects.Count} left)");
            }
            else
            {
                Log.Warning($"[SourceFx] '{effect?.Name}' is not on source '{Id}', remove ignored");
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
            int _had = _effects.Count;
            _effects.Clear();
            _effectsChanged = true;
            _effectsVersion++;

            if (_had > 0) Log.Info($"[SourceFx] Chain of source '{Id}' cleared ({_had} effects)");
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
    /// Same sum, but only over the effects actually running. A bypassed lookahead limiter delays
    /// nothing, so this is what an analyzer needs to line the dry and wet signal up - PDC uses the
    /// figure above instead, which stays put across a bypass toggle.
    /// </summary>
    public int ActiveEffectLatencySamples
    {
        get
        {
            lock (_effectsLock)
            {
                int _total = 0;
                foreach (var e in _effects) if (e.Enabled) _total += e.LatencySamples;
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

        Log.Info($"[SourceFx] PDC on source '{Id}' set to {samples} frames");
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
        return _runEffectChain(buffer, _innerSource.ReadSamples(buffer, frameCount));
    }

    /// <summary>
    /// Runs the chain plus PDC over what the inner source just gave us. Hot path, zero-alloc
    /// after warmup.
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="framesRead"></param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int _runEffectChain(Span<float> buffer, int framesRead)
    {
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
            catch (Exception ex)
            {
                _effectFaults++;
                if (!_effectFaultLogged)
                {
                    _effectFaultLogged = true;
                    Log.Error($"[SourceFx] Effect '{effect.Name}' on source '{Id}' threw, its block is passed through dry", ex);
                }
            }
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
    /// Dumps how many buffers the effect chain dropped since the last report, then arms the
    /// first-hit line again. Called off the read path.
    /// </summary>
    private void _reportEffectFaults()
    {
        if (_effectFaults == 0) return;

        Log.Error($"[SourceFx] Source '{Id}' passed {_effectFaults} blocks through dry on effect failures");
        _effectFaults = 0;
        _effectFaultLogged = false;
    }

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
                catch (Exception ex) { Log.Error($"[SourceFx] Effect '{effect.Name}' failed to reset, it keeps its old tail", ex); }
            }
        }

        _reportEffectFaults();

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

        _reportEffectFaults();

        lock (_effectsLock)
        {
            foreach (var effect in _effects)
            {
                try { effect?.Dispose(); }
                catch (Exception ex) { Log.Error($"[SourceFx] Effect '{effect?.Name}' dispose failed", ex); }
            }
            _effects.Clear();
        }

        try { _innerSource?.Dispose(); }
        catch (Exception ex) { Log.Error($"[SourceFx] Inner source of '{Id}' failed to dispose", ex); }

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
