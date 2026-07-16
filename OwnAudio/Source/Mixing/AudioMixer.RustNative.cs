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
/// Rust-native chain facade for <see cref="AudioMixer"/> (plan 14 / D.2.d, control plane).
/// </summary>
/// <remarks>
/// <para>
/// When the process opts into the Rust-native chain (<see cref="Engine.RustNativeChain"/>), the
/// mixer owns a shared <see cref="MultiTrackSession"/>. Each <see cref="FileSource"/> added to the
/// mixer is backed by a track in that session (the source is <em>attached</em>, per the agreed
/// ownership model), the managed <c>MixThread</c> is not used, and the source's non-overridable
/// <c>Volume</c>/<c>Loop</c> are mirrored onto the track/file source by a control-rate sync tick.
/// </para>
/// <para>
/// This partial covers the control plane (session ownership, attach/detach, transport-flag
/// handling and control-state sync). Driving the session's native audio output
/// (<c>OpenOutput</c>) is wired in a following sub-step; until then the Rust-native mixer manages
/// state without rendering to a device.
/// </para>
/// </remarks>
public sealed partial class AudioMixer
{
    /// <summary>
    /// Interval, in milliseconds, at which the control-rate sync tick mirrors each source's
    /// <c>Volume</c>/<c>Loop</c> onto its track. Control-rate (not per audio buffer), so the P/Invoke
    /// cost is negligible while live slider changes still propagate promptly.
    /// </summary>
    private const int RustControlSyncIntervalMs = 15;

    /// <summary>
    /// Whether this mixer runs on the Rust-native chain. Assigned once in the constructor from
    /// <see cref="Engine.RustNativeChain.Enabled"/> so the mode is stable for the mixer's lifetime.
    /// </summary>
    private readonly bool _rustNative;

    /// <summary>
    /// Serializes creation, mutation and teardown of the shared session and the sync tick.
    /// </summary>
    private readonly object _rustSessionLock = new();

    /// <summary>
    /// The shared session that owns every attached source's track, created lazily when the first
    /// file source is added. <see langword="null"/> in legacy mode or before the first source.
    /// </summary>
    private MultiTrackSession? _rustSession;

    /// <summary>
    /// Managed master effect → paired native master effect, for the Rust-native chain. The managed
    /// effect is the parameter model; the native effect does the audio on the master bus. Guarded by
    /// <see cref="_rustSessionLock"/>.
    /// </summary>
    private readonly List<RustEffectPair> _rustMasterEffects = new();

    /// <summary>
    /// Pairs a managed effect (the parameter model) with its native effect, and remembers the last
    /// value pushed for each native parameter so the control-rate mirror only enqueues a command when
    /// a value actually changes — otherwise the mixer command queue would flood every tick. The pair
    /// also owns a <see cref="Sink"/> delegate bound once at construction, so the per-tick mirror
    /// pushes parameters without allocating a closure (the tick must stay allocation-free).
    /// </summary>
    private sealed class RustEffectPair
    {
        /// <summary>
        /// The master chain that owns <see cref="Native"/>, or <see langword="null"/> when the
        /// effect lives on a track chain.
        /// </summary>
        private readonly MasterEffectChain? _masterChain;

        /// <summary>
        /// The track chain that owns <see cref="Native"/>, or <see langword="null"/> when the
        /// effect lives on the master chain.
        /// </summary>
        private readonly TrackEffectChain? _trackChain;

        /// <summary>
        /// Creates a pair whose native effect lives on the session's master chain.
        /// </summary>
        /// <param name="managed">The managed effect acting as the parameter model.</param>
        /// <param name="native">The paired native effect wrapper.</param>
        /// <param name="chain">The master chain that owns <paramref name="native"/>.</param>
        public RustEffectPair(IEffectProcessor managed, object native, MasterEffectChain chain)
        {
            Managed = managed;
            Native = native;
            _masterChain = chain;
            Sink = PushParam;
        }

        /// <summary>
        /// Creates a pair whose native effect lives on a track's effect chain.
        /// </summary>
        /// <param name="managed">The managed effect acting as the parameter model.</param>
        /// <param name="native">The paired native effect wrapper.</param>
        /// <param name="chain">The track chain that owns <paramref name="native"/>.</param>
        public RustEffectPair(IEffectProcessor managed, object native, TrackEffectChain chain)
        {
            Managed = managed;
            Native = native;
            _trackChain = chain;
            Sink = PushParam;
        }

        /// <summary>The managed effect acting as the parameter model.</summary>
        public IEffectProcessor Managed { get; }

        /// <summary>The paired native effect wrapper (from the master or track chain).</summary>
        public object Native { get; }

        /// <summary>Last value pushed per native parameter id, for change detection.</summary>
        public Dictionary<uint, float> LastParams { get; } = new();

        /// <summary>
        /// Change-detecting parameter sink bound to <see cref="PushParam"/> once at construction.
        /// Reusing this single delegate keeps the control-rate mirror allocation-free; building the
        /// sink per tick would allocate a closure per effect per tick for the mixer's lifetime.
        /// </summary>
        public RustEffectAdapters.ParamSink Sink { get; }

        /// <summary>
        /// Removes the paired native effect from its owning chain, swallowing a transient failure
        /// (for example a momentarily full command queue) so tearing down effects never crashes the
        /// app. A skipped removal only leaves the native effect in place until the session is
        /// disposed.
        /// </summary>
        public void RemoveNativeBestEffort()
        {
            try
            {
                if (_masterChain is not null)
                {
                    _masterChain.Remove(Native);
                }
                else
                {
                    _trackChain?.Remove(Native);
                }
            }
            catch (Ownaudio.Safe.Exceptions.OwnAudioException)
            {
            }
        }

        /// <summary>
        /// Pushes one parameter onto the paired native effect through its owning chain, skipping
        /// values unchanged since the last push — the flood guard for the lock-free mixer command
        /// queue, which the mirror would otherwise fill every tick.
        /// </summary>
        /// <param name="paramId">The native parameter id.</param>
        /// <param name="value">The parameter value to push.</param>
        private void PushParam(uint paramId, float value)
        {
            if (LastParams.TryGetValue(paramId, out float last) && last.Equals(value))
            {
                return;
            }

            LastParams[paramId] = value;
            if (_masterChain is not null)
            {
                _masterChain.SetParam(Native, paramId, value);
            }
            else
            {
                _trackChain?.SetParam(Native, paramId, value);
            }
        }
    }

    /// <summary>
    /// Per-track effect routing state for sources wrapped in a <see cref="SourceWithEffects"/>: the
    /// wrapper's effect list is reconciled onto the native track's effect chain on the control-rate
    /// tick. Guarded by <see cref="_rustSessionLock"/>.
    /// </summary>
    private readonly List<RustTrackEffectRouting> _rustEffectSources = new();

    /// <summary>
    /// Last per-source <see cref="FileSource.StartOffset"/> applied to the native track (keyed by
    /// source id), so the control-rate tick can detect a changed offset and realign the track. An
    /// entry exists once the offset has been applied at least once. Guarded by
    /// <see cref="_rustSessionLock"/>.
    /// </summary>
    private readonly Dictionary<Guid, double> _rustAppliedStartOffsets = new();

    /// <summary>
    /// Last per-source <c>OutputChannelMapping</c> applied to the native track (keyed by the
    /// track-owning file source's id), stored as an independent clone so a later in-place edit of
    /// the same array is still detected. <see langword="null"/> value means routing is cleared.
    /// The control-rate tick re-applies the map only when it differs from this. Guarded by
    /// <see cref="_rustSessionLock"/>.
    /// </summary>
    private readonly Dictionary<Guid, int[]?> _rustAppliedChannelMaps = new();

    /// <summary>
    /// Tracks a <see cref="SourceWithEffects"/> and its underlying native-backed source together
    /// with the managed→native effect pairings currently installed on the native track.
    /// </summary>
    private sealed class RustTrackEffectRouting
    {
        public RustTrackEffectRouting(SourceWithEffects source, IRustNativeChainSource backing)
        {
            Source = source;
            Backing = backing;
        }

        /// <summary>The wrapper whose effect list drives the native track chain.</summary>
        public SourceWithEffects Source { get; }

        /// <summary>The underlying native-backed source that owns the native track.</summary>
        public IRustNativeChainSource Backing { get; }

        /// <summary>
        /// Last <see cref="SourceWithEffects.EffectsVersion"/> reconciled onto the native chain.
        /// A mismatch against the current version is the (allocation-free) signal that the chain
        /// changed and must be rebuilt. <c>-1</c> forces the first reconcile.
        /// </summary>
        public int CachedVersion { get; set; } = -1;

        /// <summary>Managed effect → paired native track effect, in chain order.</summary>
        public List<RustEffectPair> Pairs { get; } = new();
    }

    /// <summary>
    /// The control-rate sync tick thread; runs while the Rust-native mixer is started.
    /// </summary>
    private Thread? _rustSyncThread;

    /// <summary>
    /// Signals the sync tick thread to exit. Declared <see langword="volatile"/> for cross-thread
    /// visibility without a lock.
    /// </summary>
    private volatile bool _rustSyncStop;

    /// <summary>
    /// The native output stream that renders the shared session on the device, opened once on the
    /// first <see cref="Start"/>. <see langword="null"/> until opened (or when degraded because the
    /// engine is not the native Rust engine, e.g. under a mock engine in tests).
    /// </summary>
    private AudioOutputStream? _rustOutputStream;

    /// <summary>
    /// Last observed native stream error count, so the control-rate tick can tell a fresh backend
    /// fault (device lost etc.) from a previously-reported one and raise <see cref="StreamFaulted"/>
    /// exactly once per new fault. Reset to zero when a fresh output stream is opened.
    /// </summary>
    private ulong _rustLastStreamErrorCount;

    /// <summary>
    /// The native engine whose own push-based output was suspended while the session drives the
    /// device, so it can be resumed on dispose. <see langword="null"/> when nothing was suspended.
    /// </summary>
    private RustAudioEngine? _rustSuspendedEngine;

    /// <summary>
    /// Gets whether this mixer runs on the Rust-native chain.
    /// </summary>
    internal bool IsRustNative => _rustNative;

    /// <summary>
    /// Gets the shared session backing this mixer's sources, or <see langword="null"/> when running
    /// legacy or before the first file source is added.
    /// </summary>
    internal MultiTrackSession? RustSession
    {
        get
        {
            lock (_rustSessionLock)
            {
                return _rustSession;
            }
        }
    }

    /// <summary>
    /// Resolves the underlying <see cref="FileSource"/> behind a mixer source: the source itself, or
    /// the inner source when it is wrapped in a <see cref="SourceWithEffects"/> (the per-track effect
    /// path). Returns <see langword="null"/> for non-file sources.
    /// </summary>
    private static FileSource? ResolveFileSource(IAudioSource source) =>
        source as FileSource ?? (source as SourceWithEffects)?.InnerSource as FileSource;

    /// <summary>
    /// Resolves the underlying <see cref="SampleSource"/> behind a mixer source: the source itself, or
    /// the inner source when it is wrapped in a <see cref="SourceWithEffects"/>. Returns
    /// <see langword="null"/> for non-sample sources.
    /// </summary>
    private static SampleSource? ResolveSampleSource(IAudioSource source) =>
        source as SampleSource ?? (source as SourceWithEffects)?.InnerSource as SampleSource;

    /// <summary>
    /// Resolves the underlying <see cref="InputSource"/> behind a mixer source: the source itself, or
    /// the inner source when it is wrapped in a <see cref="SourceWithEffects"/>. Returns
    /// <see langword="null"/> for non-input sources.
    /// </summary>
    private static InputSource? ResolveInputSource(IAudioSource source) =>
        source as InputSource ?? (source as SourceWithEffects)?.InnerSource as InputSource;

    /// <summary>
    /// Attaches a source to the shared session in Rust-native mode. A <see cref="FileSource"/> is
    /// backed by a natively-decoded file track; a <see cref="SampleSource"/> is backed by a native
    /// memory track serving its buffer. In both cases a <see cref="SourceWithEffects"/> is unwrapped,
    /// the source is registered for native per-track effect routing, and the audio path stays entirely
    /// native. Other sources are ignored.
    /// </summary>
    /// <param name="source">The source being added to the mixer.</param>
    private void AttachSourceToRustSession(IAudioSource source)
    {
        FileSource? fs = ResolveFileSource(source);
        if (fs?.FilePath is not null)
        {
            AttachFileSourceToRustSession(source, fs);
            return;
        }

        SampleSource? ss = ResolveSampleSource(source);
        if (ss is not null)
        {
            AttachSampleSourceToRustSession(source, ss);
            return;
        }

        InputSource? ins = ResolveInputSource(source);
        if (ins is not null)
        {
            AttachInputSourceToRustSession(source, ins);
        }
    }

    /// <summary>Attaches a natively-decoded <see cref="FileSource"/> to the shared session.</summary>
    /// <param name="source">The mixer source (the file source or its effect wrapper).</param>
    /// <param name="fs">The resolved underlying file source.</param>
    private void AttachFileSourceToRustSession(IAudioSource source, FileSource fs)
    {
        lock (_rustSessionLock)
        {
            _rustSession ??= new MultiTrackSession(
                (float)_config.SampleRate,
                (ushort)_config.Channels);

            FileTrack fileTrack = _rustSession.AddFileTrack(fs.FilePath!);
            fs.AttachRustTrack(fileTrack.Track, fileTrack);

            // Apply any output-channel routing configured before the source was added, so the very
            // first rendered block already lands on the requested output channels.
            ApplyChannelMapLocked(source, fs.Id, fs.RustTrack);

            // A source wrapped in SourceWithEffects carries per-track effects; route them onto the
            // native track's effect chain (reconciled on the control-rate tick).
            if (source is SourceWithEffects swe)
            {
                _rustEffectSources.Add(new RustTrackEffectRouting(swe, fs));
            }
        }
    }

    /// <summary>
    /// Attaches a <see cref="SampleSource"/> to the shared session, backing it with a native memory
    /// track that serves the source's buffer directly on the audio thread (no managed audio path).
    /// </summary>
    /// <param name="source">The mixer source (the sample source or its effect wrapper).</param>
    /// <param name="ss">The resolved underlying sample source.</param>
    private void AttachSampleSourceToRustSession(IAudioSource source, SampleSource ss)
    {
        lock (_rustSessionLock)
        {
            _rustSession ??= new MultiTrackSession(
                (float)_config.SampleRate,
                (ushort)_config.Channels);

            MemoryTrack memoryTrack = _rustSession.AddMemoryTrack(ss.GetRustSampleSnapshot(), ss.Loop);
            ss.AttachRustTrack(memoryTrack.Track, memoryTrack);

            ApplyChannelMapLocked(source, ss.Id, ss.RustTrack);

            if (source is SourceWithEffects swe)
            {
                _rustEffectSources.Add(new RustTrackEffectRouting(swe, ss));
            }
        }
    }

    /// <summary>
    /// Attaches an <see cref="InputSource"/> to the shared session, backing it with a native input
    /// capture that writes device audio straight into the track's ring buffer (no managed audio
    /// path). Degrades to a no-op when the mixer's engine is not the native Rust engine (for example
    /// a mock engine in tests), so the source stays silent rather than crashing.
    /// </summary>
    /// <param name="source">The mixer source (the input source or its effect wrapper).</param>
    /// <param name="ins">The resolved underlying input source.</param>
    private void AttachInputSourceToRustSession(IAudioSource source, InputSource ins)
    {
        lock (_rustSessionLock)
        {
            RustAudioEngine? rustEngine = _engine as RustAudioEngine;
            AudioEngine? nativeEngine = rustEngine?.NativeEngine;
            if (nativeEngine is null)
            {
                return;
            }

            _rustSession ??= new MultiTrackSession(
                (float)_config.SampleRate,
                (ushort)_config.Channels);

            InputTrack inputTrack = _rustSession.AddInputTrack(
                nativeEngine, device: null, bufferFrames: (uint)_config.BufferSize);
            ins.AttachRustTrack(inputTrack.Track, inputTrack);

            ApplyChannelMapLocked(source, ins.Id, ins.RustTrack);

            if (source is SourceWithEffects swe)
            {
                _rustEffectSources.Add(new RustTrackEffectRouting(swe, ins));
            }
        }
    }

    /// <summary>
    /// Detaches a source from the shared session and removes (and disposes) its track. No-op for
    /// sources with no native backend.
    /// </summary>
    /// <param name="source">The source being removed from the mixer.</param>
    private void DetachSourceFromRustSession(IAudioSource source)
    {
        FileSource? fs = ResolveFileSource(source);
        if (fs is not null)
        {
            DetachBackedSourceFromRustSession(source, fs, fs.Id, fs.RustTrack, fs.DetachRustTrack);
            return;
        }

        SampleSource? ss = ResolveSampleSource(source);
        if (ss is not null)
        {
            DetachBackedSourceFromRustSession(source, ss, ss.Id, ss.RustTrack, ss.DetachRustTrack);
            return;
        }

        InputSource? ins = ResolveInputSource(source);
        if (ins is not null)
        {
            DetachBackedSourceFromRustSession(source, ins, ins.Id, ins.RustTrack, ins.DetachRustTrack);
        }
    }

    /// <summary>
    /// Shared detach path: drops the source's effect routing and applied-state bookkeeping, unbinds
    /// its native track, and removes (disposes) that track from the shared session.
    /// </summary>
    /// <param name="source">The mixer source being removed.</param>
    /// <param name="backing">The resolved native-backed source.</param>
    /// <param name="id">The backed source's id (bookkeeping key).</param>
    /// <param name="track">The native track before detaching, or <see langword="null"/>.</param>
    /// <param name="detach">The backed source's detach action.</param>
    private void DetachBackedSourceFromRustSession(
        IAudioSource source, IRustNativeChainSource backing, Guid id, AudioTrack? track, Action detach)
    {
        lock (_rustSessionLock)
        {
            _rustEffectSources.RemoveAll(r => ReferenceEquals(r.Source, source));
            _rustAppliedStartOffsets.Remove(id);
            _rustAppliedChannelMaps.Remove(id);

            detach();

            if (track is not null && _rustSession is not null)
            {
                _rustSession.RemoveTrack(track);
            }
        }
    }

    /// <summary>
    /// Mirrors every attached source's <c>Volume</c>, <c>Pan</c> and <c>Loop</c> onto its track and
    /// backing source once. Called on the sync tick and available for deterministic tests.
    /// </summary>
    internal void SyncRustControlStateOnce()
    {
        IAudioSource[] sources = Volatile.Read(ref _rustSourceSnapshot);
        foreach (IAudioSource source in sources)
        {
            if (source is FileSource fs)
            {
                AudioTrack? track = fs.RustTrack;
                if (track is null)
                {
                    continue;
                }

                track.Gain = fs.Volume;
                track.Pan = fs.Pan;

                FileTrack? fileTrack = fs.RustFileTrack;
                if (fileTrack is not null)
                {
                    fileTrack.Loop = fs.Loop;
                }

                // The managed OnSamplesRead path (which fed OutputLevels in legacy mode)
                // does not run here — the native track renders the audio — so mirror the
                // native track's own metering peaks onto the source. A track that is not
                // playing reports silence so the meter decays.
                fs.SetOutputLevels(fs.State == AudioState.Playing ? track.Peaks : (0f, 0f));
            }
            else if (source is SampleSource ss)
            {
                AudioTrack? track = ss.RustTrack;
                if (track is null)
                {
                    continue;
                }

                track.Gain = ss.Volume;
                track.Pan = ss.Pan;

                MemoryTrack? memoryTrack = ss.RustMemoryTrack;
                if (memoryTrack is not null)
                {
                    memoryTrack.Loop = ss.Loop;
                }

                ss.SetOutputLevels(ss.State == AudioState.Playing ? track.Peaks : (0f, 0f));
            }
            else if (source is InputSource ins)
            {
                AudioTrack? track = ins.RustTrack;
                if (track is null)
                {
                    continue;
                }

                track.Gain = ins.Volume;
                track.Pan = ins.Pan;

                // Live capture has no loop; mirror the native track's metering peaks onto the source.
                ins.SetOutputLevels(ins.State == AudioState.Playing ? track.Peaks : (0f, 0f));
            }
        }
    }

    /// <summary>
    /// Mirrors the mixer's master volume and master pan onto the shared session's native master bus
    /// and reads back the session's master output peaks into <c>_leftPeak</c>/<c>_rightPeak</c>.
    /// Called on the control-rate sync tick, so a live master-volume or master-pan change propagates
    /// promptly and the mixer's <c>LeftPeak</c>/<c>RightPeak</c> metering stays current even though
    /// the managed <c>MixThread</c> (which computed them in legacy mode) does not run here.
    /// </summary>
    internal void SyncRustMasterOnce()
    {
        MultiTrackSession? session;
        lock (_rustSessionLock)
        {
            session = _rustSession;
        }

        if (session is null)
        {
            return;
        }

        session.MasterGain = _masterVolume;
        session.MasterPan = _masterPan;

        (float left, float right) = session.GetMasterPeaks();
        _leftPeak = left;
        _rightPeak = right;
    }

    /// <summary>
    /// Applies a source's <see cref="FileSource.StartOffset"/> to its native track relative to the
    /// given project-timeline position, reproducing the managed engine's per-track offset:
    /// <c>content = projectPosition − StartOffset</c>. When the content position is non-negative the
    /// track's decoder is seeked there; when it is negative the track is held silent for the
    /// remaining <c>(StartOffset − projectPosition)</c> frames (native start-delay) and its content
    /// starts from zero — so a positive offset delays the track's entry and a negative offset
    /// pre-advances it, sample-accurately against the shared clock. Must be called under
    /// <see cref="_rustSessionLock"/>.
    /// </summary>
    /// <param name="fs">The file source whose offset is applied.</param>
    /// <param name="projectPosition">Current project-timeline position, in seconds.</param>
    private void ApplyRustStartOffsetLocked(FileSource fs, double projectPosition)
    {
        AudioTrack? track = fs.RustTrack;
        if (track is null)
        {
            return;
        }

        double offset = fs.StartOffset;
        double projectLocal = projectPosition - offset;

        if (projectLocal >= 0.0)
        {
            // The seek target is a project (wall-clock) position, but the decoder addresses the
            // content timeline: at tempo r, a project-local time t maps to content t × r. This
            // reproduces the legacy chain's `filePosition = trackTime × tempo`, so a seek at a
            // non-unity tempo lands the audio at the position the caller intends instead of one
            // scaled by the tempo. FileSource.Seek takes content seconds.
            float tempo = fs.Tempo <= 0f ? 1f : fs.Tempo;
            double contentTarget = projectLocal * tempo;
            fs.Seek(Math.Clamp(contentTarget, 0.0, fs.Duration));
            track.SetStartDelayFrames(0);
        }
        else
        {
            fs.Seek(0.0);
            track.SetStartDelayFrames((long)Math.Round(-projectLocal * _config.SampleRate));
        }

        _rustAppliedStartOffsets[fs.Id] = offset;
    }

    /// <summary>
    /// Realigns any source whose <see cref="FileSource.StartOffset"/> changed since it was last
    /// applied, relative to the current project-clock position. Called on the control-rate tick so a
    /// live offset edit (aligning a drifted track, or shifting one against the sync position) takes
    /// effect promptly without an explicit seek. A track whose offset is unchanged is left untouched.
    /// </summary>
    internal void SyncRustStartOffsetsOnce()
    {
        double project = _masterClock.CurrentTimestamp;

        IAudioSource[] sources = Volatile.Read(ref _rustSourceSnapshot);
        lock (_rustSessionLock)
        {
            foreach (IAudioSource source in sources)
            {
                if (source is not FileSource fs || fs.RustTrack is null)
                {
                    continue;
                }

                bool known = _rustAppliedStartOffsets.TryGetValue(fs.Id, out double applied);
                if (known && applied == fs.StartOffset)
                {
                    continue;
                }

                try
                {
                    ApplyRustStartOffsetLocked(fs, project);
                }
                catch
                {
                    // best-effort realign; never let one source abort the tick
                }
            }
        }
    }

    /// <summary>
    /// Mirrors a source's <c>OutputChannelMapping</c> onto its native track, reproducing the managed
    /// mixer's selective channel routing: source channel <c>i</c> is summed into output channel
    /// <c>mapping[i]</c> and every unmapped output channel receives silence from this track. The map
    /// is read from the outermost mixer source (a <see cref="SourceWithEffects"/> forwards its inner
    /// source's config) and applied to the track owned by the resolved <see cref="FileSource"/>.
    /// Only re-applies when the mapping changed since the last call. Must be called under
    /// <see cref="_rustSessionLock"/>.
    /// </summary>
    /// <param name="source">The mixer source carrying the mapping (plain or effect-wrapped).</param>
    /// <param name="key">The backed source's id (change-detection / bookkeeping key).</param>
    /// <param name="track">The native track that owns the routing, or <see langword="null"/>.</param>
    private void ApplyChannelMapLocked(IAudioSource source, Guid key, AudioTrack? track)
    {
        if (track is null)
        {
            return;
        }

        int[]? current = (source as BaseAudioSource)?.OutputChannelMapping;

        // A source that has never been routed and is not routed now needs no native call.
        if (current is null && !_rustAppliedChannelMaps.ContainsKey(key))
        {
            return;
        }

        if (_rustAppliedChannelMaps.TryGetValue(key, out int[]? applied)
            && ChannelMapsEqual(applied, current))
        {
            return;
        }

        if (current is null || current.Length == 0)
        {
            track.ClearOutputChannelMap();
        }
        else
        {
            track.SetOutputChannelMap(current);
        }

        // Store an independent clone so a later in-place edit of the same array is detected.
        _rustAppliedChannelMaps[key] = current is null ? null : (int[])current.Clone();
    }

    /// <summary>
    /// Value-compares two output-channel maps (either may be <see langword="null"/>).
    /// </summary>
    private static bool ChannelMapsEqual(int[]? a, int[]? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }
        if (a is null || b is null || a.Length != b.Length)
        {
            return false;
        }
        return a.AsSpan().SequenceEqual(b);
    }

    /// <summary>
    /// Re-applies every attached source's <c>OutputChannelMapping</c> onto its native track when it
    /// has changed since the last tick, so a live re-route (or an initial mapping set after the
    /// source was added) takes effect promptly. Called on the control-rate sync tick.
    /// </summary>
    internal void SyncRustChannelMapsOnce()
    {
        IAudioSource[] sources = Volatile.Read(ref _rustSourceSnapshot);
        lock (_rustSessionLock)
        {
            foreach (IAudioSource source in sources)
            {
                (Guid id, AudioTrack? track) = ResolveRustBacked(source);
                if (track is null)
                {
                    continue;
                }

                try
                {
                    ApplyChannelMapLocked(source, id, track);
                }
                catch
                {
                    // best-effort re-route; never let one source abort the tick
                }
            }
        }
    }

    /// <summary>
    /// Resolves the native-backed source behind a mixer source (file or sample, plain or
    /// effect-wrapped) to its bookkeeping id and current native track. Returns a null track when the
    /// source has no native backend.
    /// </summary>
    private static (Guid Id, AudioTrack? Track) ResolveRustBacked(IAudioSource source)
    {
        FileSource? fs = ResolveFileSource(source);
        if (fs is not null)
        {
            return (fs.Id, fs.RustTrack);
        }

        SampleSource? ss = ResolveSampleSource(source);
        if (ss is not null)
        {
            return (ss.Id, ss.RustTrack);
        }

        InputSource? ins = ResolveInputSource(source);
        if (ins is not null)
        {
            return (ins.Id, ins.RustTrack);
        }

        return (Guid.Empty, null);
    }

    /// <summary>
    /// Routes a managed master effect onto the shared session's native master bus in Rust-native
    /// mode (plan E.3). A paired native effect is created and the managed effect's parameters are
    /// mirrored onto it; the managed effect's own DSP does not run. No-op in legacy mode or when the
    /// effect type has no native adapter yet (E.4).
    /// </summary>
    /// <param name="effect">The managed master effect being added.</param>
    internal void AttachMasterEffectToRust(IEffectProcessor effect)
    {
        if (!_rustNative || effect is null)
        {
            return;
        }

        // VST3 plugins are hosted natively (plan E.6): the managed control plane owns the plugin and
        // the Rust master bus calls its process entry point directly. The managed Enabled/Mix are
        // mirrored onto the native bridge like any other paired effect; plugin-specific parameters go
        // straight to the plugin through its own SetParameter, not through this mirror.
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
                _rustSession ??= new MultiTrackSession(
                    (float)_config.SampleRate,
                    (ushort)_config.Channels);

                MasterEffectChain chain = _rustSession.MasterEffects;
                object native = chain.AddVst(
                    vst.NativePluginHandle,
                    vst.NativeProcessAudioPointer,
                    (ushort)_config.Channels,
                    (uint)_config.BufferSize,
                    (uint)Math.Max(0, vst.LatencySamples));
                var pair = new RustEffectPair(effect, native, chain);
                _rustMasterEffects.Add(pair);
                MirrorPairLocked(pair);
            }

            return;
        }

        if (!RustEffectAdapters.TryGetEffectType(effect, out var effectType))
        {
            // No native counterpart: in the Rust-native chain the managed DSP does not run, so the
            // effect produces no master processing. (SmartMaster is routed natively via its own
            // adapter; VST3 has its dedicated hosting branch above.)
            System.Diagnostics.Debug.WriteLine(
                $"[OwnAudio] Master effect '{effect.GetType().Name}' has no native adapter and is " +
                "inactive in the Rust-native chain.");
            return;
        }

        lock (_rustSessionLock)
        {
            _rustSession ??= new MultiTrackSession(
                (float)_config.SampleRate,
                (ushort)_config.Channels);

            MasterEffectChain chain = _rustSession.MasterEffects;
            object native = chain.Add(effectType, _config.SampleRate);
            var pair = new RustEffectPair(effect, native, chain);
            _rustMasterEffects.Add(pair);
            MirrorPairLocked(pair);
        }
    }

    /// <summary>
    /// Removes the native master effect paired with <paramref name="effect"/> (the inverse of
    /// <see cref="AttachMasterEffectToRust"/>). No-op when not paired.
    /// </summary>
    /// <param name="effect">The managed master effect being removed.</param>
    internal void DetachMasterEffectFromRust(IEffectProcessor effect)
    {
        if (!_rustNative || effect is null)
        {
            return;
        }

        lock (_rustSessionLock)
        {
            int index = _rustMasterEffects.FindIndex(p => ReferenceEquals(p.Managed, effect));
            if (index < 0)
            {
                return;
            }

            if (_rustSession is not null)
            {
                _rustMasterEffects[index].RemoveNativeBestEffort();
            }

            _rustMasterEffects.RemoveAt(index);
        }
    }

    /// <summary>
    /// Removes every native master effect (the inverse of clearing the managed master chain).
    /// </summary>
    internal void ClearRustMasterEffects()
    {
        if (!_rustNative)
        {
            return;
        }

        lock (_rustSessionLock)
        {
            if (_rustSession is not null)
            {
                foreach (var pair in _rustMasterEffects)
                {
                    pair.RemoveNativeBestEffort();
                }
            }

            _rustMasterEffects.Clear();
        }
    }

    /// <summary>
    /// Mirrors every paired master effect's current managed parameters onto its native effect once.
    /// Called on the control-rate sync tick and available for deterministic tests.
    /// </summary>
    internal void MirrorRustMasterEffectsOnce()
    {
        lock (_rustSessionLock)
        {
            if (_rustSession is null)
            {
                return;
            }

            foreach (var pair in _rustMasterEffects)
            {
                MirrorPairLocked(pair);
            }
        }
    }

    /// <summary>
    /// Mirrors a managed effect's parameters onto its native effect, enqueuing a native
    /// <c>set_param</c> only for values that changed since the last mirror. This is essential: the
    /// mirror runs every control-rate tick, and pushing every parameter unconditionally would flood
    /// the lock-free mixer command queue (eventually overflowing it and failing later operations).
    /// The pair's pre-bound <see cref="RustEffectPair.Sink"/> carries the change detection and the
    /// chain dispatch, so this per-tick path allocates nothing.
    /// </summary>
    private static void MirrorPairLocked(RustEffectPair pair)
    {
        // A hosted VST3 plugin is enabled/disabled through native bypass, not the Rust effect's
        // enabled parameter: JUCE's processBlockBypassed keeps the output time-aligned with the
        // processed path (a Rust dry/wet switch would jump by the plugin's latency on every toggle).
        // Plugin-specific parameters flow through the plugin's own SetParameter, so they are not
        // mirrored here — only the on/off state is, and only when it actually changes.
        if (pair.Managed is VST3EffectProcessor vst)
        {
            const uint enabledParamId = 0;
            float enabledValue = vst.Enabled ? 1f : 0f;
            if (pair.LastParams.TryGetValue(enabledParamId, out float lastEnabled) && lastEnabled.Equals(enabledValue))
            {
                return;
            }

            pair.LastParams[enabledParamId] = enabledValue;
            vst.SetNativeBypass(!vst.Enabled);
            return;
        }

        RustEffectAdapters.Mirror(pair.Managed, pair.Sink);
    }

    /// <summary>
    /// Reconciles every <see cref="SourceWithEffects"/>-wrapped source's managed effect list onto its
    /// native track effect chain, and mirrors each paired effect's parameters (plan E.2). Called on
    /// the control-rate sync tick.
    /// </summary>
    /// <remarks>
    /// The per-track effects are added directly to the wrapper (<c>SourceWithEffects.AddEffect</c>),
    /// not through the mixer, so there is no explicit hook; the wrapper's effect list is polled here
    /// and the native chain rebuilt in order whenever it changes (effects with a registered adapter,
    /// including the composite SmartMaster, plus natively-hosted VST3). Parameters are then mirrored
    /// every tick.
    /// </remarks>
    internal void ReconcileRustTrackEffectsOnce()
    {
        lock (_rustSessionLock)
        {
            foreach (RustTrackEffectRouting routing in _rustEffectSources)
            {
                AudioTrack? track = routing.Backing.RustTrack;
                if (track is null)
                {
                    continue;
                }

                // Cheap change-detection: only re-snapshot the managed effect list (a heap
                // allocation) and rebuild the native chain when the wrapper's effect version has
                // actually moved since the last reconcile. In steady state this is a single integer
                // comparison per track per tick, with no allocation and no LINQ.
                int version = routing.Source.EffectsVersion;
                if (version != routing.CachedVersion)
                {
                    IEffectProcessor[] managed = routing.Source.GetEffects();

                    // Rebuild the native chain in order: drop the current pairings, then re-add every
                    // adaptable managed effect. This preserves chain order across add/remove/reorder.
                    foreach (var pair in routing.Pairs)
                    {
                        pair.RemoveNativeBestEffort();
                    }

                    routing.Pairs.Clear();

                    TrackEffectChain chain = track.Effects;
                    foreach (IEffectProcessor effect in managed)
                    {
                        // VST3 plugins are hosted natively on the track (plan E.6); other effects with
                        // a registered adapter — including the composite SmartMaster — go through their
                        // native counterpart. Effects without an adapter (or a VST not yet
                        // audio-initialized) are skipped this pass.
                        if (effect is VST3EffectProcessor vst && vst.CanHostNatively)
                        {
                            object native = chain.AddVst(
                                vst.NativePluginHandle,
                                vst.NativeProcessAudioPointer,
                                (ushort)_config.Channels,
                                (uint)_config.BufferSize,
                                (uint)Math.Max(0, vst.LatencySamples));
                            routing.Pairs.Add(new RustEffectPair(effect, native, chain));
                        }
                        else if (RustEffectAdapters.TryGetEffectType(effect, out var effectType))
                        {
                            object native = chain.Add(effectType, (float)_config.SampleRate);
                            routing.Pairs.Add(new RustEffectPair(effect, native, chain));
                        }
                    }

                    routing.CachedVersion = version;
                }

                foreach (var pair in routing.Pairs)
                {
                    MirrorPairLocked(pair);
                }
            }
        }
    }

    /// <summary>
    /// Drives one network drift-correction pass over every attached file source, nudging each
    /// track's tempo (or hard-seeking) toward the network-controlled master clock. No-op for
    /// sources that are not playing under a network-controlled clock. Called on the sync tick and
    /// available for deterministic tests.
    /// </summary>
    internal void DriveRustNativeSyncOnce()
    {
        IAudioSource[] sources = Volatile.Read(ref _rustSourceSnapshot);
        foreach (IAudioSource source in sources)
        {
            if (source is FileSource fs)
            {
                fs.ApplyRustNativeSync();
            }
        }
    }

    /// <summary>
    /// Starts the control-rate sync tick if it is not already running. Idempotent.
    /// </summary>
    private void StartRustSyncTick()
    {
        lock (_rustSessionLock)
        {
            if (_rustSyncThread is not null)
            {
                return;
            }

            _rustSyncStop = false;
            _rustSyncThread = new Thread(RustSyncLoop)
            {
                Name = "AudioMixer.RustControlSync",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
            };
            _rustSyncThread.Start();
        }
    }

    /// <summary>
    /// Signals and joins the control-rate sync tick. Idempotent.
    /// </summary>
    private void StopRustSyncTick()
    {
        Thread? thread;
        lock (_rustSessionLock)
        {
            thread = _rustSyncThread;
            _rustSyncStop = true;
            _rustSyncThread = null;
        }

        if (thread is not null && thread != Thread.CurrentThread)
        {
            thread.Join();
        }
    }

    /// <summary>
    /// The control-rate sync loop: periodically mirrors source control state onto the tracks until
    /// signalled to stop. Exceptions are swallowed so a transient track error never kills the tick.
    /// </summary>
    private void RustSyncLoop()
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
                AdvanceMasterClockFromRustTracks();
                PollRustStreamFaultOnce();

                // A clean pass clears the consecutive-error run so only a persistent fault
                // accumulates toward the threshold.
                _rustSyncConsecutiveErrors = 0;
            }
            catch (Exception ex)
            {
                // A transient error must not kill the tick, but a deterministically-repeating one
                // (dropped handle, corrupt source) is surfaced instead of silently swallowed, and
                // the tick stops once it has clearly faulted rather than spinning for hours.
                if (HandleLoopError("Rust-native control sync tick", ex, ref _rustSyncConsecutiveErrors))
                {
                    break;
                }
            }

            Thread.Sleep(RustControlSyncIntervalMs);
        }
    }

    /// <summary>
    /// Drives the master clock from the authoritative native track position in local (non-network)
    /// Rust-native playback.
    /// </summary>
    /// <remarks>
    /// The managed <c>MixThread</c> — which advances the clock per buffer in legacy mode — does not
    /// run here, so without this the clock would be frozen and the reported position would drift from
    /// the audio. The clock is set to the furthest-along playing file source's project position
    /// (start offset + rendered content time). Network-controlled clocks are left to the external
    /// synchroniser (the tracks are corrected toward the clock by <see cref="DriveRustNativeSyncOnce"/>
    /// instead).
    /// </remarks>
    private void AdvanceMasterClockFromRustTracks()
    {
        if (_masterClock.IsNetworkControlled)
        {
            return;
        }

        double projectPos = -1.0;
        IAudioSource[] sources = Volatile.Read(ref _rustSourceSnapshot);
        foreach (IAudioSource source in sources)
        {
            if (source is FileSource fs && fs.State == AudioState.Playing)
            {
                // A track still in its start-offset silence (positive offset, not yet entered) has
                // not begun advancing content; excluding it keeps it from dragging the project clock
                // forward to its offset before it actually enters.
                if (fs.StartOffset > 0.0 && (fs.RustTrack?.RenderedFrames ?? 0UL) == 0UL)
                {
                    continue;
                }

                // Drive the master clock from the source's *project* (wall-clock) position, not its
                // content-time Position: the shared clock must advance at the real playback rate
                // regardless of tempo (a stretched track would otherwise run it at its content
                // rate and desync every other track), matching the legacy chain where the clock
                // advanced by output frames while Position reported content time.
                double p = fs.StartOffset + fs.RustNativeRealPosition;
                if (p > projectPos)
                {
                    projectPos = p;
                }
            }
        }

        if (projectPos >= 0.0)
        {
            _masterClock.SeekTo(projectPos);
        }
    }

    /// <summary>
    /// Polls the native output stream's backend error state and raises <see cref="StreamFaulted"/>
    /// once whenever a new fault (device lost, backend error) has been reported since the last poll.
    /// </summary>
    /// <remarks>
    /// The native mixer renders on the audio thread, so a device that disappears mid-playback would
    /// otherwise stop the stream silently with nothing on the managed side ever learning of it. The
    /// error count is monotonic; comparing it against the last-seen value detects a fresh fault even
    /// when the same kind repeats. The stream handle is read under the session lock to avoid racing a
    /// concurrent stop/dispose, but the event is raised outside the lock so a handler can safely call
    /// back into the mixer.
    /// </remarks>
    internal void PollRustStreamFaultOnce()
    {
        AudioStreamErrorKind kind;
        ulong count;

        lock (_rustSessionLock)
        {
            AudioOutputStream? stream = _rustOutputStream;
            if (stream is null)
            {
                return;
            }

            kind = stream.PollErrorState(out count);
        }

        if (count == _rustLastStreamErrorCount)
        {
            return;
        }

        _rustLastStreamErrorCount = count;

        if (kind == AudioStreamErrorKind.None)
        {
            return;
        }

        AudioStreamFaultKind faultKind = kind == AudioStreamErrorKind.DeviceNotAvailable
            ? AudioStreamFaultKind.DeviceNotAvailable
            : AudioStreamFaultKind.BackendSpecific;

        StreamFaulted?.Invoke(this, new AudioStreamFaultEventArgs(faultKind, count));
    }

    /// <summary>
    /// Applies a user seek across the shared Rust-native session: moves the master clock to the
    /// target project position and repositions every attached file source's native decoder to the
    /// matching content position. Unlike legacy, nothing in the managed pipeline propagates the seek
    /// to the tracks, so it is done explicitly here.
    /// </summary>
    /// <param name="projectSeconds">Target position on the project timeline, in seconds.</param>
    internal void SeekRustNative(double projectSeconds)
    {
        if (projectSeconds < 0.0)
        {
            projectSeconds = 0.0;
        }

        _masterClock.SeekTo(projectSeconds);

        lock (_rustSessionLock)
        {
            foreach (IAudioSource source in _sources.Values)
            {
                if (source is not FileSource fs)
                {
                    continue;
                }

                try
                {
                    // Reposition each track to the seek target adjusted for its start offset,
                    // holding a not-yet-entered track silent (positive offset) exactly as the
                    // managed engine did with content = masterTimestamp − startOffset.
                    ApplyRustStartOffsetLocked(fs, projectSeconds);
                }
                catch
                {
                    // best-effort per-source seek; never let one source abort the whole seek
                }
            }
        }
    }

    /// <summary>
    /// Opens the shared session's native output on the underlying Rust engine's device (once) and
    /// suspends that engine's own push-based output so the two do not compete. Degrades to a no-op
    /// when there is no session or the engine is not the native Rust engine.
    /// </summary>
    private void OpenRustOutputLocked()
    {
        if (_rustOutputStream is not null || _rustSession is null)
        {
            return;
        }

        RustAudioEngine? rustEngine = _engine as RustAudioEngine;
        AudioEngine? nativeEngine = rustEngine?.NativeEngine;
        if (rustEngine is null || nativeEngine is null)
        {
            return;
        }

        _rustOutputStream = _rustSession.OpenOutput(nativeEngine);
        // A fresh stream starts with a clean error count; baseline the fault poller to it.
        _rustLastStreamErrorCount = 0;
        rustEngine.SuspendOutput();
        _rustSuspendedEngine = rustEngine;
    }

    /// <summary>
    /// Rust-native transport start: opens the device output (once) and starts all tracks against
    /// the shared clock. No-op on the transport when the output could not be opened (degraded).
    /// </summary>
    private void StartRustOutput()
    {
        lock (_rustSessionLock)
        {
            OpenRustOutputLocked();

            if (_rustOutputStream is not null)
            {
                // Position every source for its start offset BEFORE PlayAll, so the first
                // rendered block already reflects each track's offset — a sample-accurate start.
                // A zero-offset source is left at its current content position (untouched, as
                // before) but still recorded so the control-rate tick does not re-seek it.
                double project = _masterClock.CurrentTimestamp;
                foreach (IAudioSource source in _sources.Values)
                {
                    if (source is not FileSource fs || fs.RustTrack is null)
                    {
                        continue;
                    }

                    try
                    {
                        if (fs.StartOffset != 0.0)
                        {
                            ApplyRustStartOffsetLocked(fs, project);
                        }
                        else
                        {
                            fs.RustTrack.SetStartDelayFrames(0);
                            _rustAppliedStartOffsets[fs.Id] = 0.0;
                        }
                    }
                    catch
                    {
                        // best-effort per-source offset; never let one source block the start
                    }
                }

                _rustSession?.PlayAll();
            }
        }
    }

    /// <summary>
    /// Opens the shared session's native output when a source is attached to a Rust-native mixer that
    /// is already running but whose output was not opened at <see cref="Start"/> time. This honours the
    /// documented "start the mixer, then add sources" ordering: because the session is created lazily
    /// with the first source, a <see cref="Start"/> call made before any source exists finds a null
    /// session and opens no device stream, so the first source added afterwards must open it. The
    /// sources are started individually by their own <c>Play()</c> in <see cref="AddSource"/>, so only
    /// the device stream needs opening here (no <c>PlayAll</c>). No-op in legacy mode, when the mixer
    /// is not running, or when the output is already open.
    /// </summary>
    private void EnsureRustOutputStartedAfterAttach()
    {
        if (!_rustNative || !_isRunning)
        {
            return;
        }

        lock (_rustSessionLock)
        {
            if (_rustOutputStream is not null)
            {
                return;
            }

            OpenRustOutputLocked();
        }
    }

    /// <summary>
    /// Rust-native transport stop: stops all tracks when the device output is active.
    /// </summary>
    private void StopRustOutput()
    {
        lock (_rustSessionLock)
        {
            if (_rustOutputStream is not null)
            {
                _rustSession?.StopAll();
            }
        }
    }

    /// <summary>
    /// Rust-native transport pause: pauses all tracks when the device output is active.
    /// </summary>
    private void PauseRustOutput()
    {
        lock (_rustSessionLock)
        {
            if (_rustOutputStream is not null)
            {
                _rustSession?.PauseAll();
            }
        }
    }

    /// <summary>
    /// Tears down the shared session (disposing every track and feeder it owns, and closing the
    /// native output stream) and the sync tick, then resumes the engine's own output.
    /// </summary>
    private void DisposeRustSession()
    {
        StopRustSyncTick();

        lock (_rustSessionLock)
        {
            // The native master + track effects live on the session's mixer; disposing the session
            // frees them, so just drop the managed pairings.
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
