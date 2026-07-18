using System;
using System.Collections.Generic;
using System.Threading;
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
            catch (Ownaudio.Safe.Exceptions.OwnAudioException)
            {
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
    /// Engine whose own push output we suspended while the session drives the device.
    /// </summary>
    private RustAudioEngine? _rustSuspendedEngine;

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
    /// Digs out the FileSource behind a mixer source, unwrapping SourceWithEffects.
    /// </summary>
    /// <param name="source"></param>
    /// <returns></returns>
    private static FileSource? _resolveFileSource(IAudioSource source) =>
        source as FileSource ?? (source as SourceWithEffects)?.InnerSource as FileSource;

    /// <summary>
    /// Digs out the SampleSource behind a mixer source, unwrapping SourceWithEffects.
    /// </summary>
    /// <param name="source"></param>
    /// <returns></returns>
    private static SampleSource? _resolveSampleSource(IAudioSource source) =>
        source as SampleSource ?? (source as SourceWithEffects)?.InnerSource as SampleSource;

    /// <summary>
    /// Digs out the InputSource behind a mixer source, unwrapping SourceWithEffects.
    /// </summary>
    /// <param name="source"></param>
    /// <returns></returns>
    private static InputSource? _resolveInputSource(IAudioSource source) =>
        source as InputSource ?? (source as SourceWithEffects)?.InnerSource as InputSource;

    /// <summary>
    /// Digs out the StreamingSource behind a mixer source, unwrapping SourceWithEffects.
    /// </summary>
    /// <param name="source"></param>
    /// <returns></returns>
    private static StreamingSource? _resolveStreamingSource(IAudioSource source) =>
        source as StreamingSource ?? (source as SourceWithEffects)?.InnerSource as StreamingSource;

    /// <summary>
    /// Hooks a source onto the shared session: file to a native file track, samples to a
    /// memory track, input to a capture track. Anything else is ignored.
    /// </summary>
    /// <param name="source"></param>
    private void _attachSourceToRustSession(IAudioSource source)
    {
        FileSource? _fs = _resolveFileSource(source);
        if (_fs?.FilePath is not null)
        {
            _attachFileSource(source, _fs);
            return;
        }

        SampleSource? _ss = _resolveSampleSource(source);
        if (_ss is not null)
        {
            _attachSampleSource(source, _ss);
            return;
        }

        InputSource? _ins = _resolveInputSource(source);
        if (_ins is not null)
        {
            _attachInputSource(source, _ins);
            return;
        }

        StreamingSource? _sts = _resolveStreamingSource(source);
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
            _rustSession ??= new MultiTrackSession((float)_config.SampleRate, (ushort)_config.Channels);

            AudioTrack _track = _rustSession.AddTrack();
            sts.AttachRustTrack(_track);

            _applyChannelMap(source, sts.Id, sts.RustTrack);

            if (source is SourceWithEffects swe)
                _rustEffectSources.Add(new RustTrackEffectRouting(swe, sts));
        }
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
            _rustSession ??= new MultiTrackSession((float)_config.SampleRate, (ushort)_config.Channels);

            FileTrack _track = _rustSession.AddFileTrack(fs.FilePath!);
            fs.AttachRustTrack(_track.Track, _track);

            //Routing set before the add still has to land on the very first rendered block
            _applyChannelMap(source, fs.Id, fs.RustTrack);

            if (source is SourceWithEffects swe)
                _rustEffectSources.Add(new RustTrackEffectRouting(swe, fs));
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
            _rustSession ??= new MultiTrackSession((float)_config.SampleRate, (ushort)_config.Channels);

            MemoryTrack _track = _rustSession.AddMemoryTrack(ss.GetRustSampleSnapshot(), ss.Loop);
            ss.AttachRustTrack(_track.Track, _track);

            _applyChannelMap(source, ss.Id, ss.RustTrack);

            if (source is SourceWithEffects swe)
                _rustEffectSources.Add(new RustTrackEffectRouting(swe, ss));
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

            _rustSession ??= new MultiTrackSession((float)_config.SampleRate, (ushort)_config.Channels);

            InputTrack _track = _rustSession.AddInputTrack(
                _nativeEngine, device: null, bufferFrames: (uint)_config.BufferSize);
            ins.AttachRustTrack(_track.Track, _track);

            _applyChannelMap(source, ins.Id, ins.RustTrack);

            if (source is SourceWithEffects swe)
                _rustEffectSources.Add(new RustTrackEffectRouting(swe, ins));
        }
    }

    /// <summary>
    /// Unhooks a source and disposes its track. No-op without a native backend.
    /// </summary>
    /// <param name="source"></param>
    private void _detachSourceFromRustSession(IAudioSource source)
    {
        FileSource? _fs = _resolveFileSource(source);
        if (_fs is not null)
        {
            _detachBackedSource(source, _fs.Id, _fs.RustTrack, _fs.DetachRustTrack);
            return;
        }

        SampleSource? _ss = _resolveSampleSource(source);
        if (_ss is not null)
        {
            _detachBackedSource(source, _ss.Id, _ss.RustTrack, _ss.DetachRustTrack);
            return;
        }

        InputSource? _ins = _resolveInputSource(source);
        if (_ins is not null)
        {
            _detachBackedSource(source, _ins.Id, _ins.RustTrack, _ins.DetachRustTrack);
            return;
        }

        StreamingSource? _sts = _resolveStreamingSource(source);
        if (_sts is not null) _detachBackedSource(source, _sts.Id, _sts.RustTrack, _sts.DetachRustTrack);
    }

    /// <summary>
    /// Shared detach path: drops the routing and bookkeeping, unbinds the track, removes it.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="id">bookkeeping key of the backed source</param>
    /// <param name="track">the native track before detaching</param>
    /// <param name="detach">the backed source's own detach action</param>
    private void _detachBackedSource(IAudioSource source, Guid id, AudioTrack? track, Action detach)
    {
        lock (_rustSessionLock)
        {
            _rustEffectSources.RemoveAll(r => ReferenceEquals(r.Source, source));
            _rustAppliedStartOffsets.Remove(id);
            _rustAppliedChannelMaps.Remove(id);

            detach();

            if (track is not null && _rustSession is not null)
                _rustSession.RemoveTrack(track);
        }
    }

    /// <summary>
    /// One pass of mirroring volume/pan/loop onto every attached track, and pulling the
    /// track's peaks back for metering (the managed OnSamplesRead path that used to feed
    /// them doesn't run here). Public-ish for deterministic tests.
    /// </summary>
    internal void SyncRustControlStateOnce()
    {
        IAudioSource[] _sources = Volatile.Read(ref _rustSourceSnapshot);
        foreach (IAudioSource source in _sources)
        {
            if (source is FileSource fs)
            {
                AudioTrack? _track = fs.RustTrack;
                if (_track is null) continue;

                _track.Gain = fs.Volume;
                _track.Pan = fs.Pan;

                FileTrack? _fileTrack = fs.RustFileTrack;
                if (_fileTrack is not null) _fileTrack.Loop = fs.Loop;

                fs.SetOutputLevels(fs.State == AudioState.Playing ? _track.Peaks : (0f, 0f));
            }
            else if (source is SampleSource ss)
            {
                AudioTrack? _track = ss.RustTrack;
                if (_track is null) continue;

                _track.Gain = ss.Volume;
                _track.Pan = ss.Pan;

                MemoryTrack? _memTrack = ss.RustMemoryTrack;
                if (_memTrack is not null) _memTrack.Loop = ss.Loop;

                ss.SetOutputLevels(ss.State == AudioState.Playing ? _track.Peaks : (0f, 0f));
            }
            else if (source is InputSource ins)
            {
                AudioTrack? _track = ins.RustTrack;
                if (_track is null) continue;

                _track.Gain = ins.Volume;
                _track.Pan = ins.Pan;

                ins.SetOutputLevels(ins.State == AudioState.Playing ? _track.Peaks : (0f, 0f));
            }
            else if (source is StreamingSource sts)
            {
                AudioTrack? _track = sts.RustTrack;
                if (_track is null) continue;

                _track.Gain = sts.Volume;
                _track.Pan = sts.Pan;

                sts.SetOutputLevels(sts.State == AudioState.Playing ? _track.Peaks : (0f, 0f));
            }
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
                if (source is not FileSource fs || fs.RustTrack is null)
                    continue;

                bool _known = _rustAppliedStartOffsets.TryGetValue(fs.Id, out double _applied);
                if (_known && _applied == fs.StartOffset)
                    continue;

                try { _applyRustStartOffset(fs, _project); }
                catch { }
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

        int[]? _current = (source as BaseAudioSource)?.OutputChannelMapping;

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
                catch { }
            }
        }
    }

    /// <summary>
    /// Resolves the native-backed source behind a mixer source to its id and current track.
    /// </summary>
    /// <param name="source"></param>
    /// <returns></returns>
    private static (Guid Id, AudioTrack? Track) _resolveRustBacked(IAudioSource source)
    {
        FileSource? _fs = _resolveFileSource(source);
        if (_fs is not null) return (_fs.Id, _fs.RustTrack);

        SampleSource? _ss = _resolveSampleSource(source);
        if (_ss is not null) return (_ss.Id, _ss.RustTrack);

        InputSource? _ins = _resolveInputSource(source);
        if (_ins is not null) return (_ins.Id, _ins.RustTrack);

        return (Guid.Empty, null);
    }

    /// <summary>
    /// Routes a managed master effect onto the native master bus: a native twin is built and
    /// the managed params get mirrored onto it, the managed DSP itself never runs.
    /// No-op on legacy or when the effect type has no native adapter yet.
    /// </summary>
    /// <param name="effect"></param>
    internal void AttachMasterEffectToRust(IEffectProcessor effect)
    {
        if (!_rustNative || effect is null)
            return;

        //VST3 is hosted natively: managed side owns the plugin, the rust bus calls its process entry
        if (effect is VST3EffectProcessor vst)
        {
            if (!vst.CanHostNatively)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[OwnAudio] Master VST3 effect '{effect.Name}' is not audio-initialized " +
                    "(call and await VST3PluginHost.InitializeAudioAsync before adding it); it is " +
                    "inactive in the Rust-native chain.");
                return;
            }

            lock (_rustSessionLock)
            {
                _rustSession ??= new MultiTrackSession((float)_config.SampleRate, (ushort)_config.Channels);

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

        if (!RustEffectAdapters.TryGetEffectType(effect, out var effectType))
        {
            System.Diagnostics.Debug.WriteLine(
                $"[OwnAudio] Master effect '{effect.GetType().Name}' has no native adapter and is " +
                "inactive in the Rust-native chain.");
            return;
        }

        lock (_rustSessionLock)
        {
            _rustSession ??= new MultiTrackSession((float)_config.SampleRate, (ushort)_config.Channels);

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
        //A hosted VST is toggled through native bypass: JUCE keeps the output time-aligned,
        //a rust dry/wet switch would jump by the plugin latency on every flip. Its own params
        //go straight to the plugin, so only the on/off state travels here.
        if (pair.Managed is VST3EffectProcessor vst)
        {
            const uint _enabledId = 0;
            float _enabled = vst.Enabled ? 1f : 0f;
            if (pair.LastParams.TryGetValue(_enabledId, out float _last) && _last.Equals(_enabled))
                return;

            pair.LastParams[_enabledId] = _enabled;
            vst.SetNativeBypass(!vst.Enabled);
            return;
        }

        RustEffectAdapters.Mirror(pair.Managed, pair.Sink);
    }

    /// <summary>
    /// Reconciles every wrapper's effect list onto its native track chain and mirrors the
    /// params. Per-track effects are added on the wrapper, not the mixer, so there is no hook —
    /// we poll the version and rebuild the chain in order whenever it moved.
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
                    IEffectProcessor[] _managed = routing.Source.GetEffects();

                    foreach (var pair in routing.Pairs)
                        pair.RemoveNativeBestEffort();

                    routing.Pairs.Clear();

                    TrackEffectChain _chain = _track.Effects;
                    foreach (IEffectProcessor effect in _managed)
                    {
                        if (effect is VST3EffectProcessor vst && vst.CanHostNatively)
                        {
                            object _native = _chain.AddVst(
                                vst.NativePluginHandle,
                                vst.NativeProcessAudioPointer,
                                (ushort)_config.Channels,
                                (uint)_config.BufferSize,
                                (uint)Math.Max(0, vst.LatencySamples));
                            routing.Pairs.Add(new RustEffectPair(effect, _native, _chain));
                        }
                        else if (RustEffectAdapters.TryGetEffectType(effect, out var effectType))
                        {
                            object _native = _chain.Add(effectType, (float)_config.SampleRate);
                            routing.Pairs.Add(new RustEffectPair(effect, _native, _chain));
                        }
                    }

                    routing.CachedVersion = _version;
                }

                foreach (var pair in routing.Pairs)
                    _mirrorPair(pair);
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
            if (source is FileSource fs) fs.ApplyRustNativeSync();
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
                MirrorRustMasterEffectsOnce();
                ReconcileRustTrackEffectsOnce();
                DriveRustNativeSyncOnce();
                _advanceMasterClockFromTracks();
                PollRustStreamFaultOnce();

                _rustSyncConsecutiveErrors = 0;
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
            if (source is FileSource fs && fs.State == AudioState.Playing)
            {
                //A track still sitting in its start-offset silence would drag the clock to its offset
                if (fs.StartOffset > 0.0 && (fs.RustTrack?.RenderedFrames ?? 0UL) == 0UL)
                    continue;

                //Project position, not content Position: a stretched track must not run the shared
                //clock at its own content rate and desync everyone else
                double _p = fs.StartOffset + fs.RustNativeRealPosition;
                if (_p > _projectPos) _projectPos = _p;
            }
        }

        if (_projectPos >= 0.0) _masterClock.SeekTo(_projectPos);
    }

    /// <summary>
    /// Polls the native stream error state and raises StreamFaulted once per fresh fault
    /// (device lost, backend error). The count is monotonic, so comparing it catches a repeat
    /// of the same kind too. Event goes out off the lock so a handler may call back in.
    /// </summary>
    internal void PollRustStreamFaultOnce()
    {
        AudioStreamErrorKind _kind;
        ulong _count;

        lock (_rustSessionLock)
        {
            AudioOutputStream? _stream = _rustOutputStream;
            if (_stream is null)
                return;

            _kind = _stream.PollErrorState(out _count);
        }

        if (_count == _rustLastStreamErrorCount)
            return;

        _rustLastStreamErrorCount = _count;

        if (_kind == AudioStreamErrorKind.None)
            return;

        AudioStreamFaultKind _fault = _kind == AudioStreamErrorKind.DeviceNotAvailable
            ? AudioStreamFaultKind.DeviceNotAvailable
            : AudioStreamFaultKind.BackendSpecific;

        StreamFaulted?.Invoke(this, new AudioStreamFaultEventArgs(_fault, _count));
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
                if (source is not FileSource fs)
                    continue;

                try { _applyRustStartOffset(fs, projectSeconds); }
                catch { }
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
            return;

        _rustOutputStream = _rustSession.OpenOutput(_nativeEngine);
        _rustLastStreamErrorCount = 0;
        _rustEngine.SuspendOutput();
        _rustSuspendedEngine = _rustEngine;
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
                if (source is not FileSource fs || fs.RustTrack is null)
                    continue;

                try
                {
                    if (fs.StartOffset != 0.0)
                    {
                        _applyRustStartOffset(fs, _project);
                    }
                    else
                    {
                        fs.RustTrack.SetStartDelayFrames(0);
                        _rustAppliedStartOffsets[fs.Id] = 0.0;
                    }
                }
                catch { }
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
            _rustEffectSources.Clear();

            _rustSession?.Dispose();
            _rustSession = null;
            _rustOutputStream = null;

            _rustSuspendedEngine?.ResumeOutput();
            _rustSuspendedEngine = null;
        }
    }
}
