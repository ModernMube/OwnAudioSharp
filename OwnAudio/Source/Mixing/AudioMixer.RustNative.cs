using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Ownaudio.Audio.Tracks;
using Ownaudio.Safe;
using OwnaudioNET.Core;
using OwnaudioNET.Engine;
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
    /// a value actually changes — otherwise the mixer command queue would flood every tick.
    /// </summary>
    private sealed class RustEffectPair
    {
        public RustEffectPair(IEffectProcessor managed, object native)
        {
            Managed = managed;
            Native = native;
        }

        /// <summary>The managed effect acting as the parameter model.</summary>
        public IEffectProcessor Managed { get; }

        /// <summary>The paired native effect wrapper (from the master or track chain).</summary>
        public object Native { get; }

        /// <summary>Last value pushed per native parameter id, for change detection.</summary>
        public Dictionary<uint, float> LastParams { get; } = new();
    }

    /// <summary>
    /// Per-track effect routing state for sources wrapped in a <see cref="SourceWithEffects"/>: the
    /// wrapper's effect list is reconciled onto the native track's effect chain on the control-rate
    /// tick. Guarded by <see cref="_rustSessionLock"/>.
    /// </summary>
    private readonly List<RustTrackEffectRouting> _rustEffectSources = new();

    /// <summary>
    /// Tracks a <see cref="SourceWithEffects"/> and its underlying <see cref="FileSource"/> together
    /// with the managed→native effect pairings currently installed on the native track.
    /// </summary>
    private sealed class RustTrackEffectRouting
    {
        public RustTrackEffectRouting(SourceWithEffects source, FileSource file)
        {
            Source = source;
            File = file;
        }

        /// <summary>The wrapper whose effect list drives the native track chain.</summary>
        public SourceWithEffects Source { get; }

        /// <summary>The underlying file source that owns the native track.</summary>
        public FileSource File { get; }

        /// <summary>The last-seen managed effect list, to detect changes cheaply.</summary>
        public IEffectProcessor[] Cached { get; set; } = Array.Empty<IEffectProcessor>();

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
    /// Attaches a source to the shared session in Rust-native mode: opens a matching native file
    /// track and binds it to the underlying <see cref="FileSource"/> (unwrapping a
    /// <see cref="SourceWithEffects"/> when present, and registering it for native per-track effect
    /// routing). Non-file sources are ignored (they keep the legacy path).
    /// </summary>
    /// <param name="source">The source being added to the mixer.</param>
    private void AttachSourceToRustSession(IAudioSource source)
    {
        FileSource? fs = ResolveFileSource(source);
        if (fs?.FilePath is null)
        {
            return;
        }

        lock (_rustSessionLock)
        {
            _rustSession ??= new MultiTrackSession(
                (float)_config.SampleRate,
                (ushort)_config.Channels);

            FileTrack fileTrack = _rustSession.AddFileTrack(fs.FilePath);
            fs.AttachRustTrack(fileTrack.Track, fileTrack);

            // A source wrapped in SourceWithEffects carries per-track effects; route them onto the
            // native track's effect chain (reconciled on the control-rate tick).
            if (source is SourceWithEffects swe)
            {
                _rustEffectSources.Add(new RustTrackEffectRouting(swe, fs));
            }
        }
    }

    /// <summary>
    /// Detaches a source from the shared session and removes (and disposes) its track. No-op for
    /// non-file sources or sources with no attached track.
    /// </summary>
    /// <param name="source">The source being removed from the mixer.</param>
    private void DetachSourceFromRustSession(IAudioSource source)
    {
        FileSource? fs = ResolveFileSource(source);
        if (fs is null)
        {
            return;
        }

        lock (_rustSessionLock)
        {
            _rustEffectSources.RemoveAll(r => ReferenceEquals(r.Source, source));

            AudioTrack? track = fs.RustTrack;
            fs.DetachRustTrack();

            if (track is not null && _rustSession is not null)
            {
                _rustSession.RemoveTrack(track);
            }
        }
    }

    /// <summary>
    /// Mirrors every attached file source's <c>Volume</c> and <c>Loop</c> onto its track and file
    /// source once. Called on the sync tick and available for deterministic tests.
    /// </summary>
    internal void SyncRustControlStateOnce()
    {
        foreach (IAudioSource source in _sources.Values)
        {
            if (source is not FileSource fs)
            {
                continue;
            }

            AudioTrack? track = fs.RustTrack;
            if (track is null)
            {
                continue;
            }

            track.Gain = fs.Volume;

            FileTrack? fileTrack = fs.RustFileTrack;
            if (fileTrack is not null)
            {
                fileTrack.Loop = fs.Loop;
            }
        }
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

        if (!RustEffectAdapters.TryGetEffectType(effect, out var effectType))
        {
            // No native counterpart (VST3, or the composite SmartMaster): in the Rust-native chain
            // its managed DSP does not run, so it produces no master processing. VST3 support lands
            // in plan E.6 (native hosting via OwnVST3Juce).
            System.Diagnostics.Debug.WriteLine(
                $"[OwnAudio] Master effect '{effect.GetType().Name}' has no native adapter and is " +
                "inactive in the Rust-native chain (VST3 → plan E.6; SmartMaster is a composite).");
            return;
        }

        lock (_rustSessionLock)
        {
            _rustSession ??= new MultiTrackSession(
                (float)_config.SampleRate,
                (ushort)_config.Channels);

            object native = _rustSession.MasterEffects.Add(effectType, _config.SampleRate);
            var pair = new RustEffectPair(effect, native);
            _rustMasterEffects.Add(pair);
            MirrorMasterEffectLocked(pair);
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

            TryRemoveNative(() => _rustSession?.MasterEffects.Remove(_rustMasterEffects[index].Native));
            _rustMasterEffects.RemoveAt(index);
        }
    }

    /// <summary>
    /// Runs a native effect-removal action, swallowing a transient failure (for example a momentarily
    /// full command queue) so tearing down effects never crashes the app. A skipped removal only
    /// leaves the native effect in place until the session is disposed.
    /// </summary>
    private static void TryRemoveNative(Action remove)
    {
        try
        {
            remove();
        }
        catch (Ownaudio.Safe.Exceptions.OwnAudioException)
        {
            // Best-effort: the native effect stays until the session is disposed.
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
                    RustEffectPair captured = pair;
                    TryRemoveNative(() => _rustSession.MasterEffects.Remove(captured.Native));
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
                MirrorMasterEffectLocked(pair);
            }
        }
    }

    /// <summary>
    /// Pushes one managed master effect's changed parameters onto its native effect. Must be called
    /// under <see cref="_rustSessionLock"/>.
    /// </summary>
    private void MirrorMasterEffectLocked(RustEffectPair pair)
    {
        MasterEffectChain chain = _rustSession!.MasterEffects;
        MirrorPairLocked(pair, (native, paramId, value) => chain.SetParam(native, paramId, value));
    }

    /// <summary>
    /// Mirrors a managed effect's parameters onto its native effect, enqueuing a native
    /// <c>set_param</c> only for values that changed since the last mirror. This is essential: the
    /// mirror runs every control-rate tick, and pushing every parameter unconditionally would flood
    /// the lock-free mixer command queue (eventually overflowing it and failing later operations).
    /// </summary>
    private static void MirrorPairLocked(RustEffectPair pair, Func<object, uint, float, bool> setParam)
    {
        RustEffectAdapters.Mirror(pair.Managed, (paramId, value) =>
        {
            if (pair.LastParams.TryGetValue(paramId, out float last) && last.Equals(value))
            {
                return;
            }

            pair.LastParams[paramId] = value;
            setParam(pair.Native, paramId, value);
        });
    }

    /// <summary>
    /// Reconciles every <see cref="SourceWithEffects"/>-wrapped source's managed effect list onto its
    /// native track effect chain, and mirrors each paired effect's parameters (plan E.2). Called on
    /// the control-rate sync tick.
    /// </summary>
    /// <remarks>
    /// The per-track effects are added directly to the wrapper (<c>SourceWithEffects.AddEffect</c>),
    /// not through the mixer, so there is no explicit hook; the wrapper's effect list is polled here
    /// and the native chain rebuilt in order whenever it changes (adaptable effects only — VST3 /
    /// SmartMaster are skipped). Parameters are then mirrored every tick.
    /// </remarks>
    internal void ReconcileRustTrackEffectsOnce()
    {
        lock (_rustSessionLock)
        {
            foreach (RustTrackEffectRouting routing in _rustEffectSources)
            {
                AudioTrack? track = routing.File.RustTrack;
                if (track is null)
                {
                    continue;
                }

                IEffectProcessor[] managed = routing.Source.GetEffects();

                if (!managed.SequenceEqual(routing.Cached))
                {
                    // Rebuild the native chain in order: drop the current pairings, then re-add every
                    // adaptable managed effect. This preserves chain order across add/remove/reorder.
                    AudioTrack rebuildTrack = track;
                    foreach (var pair in routing.Pairs)
                    {
                        RustEffectPair captured = pair;
                        TryRemoveNative(() => rebuildTrack.Effects.Remove(captured.Native));
                    }

                    routing.Pairs.Clear();

                    foreach (IEffectProcessor effect in managed)
                    {
                        if (RustEffectAdapters.TryGetEffectType(effect, out var effectType))
                        {
                            object native = track.Effects.Add(effectType, (float)_config.SampleRate);
                            routing.Pairs.Add(new RustEffectPair(effect, native));
                        }
                    }

                    routing.Cached = managed;
                }

                TrackEffectChain trackChain = track.Effects;
                foreach (var pair in routing.Pairs)
                {
                    MirrorPairLocked(pair, (native, paramId, value) => trackChain.SetParam(native, paramId, value));
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
        foreach (IAudioSource source in _sources.Values)
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
                MirrorRustMasterEffectsOnce();
                ReconcileRustTrackEffectsOnce();
                DriveRustNativeSyncOnce();
                AdvanceMasterClockFromRustTracks();
            }
            catch
            {
                // best-effort control sync; never let a transient error kill the tick
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
        foreach (IAudioSource source in _sources.Values)
        {
            if (source is FileSource fs && fs.State == AudioState.Playing)
            {
                double p = fs.StartOffset + fs.Position;
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

        foreach (IAudioSource source in _sources.Values)
        {
            if (source is not FileSource fs)
            {
                continue;
            }

            double content = Math.Clamp(projectSeconds - fs.StartOffset, 0.0, fs.Duration);
            try
            {
                fs.Seek(content);
            }
            catch
            {
                // best-effort per-source seek; never let one source abort the whole seek
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
                _rustSession?.PlayAll();
            }
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
