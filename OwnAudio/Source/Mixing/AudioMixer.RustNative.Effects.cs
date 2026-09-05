using System;
using System.Collections.Generic;
using System.Threading;
using Logger;
using Ownaudio.Audio.Tracks;
using Ownaudio.Safe;
using OwnaudioNET.Core;
using OwnaudioNET.Effects.VST;
using OwnaudioNET.Engine;
using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Sources;

namespace OwnaudioNET.Mixing;

/// <summary>
/// Master and per-track effect twins, built and reconciled on the tick.
/// </summary>
public sealed partial class AudioMixer
{
    /// <summary>
    /// Routes a managed master effect onto the native master bus: a native twin is built and
    /// the managed params get mirrored onto it. The effect's own Process is never called here.
    /// AddMasterEffect has already rejected anything without an adapter by this point.
    /// </summary>
    /// <param name="effect"></param>
    internal void AttachMasterEffectToRust(IEffectProcessor effect)
    {
        if (!_rustNative || effect is null)
            return;

        //The managed side owns the plugin instance; the rust bridge calls its process entry
        if (effect is VST3EffectProcessor vst)
        {
            if (!vst.CanHostNatively)
            {
                Log.Warning($"[Mixer] Master VST3 '{effect.Name}' is not audio-initialized (await "
                    + "VST3PluginHost.InitializeAudioAsync before adding it), it stays silent");
                return;
            }

            lock (_rustSessionLock)
            {
                _ensureRustSession();

                MasterEffectChain _chain = _rustSession.MasterEffects;
                object _native = _chain.AddVst(
                    vst.NativePluginHandle,
                    vst.NativeProcessAudioPointer,
                    (ushort)_config.Channels,
                    (uint)_config.BufferSize,
                    (uint)Math.Max(0, vst.LatencySamples));

                var _pair = new RustEffectPair(effect, _native, _chain);
                _rustMasterEffects.Add(_pair);
                _mirrorPair(_pair);
            }

            return;
        }

        //AddMasterEffect rejects these up front; getting here means someone bypassed it
        if (!RustEffectAdapters.TryGetEffectType(effect, out var effectType))
        {
            Log.Error($"[Mixer] Master effect '{effect.GetType().Name}' has no native twin, it stays off the bus");
            return;
        }

        lock (_rustSessionLock)
        {
            _ensureRustSession();

            MasterEffectChain _chain = _rustSession.MasterEffects;
            object _native = _chain.Add(effectType, _config.SampleRate);

            var _pair = new RustEffectPair(effect, _native, _chain);
            _rustMasterEffects.Add(_pair);
            _mirrorPair(_pair);
        }
    }

    /// <summary>
    /// Drops the native twin of a master effect. No-op when it was never paired.
    /// </summary>
    /// <param name="effect"></param>
    internal void DetachMasterEffectFromRust(IEffectProcessor effect)
    {
        if (!_rustNative || effect is null)
            return;

        lock (_rustSessionLock)
        {
            int _index = _rustMasterEffects.FindIndex(p => ReferenceEquals(p.Managed, effect));
            if (_index < 0)
                return;

            if (_rustSession is not null) _rustMasterEffects[_index].RemoveNativeBestEffort();

            _rustMasterEffects.RemoveAt(_index);
        }
    }

    /// <summary>
    /// Drops every native master effect.
    /// </summary>
    internal void ClearRustMasterEffects()
    {
        if (!_rustNative)
            return;

        lock (_rustSessionLock)
        {
            if (_rustSession is not null)
            {
                foreach (var pair in _rustMasterEffects)
                    pair.RemoveNativeBestEffort();
            }

            _rustMasterEffects.Clear();
        }
    }

    /// <summary>
    /// One mirroring pass over every paired master effect.
    /// </summary>
    internal void MirrorRustMasterEffectsOnce()
    {
        lock (_rustSessionLock)
        {
            if (_rustSession is null)
                return;

            foreach (var pair in _rustMasterEffects)
                _mirrorPair(pair);
        }
    }

    /// <summary>
    /// Mirrors a managed effect's params onto its native twin, enqueuing only what changed —
    /// pushing everything each tick would overflow the lock-free command queue.
    /// </summary>
    /// <param name="pair"></param>
    private static void _mirrorPair(RustEffectPair pair)
    {
        //A VST needs no special case any more: the rust bridge delays its dry path by the
        //plugin latency, so its own bypass and dry/wet land level with the wet output. That
        //is what the host bypass used to be here for. The plugin's own params still go
        //straight to the plugin; only enable and mix travel through the mirror.
        int _generation = pair.Managed.ResetGeneration;
        if (_generation != pair.LastResetGeneration)
        {
            pair.LastResetGeneration = _generation;
            pair.ResetNative();
        }

        RustEffectAdapters.Mirror(pair.Managed, pair.Sink);
    }

    /// <summary>
    /// Reconciles every wrapper's effect list onto its native track chain and mirrors the
    /// params. Runs on the tick, and straight away on the caller thread whenever a wrapper's
    /// chain is edited.
    /// </summary>
    internal void ReconcileRustTrackEffectsOnce()
    {
        lock (_rustSessionLock)
        {
            foreach (RustTrackEffectRouting routing in _rustEffectSources)
            {
                AudioTrack? _track = routing.Backing.RustTrack;
                if (_track is null) continue;

                int _version = routing.Source.EffectsVersion;
                if (_version != routing.CachedVersion)
                {
                    _rebuildTrackChain(routing, _track.Effects);
                    routing.CachedVersion = _version;
                }

                foreach (var pair in routing.Pairs)
                    _mirrorPair(pair);
            }
        }
    }

    /// <summary>
    /// Brings one track's native chain in line with its wrapper. Everything up to the first
    /// difference stays put — appending an effect mid-playback used to wipe every reverb tail
    /// and compressor envelope on the track, which is plainly audible.
    /// </summary>
    /// <param name="routing"></param>
    /// <param name="chain">the track's native chain</param>
    private void _rebuildTrackChain(RustTrackEffectRouting routing, TrackEffectChain chain)
    {
        IEffectProcessor[] _managed = routing.Source.GetEffects();

        int _keep = 0;
        while (_keep < routing.Pairs.Count && _keep < _managed.Length
            && ReferenceEquals(routing.Pairs[_keep].Managed, _managed[_keep]))
        {
            _keep++;
        }

        for (int i = _keep; i < routing.Pairs.Count; i++)
            routing.Pairs[i].RemoveNativeBestEffort();

        routing.Pairs.RemoveRange(_keep, routing.Pairs.Count - _keep);

        for (int i = _keep; i < _managed.Length; i++)
        {
            IEffectProcessor _effect = _managed[i];

            if (_effect is VST3EffectProcessor vst && vst.CanHostNatively)
            {
                object _native = chain.AddVst(
                    vst.NativePluginHandle,
                    vst.NativeProcessAudioPointer,
                    (ushort)_config.Channels,
                    (uint)_config.BufferSize,
                    (uint)Math.Max(0, vst.LatencySamples));
                routing.Pairs.Add(new RustEffectPair(_effect, _native, chain));
            }
            else if (RustEffectAdapters.TryGetEffectType(_effect, out var effectType))
            {
                object _native = chain.Add(effectType, (float)_config.SampleRate);
                routing.Pairs.Add(new RustEffectPair(_effect, _native, chain));
            }
            else
            {
                //AddEffect rejects these through NativeChainValidator, so this is belt and braces —
                //and it must not throw: the tick runs the same rebuild, and a throwing one here
                //would take the master clock and the EOS poll down with it every 15 ms
                Log.Error($"[Mixer] Effect '{_effect.GetType().Name}' on source '{routing.Source.Id}' has no "
                    + "native twin, it is skipped");
            }
        }
    }
}
