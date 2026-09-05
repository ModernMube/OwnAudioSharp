using System;
using System.Collections.Generic;
using Ownaudio.Audio.Effects;
using Ownaudio.Core;
using Ownaudio.Safe.Effects;
using OwnaudioNET.Effects.VST;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Mixing;

namespace OwnaudioNET.Effects;

/// <summary>
/// Standalone native DSP behind a managed IEffectProcessor. Mixer twins stay
/// on the rust session; this one is only for Process() / Matchering / ReadSamples.
/// </summary>
internal sealed class NativeEffectEngine : IDisposable
{
    /// <summary>
    /// Native meter id: the gain the effect applies right now, linear. Read-only, the
    /// mirror never writes it.
    /// </summary>
    internal const uint MeterCurrentGain = 1000;

    /// <summary>
    /// Native meter id: detected input level, linear.
    /// </summary>
    internal const uint MeterInputLevel = 1001;

    /// <summary>
    /// Serialises the native instance. Process may run on a decode thread while the UI
    /// pushes a parameter or disposes the effect, and two of those inside one rust
    /// effect at the same time is a data race, not just a torn value.
    /// </summary>
    private readonly object _gate = new object();

    /// <summary>
    /// Last value pushed per native param id, so a Process only sends what moved.
    /// </summary>
    private readonly Dictionary<uint, float> _lastParams = new Dictionary<uint, float>();

    /// <summary>
    /// Bound once — Mirror takes a delegate, and a lambda per Process would allocate
    /// on a path the interface documents as in-place.
    /// </summary>
    private readonly RustEffectAdapters.ParamSink _sink;

    private StandaloneEffect? _fx;
    private int _sampleRate;
    private int _channels;
    private bool _disposed;

    /// <summary>
    /// Set for a VST bridge: the plugin has to see every block even while bypassed, or it
    /// goes cold and its first resumed block does non-realtime work. The bridge emits the
    /// delayed dry itself.
    /// </summary>
    private bool _drivenWhileBypassed;

    /// <summary>
    /// Largest block the VST bridge was sized for; a wider one rebuilds it, the way the
    /// managed path used to reallocate its planar scratch.
    /// </summary>
    private int _maxBlock;

    internal NativeEffectEngine()
    {
        _sink = _pushParam;
    }

    /// <summary>
    /// Native instance is up.
    /// </summary>
    internal bool IsReady
    {
        get { lock (_gate) return _fx != null; }
    }

    /// <summary>
    /// Look-ahead in frames, 0 until Initialize.
    /// </summary>
    internal int LatencySamples
    {
        get { lock (_gate) return _fx?.LatencySamples ?? 0; }
    }

    /// <summary>
    /// Builds (or rebuilds on rate/channel change) the native effect and pushes
    /// the current managed params onto it.
    /// </summary>
    internal void Initialize(IEffectProcessor effect, AudioConfig config)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));

        if (effect is VST3EffectProcessor vst)
        {
            InitializeVst(vst, config.SampleRate, config.Channels, config.BufferSize);
            return;
        }

        if (!RustEffectAdapters.TryGetEffectType(effect, out EffectType type))
            throw new InvalidOperationException(
                $"Effect '{effect.GetType().Name}' has no native engine — Process() cannot run.");

        int _rate = config.SampleRate;
        int _ch = Math.Max(1, config.Channels);

        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NativeEffectEngine));

            if (_fx != null && _sampleRate == _rate && _channels == _ch)
            {
                _mirror(effect);
                return;
            }

            _fx?.Dispose();
            _fx = new StandaloneEffect(type, _rate, _ch);
            _sampleRate = _rate;
            _channels = _ch;
            _lastParams.Clear();
            _mirror(effect);
        }
    }

    /// <summary>
    /// Mirrors the params that moved, then runs the native process in place.
    /// </summary>
    internal void Process(IEffectProcessor effect, Span<float> buffer, int frameCount)
    {
        lock (_gate)
        {
            if (_fx == null)
                throw new InvalidOperationException("Effect not initialized. Call Initialize() first.");
            if (frameCount <= 0 || buffer.IsEmpty) return;
            if (!effect.Enabled && !_drivenWhileBypassed) return;

            _mirror(effect);
            _fx.Process(buffer, frameCount);
        }
    }

    /// <summary>
    /// Same mirror the mixer control tick uses, for ApplyConfiguration and friends.
    /// </summary>
    internal void Push(IEffectProcessor effect)
    {
        lock (_gate)
        {
            if (_fx != null) _mirror(effect);
        }
    }

    /// <summary>
    /// Reads a native param back — meters live on ids the managed model has no field for.
    /// Null while the instance is down or the id is not on this effect.
    /// </summary>
    internal float? GetParam(uint paramId)
    {
        lock (_gate) return _fx?.GetParam(paramId);
    }

    /// <summary>
    /// Native reset, params untouched.
    /// </summary>
    internal void Reset()
    {
        lock (_gate) _fx?.Reset();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _fx?.Dispose();
            _fx = null;
        }
    }

    /// <summary>
    /// The VST bridge is built from the plugin instance, not from a type tag, and it is sized
    /// for a block rather than a sample rate. channels is the bridge's own width — the wider
    /// of the mixer's and the plugin's — not whatever the mixer config happens to say.
    /// </summary>
    internal void InitializeVst(VST3EffectProcessor vst, int sampleRate, int channels, int blockFrames)
    {
        if (!vst.CanHostNatively)
            throw new InvalidOperationException(
                $"VST3 '{vst.Name}' is not audio-initialized — await VST3PluginHost.InitializeAudioAsync() first.");

        int _ch = Math.Max(1, channels);
        int _block = Math.Max(1, blockFrames);
        int _latency = Math.Max(0, vst.LatencySamples);

        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NativeEffectEngine));

            _drivenWhileBypassed = true;

            if (_fx != null && _channels == _ch && _maxBlock >= _block)
            {
                _mirror(vst);
                return;
            }

            _fx?.Dispose();
            _fx = StandaloneEffect.ForVst(
                vst.NativePluginHandle, vst.NativeProcessAudioPointer, _ch, _block, _latency);
            _sampleRate = sampleRate;
            _channels = _ch;
            _maxBlock = _block;
            _lastParams.Clear();
            _mirror(vst);
        }
    }

    /// <summary>
    /// Grows the VST bridge for a block wider than it was built for. The engine skips an
    /// oversized block instead of allocating on the caller's thread, so this has to happen
    /// before Process sees it.
    /// </summary>
    internal void EnsureVstBlock(VST3EffectProcessor vst, int sampleRate, int channels, int frameCount)
    {
        lock (_gate)
        {
            if (_fx == null || frameCount <= _maxBlock) return;
        }

        InitializeVst(vst, sampleRate, channels, frameCount);
    }

    /// <summary>
    /// Call under _gate.
    /// </summary>
    private void _mirror(IEffectProcessor effect)
    {
        RustEffectAdapters.Mirror(effect, _sink);
    }

    /// <summary>
    /// Drops the ids the value hasn't moved on. SmartMaster alone mirrors 92 params, and
    /// resending every one of them per block is 92 p/invokes plus a rust error string for
    /// each id the effect doesn't know.
    /// </summary>
    private void _pushParam(uint paramId, float value)
    {
        if (_fx == null) return;
        if (_lastParams.TryGetValue(paramId, out float _last) && _last.Equals(value)) return;

        _lastParams[paramId] = value;
        _fx.SetParam(paramId, value);
    }
}
