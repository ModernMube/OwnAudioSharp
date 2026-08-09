using System;
using Ownaudio.Audio.Tracks;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Monitoring;
using OwnaudioNET.Sources;

namespace OwnaudioNET.Mixing;

public sealed partial class AudioMixer
{
    /// <summary>
    /// Default tap ring per side, in blocks. Two is enough headroom for a UI-rate drain without
    /// letting the analyzer look at audio that already played a while ago.
    /// </summary>
    private const int TapBufferBlocks = 4;

    /// <summary>
    /// Opens a tap around a source's effect chain, so you can look at the signal before and after
    /// the effects - a spectrum comparison, a scope, whatever. Dispose the tap to stop it.
    /// The tap sits ahead of the track's gain, pan and delay compensation.
    /// </summary>
    /// <param name="sourceId">id of a source registered on this mixer</param>
    /// <param name="capacitySamples">ring size per side, 0 picks a sensible default</param>
    public EffectTap CreateEffectTap(Guid sourceId, int capacitySamples = 0)
    {
        _throwIfDisposed();

        if (!_sources.TryGetValue(sourceId, out IAudioSource? source))
            throw new ArgumentException($"No source with id {sourceId} on this mixer.", nameof(sourceId));

        //Not _resolveRustBacked: that one skips StreamingSource, and a stream has a track to tap too
        IRustNativeChainSource? _backing = source as IRustNativeChainSource
            ?? (source as SourceWithEffects)?.InnerSource as IRustNativeChainSource;

        AudioTrack? _track = _backing?.RustTrack;
        if (_track is null)
            throw new InvalidOperationException(
                "This source has no native track to tap. Effect taps need the rust-native chain.");

        SourceWithEffects? _chain = source as SourceWithEffects;

        _track.StartFxTap(_tapCapacity(capacitySamples));
        return new EffectTap(
            _track,
            () => _chain?.ActiveEffectLatencySamples ?? 0,
            _config.Channels,
            _config.SampleRate);
    }

    /// <summary>
    /// Same thing around the master chain, so it sees the summed mix going into the master
    /// effects and coming out. Ahead of the master gain and pan.
    /// </summary>
    /// <param name="capacitySamples"></param>
    public EffectTap CreateMasterEffectTap(int capacitySamples = 0)
    {
        _throwIfDisposed();

        MultiTrackSession? _session = RustSession;
        if (_session is null)
            throw new InvalidOperationException(
                "No native session yet - add a source (or start playback) before tapping the master bus.");

        _session.StartMasterFxTap(_tapCapacity(capacitySamples));
        return new EffectTap(_session, _masterChainLatency, _config.Channels, _config.SampleRate);
    }

    /// <summary>
    /// Latency of the running master effects, in frames. Bypassed ones delay nothing, so they stay
    /// out of it - the tap needs the delay actually sitting in the signal, not the PDC figure.
    /// </summary>
    private int _masterChainLatency()
    {
        lock (_effectsLock)
        {
            int _total = 0;
            foreach (var e in _masterEffects) if (e.Enabled) _total += e.LatencySamples;
            return _total;
        }
    }

    private int _tapCapacity(int requested) =>
        requested > 0 ? requested : _config.BufferSize * _config.Channels * TapBufferBlocks;
}
