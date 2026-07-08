using System;
using Ownaudio.Audio.Tracks;
using OwnaudioNET.Core;
using OwnaudioNET.Synchronization;

namespace OwnaudioNET.Sources;

/// <summary>
/// Rust-native chain scaffold for <see cref="FileSource"/> (plan 14 / D.2.b).
/// </summary>
/// <remarks>
/// <para>
/// When the process opts into the Rust-native file-playback chain
/// (<see cref="Engine.RustNativeChain"/>), a <see cref="FileSource"/> is backed by an
/// <see cref="AudioTrack"/> in a <see cref="MultiTrackSession"/> instead of the managed
/// SoundTouch/decoder-thread path. This partial only establishes the mode flag and the
/// backend ownership model; the property/transport rebinding (Volume/Tempo/Pitch/Seek/…)
/// and the mixer facade are wired in the following sub-steps (D.2.c / D.2.d), so the legacy
/// behavior is unchanged until then.
/// </para>
/// <para>
/// Ownership follows the agreed design: a <b>standalone</b> source (played without an
/// <c>AudioMixer</c>) owns a private single-track session; a source that is added to a mixer
/// is instead <b>attached</b> to the mixer's shared session and does not own it.
/// </para>
/// </remarks>
public partial class FileSource : IRustNativeChainSource
{
    /// <inheritdoc/>
    AudioTrack? IRustNativeChainSource.RustTrack => RustTrack;

    /// <summary>
    /// Whether this source runs on the Rust-native chain. Assigned once in the constructor from
    /// <see cref="Engine.RustNativeChain.Enabled"/> so the mode is stable for the source's lifetime.
    /// </summary>
    private readonly bool _rustNative;

    /// <summary>
    /// Serializes creation, attachment and teardown of the Rust-native backend so the lazy
    /// initialization is race-free and the disposal ordering is deterministic.
    /// </summary>
    private readonly object _rustBackendLock = new();

    /// <summary>
    /// The private single-track session owned by this source in standalone mode, or
    /// <see langword="null"/> when the source is attached to a mixer's shared session (or the
    /// backend has not been created).
    /// </summary>
    private MultiTrackSession? _ownedRustSession;

    /// <summary>
    /// The native file source decoding into <see cref="_rustTrack"/>, when this source owns its
    /// backend. Owned by <see cref="_ownedRustSession"/> in standalone mode.
    /// </summary>
    private FileTrack? _rustFileTrack;

    /// <summary>
    /// The native track that renders this source in the Rust-native chain, or
    /// <see langword="null"/> before the backend is created.
    /// </summary>
    private AudioTrack? _rustTrack;

    /// <summary>
    /// <see langword="true"/> when <see cref="_rustTrack"/> belongs to a mixer's shared session
    /// (attached, not owned): this source must not dispose the session or the track.
    /// </summary>
    private bool _rustBackendAttached;

    /// <summary>
    /// Absolute content time (seconds) of the most recent seek target. The track's rendered
    /// position counts from zero after each seek, so the reported <see cref="Position"/> is this
    /// base plus the track's rendered time.
    /// </summary>
    private double _rustPositionBaseSeconds;

    /// <summary>
    /// Gets whether this source was constructed to use the Rust-native chain. Captured once at
    /// construction from <see cref="Engine.RustNativeChain.Enabled"/>, so toggling the switch
    /// afterwards does not change the mode of an existing source.
    /// </summary>
    internal bool IsRustNativeChain => _rustNative;

    /// <summary>
    /// Gets the path of the file this source plays, for the mixer facade to open a matching
    /// track in its shared session.
    /// </summary>
    internal string? FilePath => _filePath;

    /// <summary>
    /// Gets the native track backing this source in the Rust-native chain, or
    /// <see langword="null"/> when running legacy or before the backend is created.
    /// </summary>
    internal AudioTrack? RustTrack
    {
        get
        {
            lock (_rustBackendLock)
            {
                return _rustTrack;
            }
        }
    }

    /// <summary>
    /// Gets the native file source driving this source's Rust-native track, or
    /// <see langword="null"/> when running legacy, before the backend is created, or when attached
    /// without a file source.
    /// </summary>
    internal FileTrack? RustFileTrack
    {
        get
        {
            lock (_rustBackendLock)
            {
                return _rustFileTrack;
            }
        }
    }

    /// <summary>
    /// Lazily builds this source's standalone Rust-native backend: a private single-track
    /// <see cref="MultiTrackSession"/> whose track is fed from the source's file by a native
    /// <see cref="FileTrack"/>. Idempotent — repeated calls return the existing track.
    /// </summary>
    /// <returns>The native <see cref="AudioTrack"/> rendering this source.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the source is not in Rust-native mode, or when it is already attached to a
    /// mixer's shared session.
    /// </exception>
    /// <exception cref="ObjectDisposedException">Thrown when the source is disposed.</exception>
    internal AudioTrack EnsureStandaloneRustBackend()
    {
        if (!_rustNative)
        {
            throw new InvalidOperationException(
                "EnsureStandaloneRustBackend requires the Rust-native chain to be enabled for this source.");
        }

        ThrowIfDisposed();

        lock (_rustBackendLock)
        {
            if (_rustBackendAttached)
            {
                throw new InvalidOperationException(
                    "This source is attached to a mixer's shared session; it cannot own a standalone backend.");
            }

            if (_rustTrack is not null)
            {
                return _rustTrack;
            }

            string path = _filePath
                ?? throw new InvalidOperationException("The file source has no file path to stream.");

            var session = new MultiTrackSession(
                (float)_streamInfo.SampleRate,
                (ushort)_streamInfo.Channels);

            try
            {
                FileTrack fileTrack = session.AddFileTrack(path);
                _ownedRustSession = session;
                _rustFileTrack = fileTrack;
                _rustTrack = fileTrack.Track;
                _rustBackendAttached = false;
                ApplyControlStateToTrackLocked();
                return _rustTrack;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Attaches this source to a track that lives in a mixer's shared session (used by the
    /// <c>AudioMixer</c> facade in D.2.d). The source references but does not own the track or the
    /// session, so it will not dispose them.
    /// </summary>
    /// <param name="track">The mixer-session track that renders this source.</param>
    /// <param name="fileTrack">The native file source driving <paramref name="track"/>, if any.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="track"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the source is not in Rust-native mode, or already owns a standalone backend.
    /// </exception>
    internal void AttachRustTrack(AudioTrack track, FileTrack? fileTrack)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (!_rustNative)
        {
            throw new InvalidOperationException(
                "AttachRustTrack requires the Rust-native chain to be enabled for this source.");
        }

        lock (_rustBackendLock)
        {
            // If the source was played standalone before being added to a mixer (a valid
            // Play()-then-AddSource ordering), it lazily built a private single-track session.
            // Tear that transient backend down and re-home the source onto the mixer's shared
            // track instead of failing — leaving it standalone would orphan a session whose
            // track keeps running outside the mixer's transport control.
            if (_ownedRustSession is not null)
            {
                _ownedRustSession.Dispose();
                _ownedRustSession = null;
                _rustFileTrack = null;
                _rustTrack = null;
            }

            _rustTrack = track;
            _rustFileTrack = fileTrack;
            _rustBackendAttached = true;
            ApplyControlStateToTrackLocked();

            // Preserve the transport state captured before attachment: if the source was already
            // playing (Play() ran before AddSource), start the freshly attached track so it is
            // audible through the mixer rather than silently stopped.
            if (State == AudioState.Playing)
            {
                _rustTrack.Play();
            }
        }
    }

    /// <summary>
    /// Detaches this source from a mixer-owned track (the inverse of <see cref="AttachRustTrack"/>).
    /// The track and feeder are owned by the mixer's session, so they are only unreferenced here,
    /// not disposed. No-op when the source owns a standalone backend or has none.
    /// </summary>
    internal void DetachRustTrack()
    {
        lock (_rustBackendLock)
        {
            if (!_rustBackendAttached)
            {
                return;
            }

            _rustTrack = null;
            _rustFileTrack = null;
            _rustBackendAttached = false;
            _rustPositionBaseSeconds = 0.0;
        }
    }

    /// <summary>
    /// Pushes the source's current control state (gain, tempo, pitch, loop) onto the backing
    /// track and feeder. Called under <see cref="_rustBackendLock"/> right after the backend is
    /// created or attached so the track reflects any values set before the backend existed.
    /// </summary>
    private void ApplyControlStateToTrackLocked()
    {
        if (_rustTrack is null)
        {
            return;
        }

        _rustTrack.Gain = Volume;
        _rustTrack.Tempo = _tempo;
        _rustTrack.PitchSemitones = _pitchShift;

        if (_rustFileTrack is not null)
        {
            _rustFileTrack.Loop = Loop;
        }
    }

    /// <summary>
    /// Ensures a standalone backend exists for a source that is not attached to a mixer session,
    /// so transport calls have a track to drive. No-op when a backend already exists or the source
    /// is attached to a mixer's session.
    /// </summary>
    private void EnsureRustBackendForTransport()
    {
        lock (_rustBackendLock)
        {
            if (_rustTrack is not null || _rustBackendAttached)
            {
                return;
            }
        }

        EnsureStandaloneRustBackend();
    }

    /// <summary>
    /// Rust-native implementation of <see cref="Play"/>: ensures the backend, applies the current
    /// control state and starts the track. The managed decoder thread and SoundTouch are not used.
    /// </summary>
    private void RustNativePlay()
    {
        EnsureRustBackendForTransport();

        lock (_rustBackendLock)
        {
            ApplyControlStateToTrackLocked();
            _rustTrack?.Play();
        }

        base.Play();
    }

    /// <summary>
    /// Rust-native implementation of <see cref="Pause"/>: pauses the track and updates state.
    /// </summary>
    private void RustNativePause()
    {
        lock (_rustBackendLock)
        {
            _rustTrack?.Pause();
        }

        base.Pause();
    }

    /// <summary>
    /// Rust-native implementation of <see cref="Stop"/>: stops the track and updates state.
    /// </summary>
    private void RustNativeStop()
    {
        lock (_rustBackendLock)
        {
            _rustTrack?.Stop();
        }

        base.Stop();
    }

    /// <summary>
    /// Rust-native implementation of <see cref="Seek(double)"/>: repositions the native file
    /// source's decoder (clearing stale look-ahead) and resets the track's rendered-position
    /// counter, then records the absolute seek base so <see cref="Position"/> reports content time.
    /// </summary>
    /// <param name="positionInSeconds">Absolute target position in seconds.</param>
    /// <returns><see langword="true"/> when the seek was accepted.</returns>
    private bool RustNativeSeek(double positionInSeconds)
    {
        lock (_rustBackendLock)
        {
            _rustPositionBaseSeconds = positionInSeconds;
            _rustFileTrack?.Seek(TimeSpan.FromSeconds(positionInSeconds));
            _rustTrack?.Seek(TimeSpan.FromSeconds(positionInSeconds));
        }

        return true;
    }

    /// <summary>
    /// Gets the Rust-native playback position in seconds: the last seek base plus the track's
    /// rendered time. Returns the seek base (or zero) before the backend exists.
    /// </summary>
    private double RustNativePosition
    {
        get
        {
            lock (_rustBackendLock)
            {
                double rendered = _rustTrack?.Position.TotalSeconds ?? 0.0;
                return _rustPositionBaseSeconds + rendered;
            }
        }
    }

    /// <summary>
    /// Applies network-driven drift correction to the backing track in Rust-native mode (plan 14 /
    /// D.2.e). This is the Rust-native counterpart of the managed soft-sync: because the managed
    /// mix/read path (and its SoundTouch drift adjustment) does not run here, the correction is
    /// applied to <see cref="AudioTrack.Tempo"/> from the mixer's control-rate tick instead.
    /// </summary>
    /// <remarks>
    /// Runs only while the source is playing under a <b>network-controlled</b> master clock; local
    /// (non-network) playback is left to the native mixer's sample-locked clock. Uses the same
    /// three-zone thresholds as the managed path: green = no change, yellow = tempo nudged toward
    /// the master, red = hard seek to the master position.
    /// </remarks>
    internal void ApplyRustNativeSync()
    {
        if (!_rustNative || State != AudioState.Playing)
        {
            return;
        }

        MasterClock? clock = _masterClock;
        if (clock is null || !clock.IsNetworkControlled)
        {
            return;
        }

        AudioTrack? track;
        double actual;
        lock (_rustBackendLock)
        {
            track = _rustTrack;
            if (track is null)
            {
                return;
            }

            actual = _rustPositionBaseSeconds + track.Position.TotalSeconds;
        }

        double target = clock.CurrentTimestamp - _startOffset;
        if (target < 0.0)
        {
            return;
        }

        double signedDrift = target - actual;
        double drift = Math.Abs(signedDrift);

        if (drift <= SyncTolerance)
        {
            track.Tempo = _tempo;
            return;
        }

        if (drift <= SoftSyncTolerance)
        {
            double driftRange = SoftSyncTolerance - SyncTolerance;
            double adjustmentFactor = driftRange > 0.0
                ? Math.Min((drift - SyncTolerance) / driftRange, 1.0)
                : 1.0;
            float adjustment = (float)(adjustmentFactor * SoftSyncMaxTempoAdjustment);

            bool isBehind = signedDrift > 0.0;
            track.Tempo = isBehind ? _tempo + adjustment : _tempo - adjustment;
            return;
        }

        Seek(target);
        track.Tempo = _tempo;
    }

    /// <summary>
    /// Tears down the Rust-native backend. The owned standalone session (and, transitively, its
    /// feeder and track) is disposed; an attached track is left to its owning mixer session.
    /// </summary>
    private void DisposeRustBackend()
    {
        lock (_rustBackendLock)
        {
            if (!_rustBackendAttached)
            {
                _ownedRustSession?.Dispose();
            }

            _ownedRustSession = null;
            _rustFileTrack = null;
            _rustTrack = null;
        }
    }
}
