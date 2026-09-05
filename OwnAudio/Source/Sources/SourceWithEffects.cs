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
/// Wraps any IAudioSource and runs an effect chain over what it reads. Everything else is
/// delegated straight to the inner source. The chain, the read path and teardown live here;
/// the pass-through members and the routing sit in the sibling partials.
/// </summary>
public sealed partial class SourceWithEffects : IAudioSource, IMasterClockSource, ISynchronizable
{
    private readonly IAudioSource _innerSource;

    /// <summary>
    /// The inner source when it can ride a master clock, null otherwise. Cached so the
    /// clock members don't type-test on every call.
    /// </summary>
    private readonly IMasterClockSource? _clockInner;

    /// <summary>
    /// The inner source when it can be sync-grouped, null for a hand-rolled IAudioSource.
    /// </summary>
    private readonly ISynchronizable? _syncInner;

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
    /// Set by the mixer while this wrapper sits on a native track: rebuilds the native chain
    /// right here on the caller thread. Without it the tick would only catch up 15 ms later,
    /// and a removed effect would keep running — straight into a use-after-free if the caller
    /// disposes it in the meantime. No-op while we're not on a mixer.
    /// </summary>
    internal Action NativeChainReconciler { get; set; } = NoNativeChain;

    /// <summary>
    /// What NativeChainReconciler falls back to off a mixer. Non-null on purpose, a nullable
    /// member here flips the type's nullable context and churns the frozen api baseline.
    /// </summary>
    internal static readonly Action NoNativeChain = static () => { };

    /// <summary>
    /// Set by the mixer next to the reconciler: throws for an effect the native chain can't host.
    /// It runs before the effect goes on the list, so the rebuild never meets one it has to skip.
    /// Off a mixer anything goes — there ReadSamples calling Process is the chain.
    /// </summary>
    internal Action<IEffectProcessor> NativeChainValidator { get; set; } = NoNativeChainCheck;

    /// <summary>
    /// The off-mixer fallback for the validator, same nullable-context reason as above.
    /// </summary>
    internal static readonly Action<IEffectProcessor> NoNativeChainCheck = static _ => { };

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
        _syncInner = source as ISynchronizable;
        _baseInner = source as BaseAudioSource;
        _effects = new List<IEffectProcessor>();
    }

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

        NativeChainValidator(effect);

        lock (_effectsLock)
        {
            effect.Initialize(Config);
            _effects.Add(effect);
            _effectsChanged = true;
            _effectsVersion++;

            Log.Info($"[SourceFx] '{effect.Name}' added to source '{Id}' ({_effects.Count} in chain, "
                + $"{effect.LatencySamples} samples latency)");
        }

        NativeChainReconciler();
    }

    /// <summary>
    /// Drops an effect from the chain. The native twin is gone by the time this returns,
    /// so disposing the effect right after is safe.
    /// </summary>
    /// <param name="effect"></param>
    /// <returns></returns>
    public bool RemoveEffect(IEffectProcessor effect)
    {
        _throwIfDisposed();

        bool _removed;
        lock (_effectsLock)
        {
            _removed = _effects.Remove(effect);
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
        }

        //Outside the lock on purpose: the tick grabs the session lock first, we'd deadlock
        if (_removed) NativeChainReconciler();

        return _removed;
    }

    /// <summary>
    /// Wipes the whole chain, native twins included.
    /// </summary>
    public void ClearEffects()
    {
        _throwIfDisposed();

        int _had;
        lock (_effectsLock)
        {
            _had = _effects.Count;
            _effects.Clear();
            _effectsChanged = true;
            _effectsVersion++;

            if (_had > 0) Log.Info($"[SourceFx] Chain of source '{Id}' cleared ({_had} effects)");
        }

        if (_had > 0) NativeChainReconciler();
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

        IEffectProcessor[] _doomed;
        lock (_effectsLock)
        {
            _doomed = _effects.ToArray();
            _effects.Clear();
            _effectsVersion++;
        }

        //The native twins have to be off the chain before we free what they point at
        NativeChainReconciler();

        foreach (var effect in _doomed)
        {
            try { effect?.Dispose(); }
            catch (Exception ex) { Log.Error($"[SourceFx] Effect '{effect?.Name}' dispose failed", ex); }
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
