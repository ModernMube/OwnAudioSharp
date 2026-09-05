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
/// Control plane of the mixer's rust-native chain: one shared MultiTrackSession owns a track
/// per attached source, and a control-rate tick mirrors volume/pan/loop/effects onto it.
/// No managed MixThread runs here — the native side renders everything.
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
    /// Hooks a source onto the shared session: file to a native file track, samples to a
    /// memory track, input to a capture track. Anything else is ignored.
    /// </summary>
    /// <param name="source"></param>
    private void _attachSourceToRustSession(IAudioSource source)
    {
        FileSource? _fs = _resolve<FileSource>(source);
        if (_fs?.FilePath is not null)
        {
            _attachFileSource(source, _fs);
            return;
        }

        SampleSource? _ss = _resolve<SampleSource>(source);
        if (_ss is not null)
        {
            _attachSampleSource(source, _ss);
            return;
        }

        InputSource? _ins = _resolve<InputSource>(source);
        if (_ins is not null)
        {
            _attachInputSource(source, _ins);
            return;
        }

        StreamingSource? _sts = _resolve<StreamingSource>(source);
        if (_sts is not null) _attachStreamingSource(source, _sts);
    }

    /// <summary>
    /// Attaches a streaming source to a bare native track: the track is created with a
    /// ring-buffer feed, which the source's own pump thread fills from its generator.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="sts"></param>
    private void _attachStreamingSource(IAudioSource source, StreamingSource sts)
    {
        lock (_rustSessionLock)
        {
            _ensureRustSession();

            AudioTrack _track = _rustSession.AddTrack();
            sts.AttachRustTrack(_track);

            _applyRoutingAtAttach(source, sts.Id, sts.RustTrack);
            _rememberRustTrack(sts.Id, sts.RustTrack);
            _routeTrackEffects(source, sts);
        }
    }

    /// <summary>
    /// Files the track under the source id and wires the wrapper's chain to the native one,
    /// so a chain edit reconciles on the caller thread instead of waiting for the tick.
    /// Call under _rustSessionLock.
    /// </summary>
    /// <param name="source">the outermost mixer source</param>
    /// <param name="backing">the source owning the native track</param>
    private void _routeTrackEffects(IAudioSource source, IRustNativeChainSource backing)
    {
        if (source is not SourceWithEffects swe) return;

        //The wrapper may already carry a chain from before it was handed to the mixer
        foreach (IEffectProcessor effect in swe.GetEffects())
            ThrowIfNoNativeTwin(effect, $"source '{swe.Id}'");

        _rustEffectSources.Add(new RustTrackEffectRouting(swe, backing));
        swe.NativeChainReconciler = ReconcileRustTrackEffectsOnce;
        swe.NativeChainValidator = _effect => ThrowIfNoNativeTwin(_effect, $"source '{swe.Id}'");
    }

    /// <summary>
    /// Everything the mixer plays runs as a native twin, so a managed type with no adapter would
    /// simply be inaudible. Better a hard error at the call site than a silent chain — and better
    /// than throwing out of the rebuild, which the control tick would then hit every 15 ms.
    /// </summary>
    /// <param name="effect"></param>
    /// <param name="where">for the message: which chain rejected it</param>
    internal static void ThrowIfNoNativeTwin(IEffectProcessor effect, string where)
    {
        if (effect is VST3EffectProcessor || RustEffectAdapters.TryGetEffectType(effect, out _))
            return;

        throw new InvalidOperationException(
            $"Effect '{effect.GetType().Name}' has no native twin and cannot run on {where}. "
            + "Only built-in OwnaudioNET.Effects types (and VST3) are hosted natively.");
    }

    /// <summary>
    /// Remembers the track we attached, keyed by source id. Call under _rustSessionLock.
    /// </summary>
    /// <param name="id"></param>
    /// <param name="track"></param>
    private void _rememberRustTrack(Guid id, AudioTrack? track)
    {
        if (track is not null) _rustAttachedTracks[id] = track;
    }

    /// <summary>
    /// Lays a source's channel map and output route onto the freshly attached track. Routing set
    /// before the add still has to land on the very first rendered block, hence here and not on
    /// the next tick.
    /// </summary>
    /// <remarks>
    /// A route the engine won't take — past its 16 channel reach, or naming a source channel the
    /// track hasn't got — is a mistake in the host's configuration, not a reason to abandon a
    /// track that plays perfectly well unrouted. It gets reported and the track goes on the bus
    /// plain, which is what the control tick does with the same failure; letting it out of
    /// AddSource instead would leave the source registered on a half wired track.
    /// </remarks>
    /// <param name="source">the outermost mixer source</param>
    /// <param name="id">bookkeeping key of the backed source</param>
    /// <param name="track"></param>
    private void _applyRoutingAtAttach(IAudioSource source, Guid id, AudioTrack? track)
    {
        try { _applyChannelMap(source, id, track); }
        catch (Exception ex) { Log.Error($"[Mixer] Output channel map of source '{id}' rejected at attach, it plays unmapped", ex); }

        try { _applyOutputRoute(source, id, track); }
        catch (Exception ex) { Log.Error($"[Mixer] Output route of source '{id}' rejected at attach, it plays unrouted", ex); }
    }

    /// <summary>
    /// Attaches a natively decoded file source to the shared session.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="fs"></param>
    private void _attachFileSource(IAudioSource source, FileSource fs)
    {
        lock (_rustSessionLock)
        {
            _ensureRustSession();

            //The source decides its own decode width; unset means the session's, as always
            FileTrack _track = _rustSession.AddFileTrack(
                fs.FilePath!,
                (ushort)(fs.DecodeChannels ?? _config.Channels));
            fs.AttachRustTrack(_track.Track, _track);

            _applyRoutingAtAttach(source, fs.Id, fs.RustTrack);
            _rememberRustTrack(fs.Id, fs.RustTrack);
            _routeTrackEffects(source, fs);
        }
    }

    /// <summary>
    /// Attaches a sample source, backed by a native memory track serving its buffer.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="ss"></param>
    private void _attachSampleSource(IAudioSource source, SampleSource ss)
    {
        lock (_rustSessionLock)
        {
            _ensureRustSession();

            MemoryTrack _track = _rustSession.AddMemoryTrack(ss.GetRustSampleSnapshot(), ss.Loop);
            ss.AttachRustTrack(_track.Track, _track);

            _applyRoutingAtAttach(source, ss.Id, ss.RustTrack);
            _rememberRustTrack(ss.Id, ss.RustTrack);
            _routeTrackEffects(source, ss);
        }
    }

    /// <summary>
    /// Attaches an input source, backed by a native capture writing straight into the track ring.
    /// Quietly does nothing under a non-rust engine (mock engine in tests) instead of blowing up.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="ins"></param>
    private void _attachInputSource(IAudioSource source, InputSource ins)
    {
        lock (_rustSessionLock)
        {
            RustAudioEngine? _rustEngine = _engine as RustAudioEngine;
            AudioEngine? _nativeEngine = _rustEngine?.NativeEngine;
            if (_nativeEngine is null)
                return;

            _ensureRustSession();

            //One device stream for every input track, not one each. On ASIO that's the whole
            //ballgame - a driver takes one client - and everywhere else it's just cheaper.
            if (_rustCapture is null && !_openRustCapture(_rustEngine!, _nativeEngine))
                return;

            AudioTrack _track = _rustSession.AddTrack();
            ins.AttachRustCapture(_track, _rustCapture!);

            try { _applyCaptureChannels(ins, _track); }
            catch (Exception ex)
            {
                Log.Error($"[Mixer] Capture channels of source '{ins.Id}' rejected at attach "
                    + $"(the device opened {_rustCapture!.ChannelCount}ch), it stays silent", ex);
            }

            _applyRoutingAtAttach(source, ins.Id, ins.RustTrack);
            _rememberRustTrack(ins.Id, ins.RustTrack);
            _routeTrackEffects(source, ins);
        }
    }

    /// <summary>
    /// Opens the one capture stream every input track taps, taking the device off the engine
    /// first. Reports the width it landed on, because that is the range an InputSource's
    /// CaptureChannels may address and there is no other way to find out what the card offers.
    /// Call under _rustSessionLock.
    /// </summary>
    /// <param name="rustEngine"></param>
    /// <param name="nativeEngine"></param>
    /// <returns>False when the device would not open, the caller then attaches nothing.</returns>
    private bool _openRustCapture(RustAudioEngine rustEngine, AudioEngine nativeEngine)
    {
        rustEngine.ReleaseInput();

        try
        {
            _rustCapture = _rustSession!.OpenCapture(
                nativeEngine, rustEngine.SelectedInputDevice, bufferFrames: (uint)_config.BufferSize);
        }
        catch (Exception ex)
        {
            Log.Error($"[Mixer] Shared capture cannot open on "
                + $"'{rustEngine.SelectedInputDevice?.Name ?? "(default)"}', input sources stay silent", ex);
            return false;
        }

        _rustLastCaptureErrorCount = 0;

        //Every input track taps this one stream, so its width is the whole capture surface
        rustEngine.TrackSessionCapture(_rustCapture.ChannelCount);
        _rustDiagnosticEngine = rustEngine;

        Log.Info($"[Mixer] Shared capture opened on '{rustEngine.SelectedInputDevice?.Name ?? "(default)"}': "
            + $"{_rustCapture.ChannelCount}ch, buffer {_config.BufferSize} frames requested");

        return true;
    }

    /// <summary>
    /// Unhooks a source and disposes its track. No-op without a native backend.
    /// </summary>
    /// <param name="source"></param>
    private void _detachSourceFromRustSession(IAudioSource source)
    {
        BaseAudioSource? _owner = _resolve<BaseAudioSource>(source);
        if (_owner is not IRustNativeChainSource _backed) return;

        _detachBackedSource(source, _owner.Id, _backed.RustTrack, _backed.DetachRustTrack);
    }

    /// <summary>
    /// Shared detach path: drops the routing and bookkeeping, unbinds the track, removes it.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="id">bookkeeping key of the backed source</param>
    /// <param name="track">the native track before detaching, null if the source was disposed already</param>
    /// <param name="detach">the backed source's own detach action</param>
    private void _detachBackedSource(IAudioSource source, Guid id, AudioTrack? track, Action detach)
    {
        lock (_rustSessionLock)
        {
            //A source disposed before RemoveSource has nulled its track, ours is still good
            if (track is null) _rustAttachedTracks.TryGetValue(id, out track);

            if (source is SourceWithEffects swe) swe.NativeChainReconciler = SourceWithEffects.NoNativeChain;

            _rustEffectSources.RemoveAll(r => ReferenceEquals(r.Source, source));
            _rustAppliedStartOffsets.Remove(id);
            _rustAppliedChannelMaps.Remove(id);
            _rustAppliedRoutes.Remove(id);
            _rustAttachedTracks.Remove(id);

            if (_rustAppliedCaptureMaps.Remove(id) && track is not null)
                _rustCapture?.Detach(track);

            detach();

            if (track is not null && _rustSession is not null)
                _rustSession.RemoveTrack(track);
        }
    }

    /// <summary>
    /// One pass of mirroring volume/pan/loop onto every attached track, and pulling the
    /// track's peaks back for metering (the managed OnSamplesRead path that used to feed
    /// them doesn't run here). Goes through the resolvers so an effect-wrapped track isn't
    /// skipped. Public-ish for deterministic tests.
    /// </summary>
    internal void SyncRustControlStateOnce()
    {
        IAudioSource[] _sources = Volatile.Read(ref _rustSourceSnapshot);
        foreach (IAudioSource source in _sources)
        {
            BaseAudioSource? _owner = _resolve<BaseAudioSource>(source);
            if (_owner is not IRustNativeChainSource _backed) continue;

            AudioTrack? _track = _backed.RustTrack;
            if (_track is null) continue;

            _track.Gain = _owner.Volume;
            _track.Pan = _owner.Pan;

            //Only the file and memory tracks loop natively, the other two have nowhere to put it
            switch (_owner)
            {
                case FileSource _fs when _fs.RustFileTrack is not null:
                    _fs.RustFileTrack.Loop = _fs.Loop;
                    break;
                case SampleSource _ss when _ss.RustMemoryTrack is not null:
                    _ss.RustMemoryTrack.Loop = _ss.Loop;
                    break;
            }

            _owner.SetOutputLevels(_owner.State == AudioState.Playing ? _track.Peaks : (0f, 0f));
        }
    }

    /// <summary>
    /// Pushes master volume/pan onto the native master bus and reads its peaks back into
    /// LeftPeak/RightPeak, which the missing MixThread used to compute.
    /// </summary>
    internal void SyncRustMasterOnce()
    {
        MultiTrackSession? _session;
        lock (_rustSessionLock) { _session = _rustSession; }

        if (_session is null)
            return;

        _session.MasterGain = _masterVolume;
        _session.MasterPan = _masterPan;

        (float _left, float _right) = _session.GetMasterPeaks();
        _leftPeak = _left;
        _rightPeak = _right;
    }

    /// <summary>
    /// Applies a source's StartOffset against a project position: content = project - offset.
    /// Non-negative content seeks the decoder there, negative holds the track silent for the
    /// remaining frames. Call under _rustSessionLock.
    /// </summary>
    /// <param name="fs"></param>
    /// <param name="projectPosition">project timeline position in seconds</param>
    private void _applyRustStartOffset(FileSource fs, double projectPosition)
    {
        AudioTrack? _track = fs.RustTrack;
        if (_track is null)
            return;

        double _offset = fs.StartOffset;
        double _local = projectPosition - _offset;

        if (_local >= 0.0)
        {
            //Seek target is wall-clock but the decoder speaks content time, so scale by tempo
            float _tempo = fs.Tempo <= 0f ? 1f : fs.Tempo;
            fs.Seek(Math.Clamp(_local * _tempo, 0.0, fs.Duration));
            _track.SetStartDelayFrames(0);
        }
        else
        {
            fs.Seek(0.0);
            _track.SetStartDelayFrames((long)Math.Round(-_local * _config.SampleRate));
        }

        _rustAppliedStartOffsets[fs.Id] = _offset;
    }

    /// <summary>
    /// Drops a freshly attached track onto the clock before it is allowed to play. Without it
    /// the track runs from the head of the file until the next tick catches it — 15 ms of the
    /// wrong audio and an audible jump when it lands.
    /// </summary>
    /// <param name="source"></param>
    internal void AlignRustSourceToClock(IAudioSource source)
    {
        if (!_rustNative) return;

        FileSource? _fs = _resolve<FileSource>(source);
        if (_fs?.RustTrack is null) return;

        lock (_rustSessionLock)
        {
            try { _applyRustStartOffset(_fs, _masterClock.CurrentTimestamp); }
            catch (Exception ex) { Log.Error($"[Mixer] Hot-swapped source '{_fs.Id}' could not be put on the clock", ex); }
        }
    }

    /// <summary>
    /// Realigns any track whose StartOffset changed since it was last applied, so a live
    /// offset edit lands without an explicit seek. Untouched offsets are left alone.
    /// </summary>
    internal void SyncRustStartOffsetsOnce()
    {
        double _project = _masterClock.CurrentTimestamp;

        IAudioSource[] _sources = Volatile.Read(ref _rustSourceSnapshot);
        lock (_rustSessionLock)
        {
            foreach (IAudioSource source in _sources)
            {
                FileSource? _fs = _resolve<FileSource>(source);
                if (_fs?.RustTrack is null)
                    continue;

                bool _known = _rustAppliedStartOffsets.TryGetValue(_fs.Id, out double _applied);
                if (_known && _applied == _fs.StartOffset)
                    continue;

                try { _applyRustStartOffset(_fs, _project); }
                catch (Exception ex) { _logRustApplyError("Start offset", _fs.Id, ex); }
            }
        }
    }

    /// <summary>
    /// Mirrors a source's OutputChannelMapping onto its track: source channel i sums into
    /// output mapping[i], unmapped outputs get silence. Only re-applied when it changed.
    /// Call under _rustSessionLock.
    /// </summary>
    /// <param name="source">the outermost mixer source carrying the map</param>
    /// <param name="key">bookkeeping key of the backed source</param>
    /// <param name="track"></param>
    private void _applyChannelMap(IAudioSource source, Guid key, AudioTrack? track)
    {
        if (track is null)
            return;

        int[]? _current = _resolve<BaseAudioSource>(source)?.OutputChannelMapping;

        if (_current is null && !_rustAppliedChannelMaps.ContainsKey(key))
            return;

        if (_rustAppliedChannelMaps.TryGetValue(key, out int[]? _applied) && _channelMapsEqual(_applied, _current))
            return;

        if (_current is null || _current.Length == 0)
            track.ClearOutputChannelMap();
        else
            track.SetOutputChannelMap(_current);

        //Own clone, otherwise an in-place edit of the same array slips through unnoticed
        _rustAppliedChannelMaps[key] = _current is null ? null : (int[])_current.Clone();
    }

    /// <summary>
    /// Value-compares two channel maps, either may be null.
    /// </summary>
    /// <param name="a"></param>
    /// <param name="b"></param>
    /// <returns></returns>
    private static bool _channelMapsEqual(int[]? a, int[]? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.Length != b.Length) return false;
        return a.AsSpan().SequenceEqual(b);
    }

    /// <summary>
    /// Re-applies every changed channel map so a live re-route takes effect on the next tick.
    /// </summary>
    internal void SyncRustChannelMapsOnce()
    {
        IAudioSource[] _sources = Volatile.Read(ref _rustSourceSnapshot);
        lock (_rustSessionLock)
        {
            foreach (IAudioSource source in _sources)
            {
                (Guid _id, AudioTrack? _track) = _resolveRustBacked(source);
                if (_track is null) continue;

                try { _applyChannelMap(source, _id, _track); }
                catch (Exception ex) { _logRustApplyError("Output channel map", _id, ex); }
            }
        }
    }

    /// <summary>
    /// OutputRoute last applied per source id. Own clone, same reason as the channel maps:
    /// an in-place edit of the caller's arrays has to be noticed. null means the route is off.
    /// </summary>
    private readonly Dictionary<Guid, OutputRoute?> _rustAppliedRoutes = new Dictionary<Guid, OutputRoute?>();

    /// <summary>
    /// Mirrors a source's OutputRoute onto its track: bus channel dst takes source channel
    /// map[dst] at gain[dst], unbound destinations get nothing from us. Only re-applied when it
    /// changed. Call under _rustSessionLock.
    /// </summary>
    /// <param name="source">the outermost mixer source carrying the route</param>
    /// <param name="key">bookkeeping key of the backed source</param>
    /// <param name="track"></param>
    private void _applyOutputRoute(IAudioSource source, Guid key, AudioTrack? track)
    {
        if (track is null)
            return;

        OutputRoute? _current = _resolve<BaseAudioSource>(source)?.OutputRoute;

        if (_current is null && !_rustAppliedRoutes.ContainsKey(key))
            return;

        if (_rustAppliedRoutes.TryGetValue(key, out OutputRoute? _applied) && OutputRoute.Equal(_applied, _current))
            return;

        if (_current is null || _current.SourceForChannel.Length == 0)
            track.ClearOutputRoute();
        else
            track.SetOutputRoute(_current.SourceForChannel, _current.Gains ?? ReadOnlySpan<float>.Empty);

        _rustAppliedRoutes[key] = _current is null ? null : new OutputRoute(_current.SourceForChannel, _current.Gains);
    }

    /// <summary>
    /// Re-applies every changed output route so a live re-cable lands on the next tick — a route
    /// write, not a stream reopen, which is what ASIO needs.
    /// </summary>
    internal void SyncRustOutputRoutesOnce()
    {
        IAudioSource[] _sources = Volatile.Read(ref _rustSourceSnapshot);
        lock (_rustSessionLock)
        {
            foreach (IAudioSource source in _sources)
            {
                (Guid _id, AudioTrack? _track) = _resolveRustBacked(source);
                if (_track is null) continue;

                try { _applyOutputRoute(source, _id, _track); }
                catch (Exception ex) { _logRustApplyError("Output route", _id, ex); }
            }
        }
    }

    /// <summary>
    /// Which bus channels the master chain, gain and pan run over. Empty (the default) is the
    /// whole bus. Narrow it to [0,1] and a click on 3/4 reaches the driver as mixed, so the
    /// limiter on the main pair doesn't squash the direct out.
    /// </summary>
    public int[] MasterChannelScope
    {
        get => _masterChannelScope;
        set
        {
            _masterChannelScope = value ?? Array.Empty<int>();
            lock (_rustSessionLock)
            {
                if (_rustSession is not null)
                    _rustSession.MasterChannelScope = _masterChannelScope;
            }
        }
    }

    private int[] _masterChannelScope = Array.Empty<int>();

    /// <summary>
    /// The one capture stream every live input track taps. Opened with the first input source,
    /// owned by the session. Guarded by _rustSessionLock.
    /// </summary>
    private CaptureBridge? _rustCapture;

    /// <summary>
    /// Capture map last applied per source id, so a live re-cable is a tap swap and nothing else.
    /// </summary>
    private readonly Dictionary<Guid, int[]> _rustAppliedCaptureMaps = new Dictionary<Guid, int[]>();

    /// <summary>
    /// Points an input source at its physical capture channels. Null CaptureChannels means the
    /// default: the first N of the device, repeating the last one so a mono mic still fills a
    /// stereo track exactly the way the per-track capture used to. Call under _rustSessionLock.
    /// </summary>
    /// <param name="ins"></param>
    /// <param name="track"></param>
    private void _applyCaptureChannels(InputSource ins, AudioTrack? track)
    {
        if (track is null || _rustCapture is null) return;

        int _deviceChannels = _rustCapture.ChannelCount;
        int[] _wanted = ins.CaptureChannels ?? _defaultCaptureMap(_deviceChannels);

        if (_rustAppliedCaptureMaps.TryGetValue(ins.Id, out int[]? _applied) && _channelMapsEqual(_applied, _wanted))
            return;

        //Past the equality check on purpose: a tap only lands at attach and on a real re-cable,
        //so this can't turn into per-tick noise
        if (ins.CaptureChannels is null && _deviceChannels < _config.Channels)
            Log.Warning($"[Mixer] Source '{ins.Id}' wants {_config.Channels}ch but the device captures "
                + $"{_deviceChannels}ch, channel {_deviceChannels - 1} is duplicated to fill the rest — "
                + "set InputSource.CaptureChannels to pick the inputs yourself");

        _rustCapture.Attach(track, _wanted);
        _rustAppliedCaptureMaps[ins.Id] = (int[])_wanted.Clone();
    }

    /// <summary>
    /// First N device channels for a session-wide track, clamped so a narrower device duplicates
    /// its last channel instead of falling off the end.
    /// </summary>
    /// <param name="captureChannels"></param>
    private int[] _defaultCaptureMap(int captureChannels)
    {
        int[] _map = new int[Math.Max(1, _config.Channels)];
        for (int i = 0; i < _map.Length; i++)
            _map[i] = Math.Min(i, Math.Max(0, captureChannels - 1));

        return _map;
    }

    /// <summary>
    /// Re-taps any input source whose CaptureChannels changed, so a live re-cable lands on the
    /// next tick without touching a stream.
    /// </summary>
    internal void SyncRustCaptureChannelsOnce()
    {
        IAudioSource[] _sources = Volatile.Read(ref _rustSourceSnapshot);
        lock (_rustSessionLock)
        {
            if (_rustCapture is null) return;

            foreach (IAudioSource source in _sources)
            {
                InputSource? _ins = _resolve<InputSource>(source);
                if (_ins?.RustTrack is null || _ins.RustCapture is null) continue;

                try { _applyCaptureChannels(_ins, _ins.RustTrack); }
                catch (Exception ex) { _logRustApplyError("Capture channels", _ins.Id, ex); }
            }
        }
    }

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

    /// <summary>
    /// One network drift-correction pass over the attached file sources. No-op for anything
    /// not playing under a network-controlled clock.
    /// </summary>
    internal void DriveRustNativeSyncOnce()
    {
        IAudioSource[] _sources = Volatile.Read(ref _rustSourceSnapshot);
        foreach (IAudioSource source in _sources)
        {
            _resolve<FileSource>(source)?.ApplyRustNativeSync();
        }
    }

    /// <summary>
    /// Starts the control tick unless it already runs.
    /// </summary>
    private void _startRustSyncTick()
    {
        lock (_rustSessionLock)
        {
            if (_rustSyncThread is not null)
                return;

            _rustSyncStop = false;
            _rustSyncThread = new Thread(_rustSyncLoop)
            {
                Name = "AudioMixer.RustControlSync",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
            };
            _rustSyncThread.Start();
        }
    }

    /// <summary>
    /// Signals and joins the control tick. Idempotent.
    /// </summary>
    private void _stopRustSyncTick()
    {
        Thread? _thread;
        lock (_rustSessionLock)
        {
            _thread = _rustSyncThread;
            _rustSyncStop = true;
            _rustSyncThread = null;
        }

        if (_thread is not null && _thread != Thread.CurrentThread) _thread.Join();
    }

    /// <summary>
    /// The control tick itself. A transient error mustn't kill it, but a repeating one gets
    /// reported and eventually stops the loop instead of spinning for hours.
    /// </summary>
    private void _rustSyncLoop()
    {
        while (!_rustSyncStop)
        {
            try
            {
                SyncRustControlStateOnce();
                SyncRustMasterOnce();
                SyncRustStartOffsetsOnce();
                SyncRustChannelMapsOnce();
                SyncRustOutputRoutesOnce();
                SyncRustCaptureChannelsOnce();
                MirrorRustMasterEffectsOnce();
                ReconcileRustTrackEffectsOnce();
                DriveRustNativeSyncOnce();
                _advanceMasterClockFromTracks();
                PollRustPlaybackEndedOnce();
                PollRustStreamFaultOnce();

                _rustSyncConsecutiveErrors = 0;
                _rustApplyErrors = 0;
            }
            catch (Exception ex)
            {
                if (_handleLoopError("Rust-native control sync tick", ex, ref _rustSyncConsecutiveErrors))
                    break;
            }

            Thread.Sleep(RustControlSyncIntervalMs);
        }
    }

    /// <summary>
    /// Raises PlaybackEnded once every attached source has run out. Sources sit on the native
    /// EOS latch, so this is just the tick noticing that none of them is playing any more.
    /// A source still Playing (or a fresh one showing up) re-arms the latch.
    /// </summary>
    internal void PollRustPlaybackEndedOnce()
    {
        IAudioSource[] _sources = Volatile.Read(ref _rustSourceSnapshot);
        if (_sources.Length == 0)
        {
            _rustPlaybackEndedRaised = false;
            return;
        }

        bool _allDone = true;
        foreach (IAudioSource source in _sources)
        {
            if (source.State != AudioState.EndOfStream) { _allDone = false; break; }
        }

        if (!_allDone)
        {
            _rustPlaybackEndedRaised = false;
            return;
        }

        if (_rustPlaybackEndedRaised) return;

        _rustPlaybackEndedRaised = true;
        RaisePlaybackEnded();
    }

    /// <summary>
    /// Drives the master clock from the furthest-along playing track in local playback —
    /// without the MixThread the clock would just sit frozen. Network-controlled clocks are
    /// left to the synchroniser, DriveRustNativeSyncOnce pulls the tracks to them instead.
    /// </summary>
    private void _advanceMasterClockFromTracks()
    {
        if (_masterClock.IsNetworkControlled)
            return;

        double _projectPos = -1.0;
        IAudioSource[] _sources = Volatile.Read(ref _rustSourceSnapshot);
        foreach (IAudioSource source in _sources)
        {
            FileSource? _fs = _resolve<FileSource>(source);
            if (_fs is not null && _fs.State == AudioState.Playing)
            {
                //A track still sitting in its start-offset silence would drag the clock to its offset
                if (_fs.StartOffset > 0.0 && (_fs.RustTrack?.RenderedFrames ?? 0UL) == 0UL)
                    continue;

                //Project position, not content Position: a stretched track must not run the shared
                //clock at its own content rate and desync everyone else
                double _p = _fs.StartOffset + _fs.RustNativeRealPosition;
                if (_p > _projectPos) _projectPos = _p;
            }
        }

        if (_projectPos >= 0.0) _masterClock.SeekTo(_projectPos);
    }

    /// <summary>
    /// Polls both directions for a fresh fault (device lost, backend error): the session
    /// output, and the shared capture every input source feeds off.
    /// </summary>
    internal void PollRustStreamFaultOnce()
    {
        AudioStreamErrorKind _outputKind = AudioStreamErrorKind.None;
        AudioStreamErrorKind _captureKind = AudioStreamErrorKind.None;
        ulong _outputCount = _rustLastStreamErrorCount;
        ulong _captureCount = _rustLastCaptureErrorCount;

        lock (_rustSessionLock)
        {
            if (_rustOutputStream is { } _stream)
                _outputKind = _stream.PollErrorState(out _outputCount);

            if (_rustCapture is { } _capture)
                _captureKind = _capture.PollErrorState(out _captureCount);
        }

        _raiseRustFault(AudioStreamDirection.Output, _outputKind, _outputCount, ref _rustLastStreamErrorCount);
        _raiseRustFault(AudioStreamDirection.Input, _captureKind, _captureCount, ref _rustLastCaptureErrorCount);
    }

    /// <summary>
    /// Fires StreamFaulted once per fresh fault on one direction. The count is monotonic,
    /// so comparing it catches a repeat of the same kind too. Off the session lock, so a
    /// handler may call back in.
    /// </summary>
    /// <param name="direction"></param>
    /// <param name="kind"></param>
    /// <param name="count">error total the stream reported now</param>
    /// <param name="lastSeen">that direction's last reported total</param>
    private void _raiseRustFault(AudioStreamDirection direction, AudioStreamErrorKind kind, ulong count,
        ref ulong lastSeen)
    {
        if (count == lastSeen)
            return;

        lastSeen = count;

        if (kind == AudioStreamErrorKind.None)
            return;

        AudioStreamFaultKind _fault = kind == AudioStreamErrorKind.DeviceNotAvailable
            ? AudioStreamFaultKind.DeviceNotAvailable
            : AudioStreamFaultKind.BackendSpecific;

        string _what = direction == AudioStreamDirection.Output
            ? "output stream"
            : "shared capture stream, every input source is silent";

        Log.FatalError($"[Mixer] Native {_what} faulted: {_fault} (fault #{count})");

        StreamFaulted?.Invoke(this, new AudioStreamFaultEventArgs(_fault, count, direction));
    }

    /// <summary>
    /// Seek across the shared session: moves the clock and repositions every native decoder,
    /// since nothing in the managed pipeline carries the seek down for us.
    /// </summary>
    /// <param name="projectSeconds"></param>
    internal void SeekRustNative(double projectSeconds)
    {
        if (projectSeconds < 0.0) projectSeconds = 0.0;

        _masterClock.SeekTo(projectSeconds);

        lock (_rustSessionLock)
        {
            foreach (IAudioSource source in _sources.Values)
            {
                FileSource? _fs = _resolve<FileSource>(source);
                if (_fs is null)
                    continue;

                try { _applyRustStartOffset(_fs, projectSeconds); }
                catch (Exception ex) { Log.Error($"[Mixer] Seek of native track '{_fs.Id}' to {projectSeconds:F3}s failed", ex); }
            }
        }
    }

    /// <summary>
    /// Opens the session's native output on the engine device once and suspends the engine's
    /// own push output so the two don't fight. No-op without a session or a rust engine.
    /// </summary>
    private void _openRustOutput()
    {
        if (_rustOutputStream is not null || _rustSession is null)
            return;

        RustAudioEngine? _rustEngine = _engine as RustAudioEngine;
        AudioEngine? _nativeEngine = _rustEngine?.NativeEngine;
        if (_rustEngine is null || _nativeEngine is null)
        {
            Log.Error("[Mixer] Rust-native output cannot open: the mixer is not sitting on a live RustAudioEngine");
            return;
        }

        //The device buffer is this path's whole latency knob: the mixer renders in the callback,
        //so there is no render ring stacked on it the way a push stream has one
        _rustOutputStream = _rustSession.OpenOutput(
            _nativeEngine, _rustEngine.SelectedOutputDevice, _config.BufferSize);
        _rustLastStreamErrorCount = 0;
        _rustEngine.ReleaseOutput();
        _rustReleasedEngine = _rustEngine;

        //Ours now drives the device, so the engine's width and buffer diagnostics read it
        _rustEngine.TrackSessionOutput(_rustOutputStream);
        _rustDiagnosticEngine = _rustEngine;

        Log.Info($"[Mixer] Session output opened on '{_rustEngine.SelectedOutputDevice?.Name ?? "(default)"}': "
            + $"{_describeSessionWidth(_rustEngine)}, buffer {_config.BufferSize} frames requested, "
            + "no render ring (the mixer renders in the device callback)");
    }

    /// <summary>
    /// The bus width against what the device opened with. They part company on a card that only
    /// offers one width — the mix stays at the bus width and the engine spreads it over the rest
    /// — and that is exactly when someone asks why a route landed on the wrong socket.
    /// </summary>
    /// <param name="engine"></param>
    private string _describeSessionWidth(RustAudioEngine engine)
    {
        int _opened = engine.ActualOutputChannels;
        return _opened <= 0 || _opened == _config.Channels
            ? $"{_config.Channels}ch bus"
            : $"{_config.Channels}ch bus -> {_opened}ch device";
    }

    /// <summary>
    /// Transport start: opens the device output and fires every track against the shared clock.
    /// </summary>
    private void _startRustOutput()
    {
        lock (_rustSessionLock)
        {
            _openRustOutput();

            if (_rustOutputStream is null)
                return;

            //Offsets must be in place before PlayAll, otherwise the first block is already wrong
            double _project = _masterClock.CurrentTimestamp;
            foreach (IAudioSource source in _sources.Values)
            {
                FileSource? _fs = _resolve<FileSource>(source);
                if (_fs?.RustTrack is null)
                    continue;

                try
                {
                    if (_fs.StartOffset != 0.0)
                    {
                        _applyRustStartOffset(_fs, _project);
                    }
                    else
                    {
                        _fs.RustTrack.SetStartDelayFrames(0);
                        _rustAppliedStartOffsets[_fs.Id] = 0.0;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"[Mixer] Track '{_fs.Id}' starts misaligned, its offset could not be applied", ex);
                }
            }

            _rustSession?.PlayAll();
        }
    }

    /// <summary>
    /// Opens the output when a source shows up on an already running mixer that had none yet —
    /// the session is built lazily, so a Start() before the first source finds nothing to open.
    /// AddSource plays the sources itself, so only the device stream is needed here.
    /// </summary>
    private void _ensureRustOutputAfterAttach()
    {
        if (!_rustNative || !_isRunning)
            return;

        lock (_rustSessionLock)
        {
            if (_rustOutputStream is not null)
                return;

            _openRustOutput();
        }
    }

    /// <summary>
    /// Transport stop: stops all tracks while the output is live.
    /// </summary>
    private void _stopRustOutput()
    {
        lock (_rustSessionLock)
        {
            if (_rustOutputStream is not null) _rustSession?.StopAll();
        }
    }

    /// <summary>
    /// Transport pause: pauses all tracks while the output is live.
    /// </summary>
    private void _pauseRustOutput()
    {
        lock (_rustSessionLock)
        {
            if (_rustOutputStream is not null) _rustSession?.PauseAll();
        }
    }

    /// <summary>
    /// Tears down the session (tracks, feeders, output stream) and the tick, then hands the
    /// device back to the engine.
    /// </summary>
    private void _disposeRustSession()
    {
        _stopRustSyncTick();

        lock (_rustSessionLock)
        {
            //Native effects live on the session mixer, disposing it frees them — just drop the pairings
            _rustMasterEffects.Clear();

            foreach (var routing in _rustEffectSources)
                routing.Source.NativeChainReconciler = SourceWithEffects.NoNativeChain;

            _rustEffectSources.Clear();
            _rustAttachedTracks.Clear();

            try { _rustSession?.Dispose(); }
            catch (Exception ex) { Log.Error("[Mixer] Native session dispose failed, native tracks may leak", ex); }

            _rustSession = null;
            _rustCapture = null;
            _rustLastCaptureErrorCount = 0;
            _rustAppliedCaptureMaps.Clear();
            _rustAppliedRoutes.Clear();
            _rustOutputStream = null;

            //What we lent the engine for its diagnostics died with the session
            _rustDiagnosticEngine?.TrackSessionOutput(null);
            _rustDiagnosticEngine?.TrackSessionCapture(0);
            _rustDiagnosticEngine = null;

            _rustReleasedEngine?.RestoreOutput();
            _rustReleasedEngine = null;

            Log.Info("[Mixer] Native session disposed, device handed back to the engine");
        }
    }
}
