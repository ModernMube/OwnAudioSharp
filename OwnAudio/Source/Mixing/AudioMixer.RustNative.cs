using System;
using System.Collections.Generic;
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
    private readonly List<(IEffectProcessor Managed, object Native)> _rustMasterEffects = new();

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
    /// Attaches a source to the shared session in Rust-native mode: opens a matching native
    /// file track and binds it to the source. Non-file sources are ignored (they keep the legacy path).
    /// </summary>
    /// <param name="source">The source being added to the mixer.</param>
    private void AttachSourceToRustSession(IAudioSource source)
    {
        if (source is not FileSource fs || fs.FilePath is null)
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
        }
    }

    /// <summary>
    /// Detaches a source from the shared session and removes (and disposes) its track. No-op for
    /// non-file sources or sources with no attached track.
    /// </summary>
    /// <param name="source">The source being removed from the mixer.</param>
    private void DetachSourceFromRustSession(IAudioSource source)
    {
        if (source is not FileSource fs)
        {
            return;
        }

        lock (_rustSessionLock)
        {
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
            return;
        }

        lock (_rustSessionLock)
        {
            _rustSession ??= new MultiTrackSession(
                (float)_config.SampleRate,
                (ushort)_config.Channels);

            object native = _rustSession.MasterEffects.Add(effectType, _config.SampleRate);
            _rustMasterEffects.Add((effect, native));
            MirrorMasterEffectLocked(effect, native);
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

            _rustSession?.MasterEffects.Remove(_rustMasterEffects[index].Native);
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
                    _rustSession.MasterEffects.Remove(pair.Native);
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
                MirrorMasterEffectLocked(pair.Managed, pair.Native);
            }
        }
    }

    /// <summary>
    /// Pushes one managed master effect's parameters onto its native effect. Must be called under
    /// <see cref="_rustSessionLock"/>.
    /// </summary>
    private void MirrorMasterEffectLocked(IEffectProcessor managed, object native)
    {
        MasterEffectChain chain = _rustSession!.MasterEffects;
        RustEffectAdapters.Mirror(managed, (paramId, value) => chain.SetParam(native, paramId, value));
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
            // The native master effects live on the session's mixer; disposing the session frees
            // them, so just drop the managed pairings.
            _rustMasterEffects.Clear();

            _rustSession?.Dispose();
            _rustSession = null;
            _rustOutputStream = null;

            _rustSuspendedEngine?.ResumeOutput();
            _rustSuspendedEngine = null;
        }
    }
}
