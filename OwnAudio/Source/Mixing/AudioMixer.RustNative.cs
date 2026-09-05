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
/// Control plane of the mixer's rust-native chain: one shared MultiTrackSession owns a
/// track per attached source, and a control-rate tick mirrors volume/pan/loop/effects onto
/// it. No managed MixThread runs here — the native side renders everything. Session state
/// and the shared helpers live here, the rest of the chain in the sibling partials.
/// </summary>
public sealed partial class AudioMixer
{
    /// <summary>
    /// Control tick period in ms. Control-rate, not per buffer, so the P/Invoke cost is
    /// nothing while a live slider still lands fast.
    /// </summary>
    private const int RustControlSyncIntervalMs = 15;

    /// <summary>
    /// Are we on the rust-native chain? Latched in the ctor, stable for life.
    /// </summary>
    private readonly bool _rustNative;

    /// <summary>
    /// Serializes create/mutate/teardown of the shared session and the sync tick.
    /// </summary>
    private readonly object _rustSessionLock = new object();

    /// <summary>
    /// The shared session owning every attached track. Built lazily with the first source.
    /// </summary>
    private MultiTrackSession? _rustSession;

    /// <summary>
    /// Managed master effect to native twin. The managed one is just the param model.
    /// Guarded by _rustSessionLock.
    /// </summary>
    private readonly List<RustEffectPair> _rustMasterEffects = new List<RustEffectPair>();

    /// <summary>
    /// Managed effect + its native twin, plus the last pushed value per param. Change
    /// detection matters: pushing everything each tick would flood the command queue.
    /// The Sink delegate is bound once in the ctor so the tick allocates nothing.
    /// </summary>
    private sealed class RustEffectPair
    {
        /// <summary>
        /// Master chain owning Native, null when it sits on a track chain.
        /// </summary>
        private readonly MasterEffectChain? _masterChain;

        /// <summary>
        /// Track chain owning Native, null when it sits on the master chain.
        /// </summary>
        private readonly TrackEffectChain? _trackChain;

        /// <summary>
        /// Pair whose native effect lives on the session master chain.
        /// </summary>
        /// <param name="managed"></param>
        /// <param name="native"></param>
        /// <param name="chain"></param>
        public RustEffectPair(IEffectProcessor managed, object native, MasterEffectChain chain)
        {
            Managed = managed;
            Native = native;
            _masterChain = chain;
            Sink = _pushParam;
            LastResetGeneration = managed.ResetGeneration;
        }

        /// <summary>
        /// Pair whose native effect lives on a track chain.
        /// </summary>
        /// <param name="managed"></param>
        /// <param name="native"></param>
        /// <param name="chain"></param>
        public RustEffectPair(IEffectProcessor managed, object native, TrackEffectChain chain)
        {
            Managed = managed;
            Native = native;
            _trackChain = chain;
            Sink = _pushParam;
            LastResetGeneration = managed.ResetGeneration;
        }

        /// <summary>
        /// The managed effect acting as param model.
        /// </summary>
        public IEffectProcessor Managed { get; }

        /// <summary>
        /// The native twin, from the master or a track chain.
        /// </summary>
        public object Native { get; }

        /// <summary>
        /// Last value pushed per native param id.
        /// </summary>
        public Dictionary<uint, float> LastParams { get; } = new Dictionary<uint, float>();

        /// <summary>
        /// Change-detecting param sink, bound once so the tick stays alloc-free.
        /// </summary>
        public RustEffectAdapters.ParamSink Sink { get; }

        /// <summary>
        /// Managed reset counter last carried over, so one Reset() call fires one native reset.
        /// </summary>
        public int LastResetGeneration { get; set; }

        /// <summary>
        /// Clears the native twin's tail. Only fired when the managed side asks for it.
        /// </summary>
        public void ResetNative()
        {
            if (_masterChain is not null)
                _masterChain.Reset(Native);
            else
                _trackChain?.Reset(Native);
        }

        /// <summary>
        /// Drops the native twin off its chain, shrugging off a transient failure
        /// (full command queue). Worst case it lives until the session dies.
        /// </summary>
        public void RemoveNativeBestEffort()
        {
            try
            {
                if (_masterChain is not null)
                    _masterChain.Remove(Native);
                else
                    _trackChain?.Remove(Native);
            }
            catch (Ownaudio.Safe.Exceptions.OwnAudioException ex)
            {
                Log.Warning($"[Mixer] Native effect twin stayed on its chain until the session dies: {ex.Message}");
            }
        }

        /// <summary>
        /// Pushes one param to the native twin, skipping unchanged values — this is the
        /// flood guard for the lock-free command queue.
        /// </summary>
        /// <param name="paramId"></param>
        /// <param name="value"></param>
        private void _pushParam(uint paramId, float value)
        {
            if (LastParams.TryGetValue(paramId, out float _last) && _last.Equals(value))
                return;

            LastParams[paramId] = value;

            if (_masterChain is not null)
                _masterChain.SetParam(Native, paramId, value);
            else
                _trackChain?.SetParam(Native, paramId, value);
        }
    }

    /// <summary>
    /// Per-track effect routing for SourceWithEffects sources, reconciled on the tick.
    /// Guarded by _rustSessionLock.
    /// </summary>
    private readonly List<RustTrackEffectRouting> _rustEffectSources = new List<RustTrackEffectRouting>();

    /// <summary>
    /// StartOffset last applied per source id, so the tick can spot an edit and realign.
    /// Guarded by _rustSessionLock.
    /// </summary>
    private readonly Dictionary<Guid, double> _rustAppliedStartOffsets = new Dictionary<Guid, double>();

    /// <summary>
    /// Native track we handed each source at attach time. A source disposed before it was
    /// removed hands back a null track, and without this the track would stay in the session
    /// and keep playing. Guarded by _rustSessionLock.
    /// </summary>
    private readonly Dictionary<Guid, AudioTrack> _rustAttachedTracks = new Dictionary<Guid, AudioTrack>();

    /// <summary>
    /// OutputChannelMapping last applied per source id, kept as an own clone so an in-place
    /// edit of the caller's array is still noticed. null value means routing is cleared.
    /// </summary>
    private readonly Dictionary<Guid, int[]?> _rustAppliedChannelMaps = new Dictionary<Guid, int[]?>();

    /// <summary>
    /// A SourceWithEffects plus its native-backed source and the pairs currently on the track chain.
    /// </summary>
    private sealed class RustTrackEffectRouting
    {
        /// <summary>
        /// Binds a wrapper to the backing source owning the native track.
        /// </summary>
        /// <param name="source"></param>
        /// <param name="backing"></param>
        public RustTrackEffectRouting(SourceWithEffects source, IRustNativeChainSource backing)
        {
            Source = source;
            Backing = backing;
        }

        /// <summary>
        /// The wrapper whose effect list drives the native chain.
        /// </summary>
        public SourceWithEffects Source { get; }

        /// <summary>
        /// The native-backed source owning the track.
        /// </summary>
        public IRustNativeChainSource Backing { get; }

        /// <summary>
        /// Effects version last reconciled; a mismatch is the alloc-free rebuild signal. -1 forces the first pass.
        /// </summary>
        public int CachedVersion { get; set; } = -1;

        /// <summary>
        /// Managed effect to native twin, in chain order.
        /// </summary>
        public List<RustEffectPair> Pairs { get; } = new List<RustEffectPair>();
    }

    /// <summary>
    /// The control-rate tick thread, alive while the rust mixer runs.
    /// </summary>
    private Thread? _rustSyncThread;

    /// <summary>
    /// Asks the tick to quit. volatile for cross-thread visibility.
    /// </summary>
    private volatile bool _rustSyncStop;

    /// <summary>
    /// Native output stream rendering the session, opened on the first Start. Stays null
    /// when degraded (mock engine in tests).
    /// </summary>
    private AudioOutputStream? _rustOutputStream;

    /// <summary>
    /// Last seen native error count, so a fresh backend fault is reported exactly once.
    /// </summary>
    private ulong _rustLastStreamErrorCount;

    /// <summary>
    /// Same for the shared capture stream, counted separately from the output's.
    /// </summary>
    private ulong _rustLastCaptureErrorCount;

    /// <summary>
    /// Latches PlaybackEnded so the tick raises it once per run, not every 15ms.
    /// </summary>
    private bool _rustPlaybackEndedRaised;

    /// <summary>
    /// Engine whose own push output we closed while the session drives the device.
    /// </summary>
    private RustAudioEngine? _rustReleasedEngine;

    /// <summary>
    /// Engine we lent our streams to for its width and buffer diagnostics. Set as soon as the
    /// session takes either direction of the device, which on an input-only mixer happens
    /// without any output ever being released. Cleared when the session dies.
    /// </summary>
    private RustAudioEngine? _rustDiagnosticEngine;

    /// <summary>
    /// Does this mixer run on the rust-native chain?
    /// </summary>
    internal bool IsRustNative => _rustNative;

    /// <summary>
    /// The shared session, null on legacy or before the first source.
    /// </summary>
    internal MultiTrackSession? RustSession
    {
        get { lock (_rustSessionLock) return _rustSession; }
    }

    /// <summary>
    /// What the session's callback is costing, null while it does not own the device. The one
    /// dropout signal on this path — nothing drains a ring here, so a late block shows up as
    /// PeakLoad crossing 1.0 rather than as an underrun. Cheap enough for a UI timer.
    /// </summary>
    public AudioStreamLoad? SessionLoad
    {
        get { lock (_rustSessionLock) return _rustOutputStream?.GetLoad(); }
    }

    /// <summary>
    /// Zeroes the load tallies, worth a call once playback has settled.
    /// </summary>
    public void ResetSessionLoad()
    {
        lock (_rustSessionLock) { _rustOutputStream?.ResetLoad(); }
    }

    /// <summary>
    /// Digs out whatever T sits behind a mixer source, unwrapping SourceWithEffects on the way.
    /// The wrapper is no BaseAudioSource itself, so a plain cast would read null off every
    /// effect-wrapped track.
    /// </summary>
    /// <param name="source"></param>
    /// <returns></returns>
    private static T? _resolve<T>(IAudioSource source) where T : class =>
        source as T ?? (source as SourceWithEffects)?.InnerSource as T;

    /// <summary>
    /// Builds the shared session on first use and hands the master scope down with it, so a
    /// scope set before any source was attached isn't lost. Call under _rustSessionLock.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_rustSession))]
    private void _ensureRustSession()
    {
        if (_rustSession is not null) return;
        _rustSession = new MultiTrackSession((float)_config.SampleRate, (ushort)_config.Channels);
        if (_masterChannelScope.Length > 0)
            _rustSession.MasterChannelScope = _masterChannelScope;
    }

    /// <summary>
    /// Resolves the native-backed source behind a mixer source to its id and current track.
    /// </summary>
    /// <param name="source"></param>
    /// <returns></returns>
    private static (Guid Id, AudioTrack? Track) _resolveRustBacked(IAudioSource source)
    {
        BaseAudioSource? _owner = _resolve<BaseAudioSource>(source);

        return _owner is IRustNativeChainSource _backed ? (_owner.Id, _backed.RustTrack) : (Guid.Empty, null);
    }
}
