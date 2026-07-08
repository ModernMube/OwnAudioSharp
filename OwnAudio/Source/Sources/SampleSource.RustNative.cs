using System;
using Ownaudio.Audio.Tracks;
using OwnaudioNET.Core;

namespace OwnaudioNET.Sources;

/// <summary>
/// Rust-native chain backend for <see cref="SampleSource"/>: the in-memory sample buffer is served
/// entirely by a native <see cref="MemoryTrack"/>, so the managed side is only a controller and no
/// audio data flows through managed code (the GC never touches the render path).
/// </summary>
/// <remarks>
/// <para>
/// This mirrors <see cref="FileSource"/>'s Rust-native backend, with a native <see cref="MemoryTrack"/>
/// (an owned interleaved buffer) in place of the file decoder. A <b>standalone</b> source owns a
/// private single-track session; a source added to an <c>AudioMixer</c> is <b>attached</b> to the
/// mixer's shared session and does not own it.
/// </para>
/// </remarks>
public sealed partial class SampleSource : IRustNativeChainSource
{
    /// <summary>
    /// Whether this source runs on the Rust-native chain. Assigned once in the constructor from
    /// <see cref="Engine.RustNativeChain.Enabled"/> so the mode is stable for the source's lifetime.
    /// </summary>
    private readonly bool _rustNative;

    /// <summary>Serializes creation, attachment and teardown of the Rust-native backend.</summary>
    private readonly object _rustBackendLock = new();

    /// <summary>The private single-track session owned in standalone mode, else null.</summary>
    private MultiTrackSession? _ownedRustSession;

    /// <summary>The native memory source serving this source, when the backend is owned.</summary>
    private MemoryTrack? _rustMemoryTrack;

    /// <summary>The native track rendering this source, or null before the backend is created.</summary>
    private AudioTrack? _rustTrack;

    /// <summary><see langword="true"/> when the track belongs to a mixer's shared session.</summary>
    private bool _rustBackendAttached;

    /// <summary>Absolute content time (seconds) of the most recent seek target.</summary>
    private double _rustPositionBaseSeconds;

    /// <summary>Gets whether this source was constructed to use the Rust-native chain.</summary>
    internal bool IsRustNativeChain => _rustNative;

    /// <inheritdoc/>
    AudioTrack? IRustNativeChainSource.RustTrack => RustTrack;

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
    /// Gets the native memory source driving this source's Rust-native track, or
    /// <see langword="null"/> when running legacy or before the backend is created.
    /// </summary>
    internal MemoryTrack? RustMemoryTrack
    {
        get
        {
            lock (_rustBackendLock)
            {
                return _rustMemoryTrack;
            }
        }
    }

    /// <summary>
    /// Returns a snapshot of the current interleaved sample buffer for the mixer to open a matching
    /// native memory track in its shared session. Read under the data lock so it never races a
    /// concurrent <see cref="SubmitSamples"/>.
    /// </summary>
    internal float[] GetRustSampleSnapshot()
    {
        lock (_dataLock)
        {
            return _sampleData ?? Array.Empty<float>();
        }
    }

    /// <summary>
    /// Lazily builds this source's standalone Rust-native backend: a private single-track session
    /// whose track is served by a native memory source over this source's buffer. Idempotent.
    /// </summary>
    /// <returns>The native <see cref="AudioTrack"/> rendering this source.</returns>
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

            var session = new MultiTrackSession(
                (float)_config.SampleRate,
                (ushort)_config.Channels);

            try
            {
                MemoryTrack memoryTrack = session.AddMemoryTrack(GetRustSampleSnapshot(), Loop);
                _ownedRustSession = session;
                _rustMemoryTrack = memoryTrack;
                _rustTrack = memoryTrack.Track;
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
    /// Attaches this source to a track that lives in a mixer's shared session. The source references
    /// but does not own the track or the memory source, so it will not dispose them.
    /// </summary>
    /// <param name="track">The mixer-session track that renders this source.</param>
    /// <param name="memoryTrack">The native memory source driving <paramref name="track"/>.</param>
    internal void AttachRustTrack(AudioTrack track, MemoryTrack memoryTrack)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (!_rustNative)
        {
            throw new InvalidOperationException(
                "AttachRustTrack requires the Rust-native chain to be enabled for this source.");
        }

        lock (_rustBackendLock)
        {
            // If the source was played standalone before being added to a mixer, tear that transient
            // backend down and re-home onto the mixer's shared track.
            if (_ownedRustSession is not null)
            {
                _ownedRustSession.Dispose();
                _ownedRustSession = null;
                _rustMemoryTrack = null;
                _rustTrack = null;
            }

            _rustTrack = track;
            _rustMemoryTrack = memoryTrack;
            _rustBackendAttached = true;
            ApplyControlStateToTrackLocked();

            // Preserve the transport state captured before attachment.
            if (State == AudioState.Playing)
            {
                _rustTrack.Play();
            }
        }
    }

    /// <summary>
    /// Detaches this source from a mixer-owned track. The track and memory source are owned by the
    /// mixer's session, so they are only unreferenced here, not disposed.
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
            _rustMemoryTrack = null;
            _rustBackendAttached = false;
            _rustPositionBaseSeconds = 0.0;
        }
    }

    /// <summary>
    /// Pushes the source's current control state (gain, loop) onto the backing track and memory
    /// source. Called under <see cref="_rustBackendLock"/> right after the backend is created/attached.
    /// </summary>
    private void ApplyControlStateToTrackLocked()
    {
        if (_rustTrack is null)
        {
            return;
        }

        _rustTrack.Gain = Volume;

        if (_rustMemoryTrack is not null)
        {
            _rustMemoryTrack.Loop = Loop;
        }
    }

    /// <summary>
    /// Ensures a standalone backend exists for a source not attached to a mixer session, so transport
    /// calls have a track to drive. No-op when a backend already exists or the source is attached.
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

    /// <summary>Rust-native implementation of <see cref="Play"/>.</summary>
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

    /// <summary>Rust-native implementation of <see cref="Pause"/>.</summary>
    private void RustNativePause()
    {
        lock (_rustBackendLock)
        {
            _rustTrack?.Pause();
        }

        base.Pause();
    }

    /// <summary>Rust-native implementation of <see cref="Stop"/>.</summary>
    private void RustNativeStop()
    {
        lock (_rustBackendLock)
        {
            _rustTrack?.Stop();
            _rustMemoryTrack?.Seek(TimeSpan.Zero);
            _rustPositionBaseSeconds = 0.0;
        }

        base.Stop();
    }

    /// <summary>
    /// Rust-native implementation of <see cref="Seek(double)"/>: repositions the native memory source
    /// and resets the track's rendered-position counter, recording the seek base so
    /// <see cref="Position"/> reports content time.
    /// </summary>
    private bool RustNativeSeek(double positionInSeconds)
    {
        if (positionInSeconds < 0 || positionInSeconds > Duration)
        {
            return false;
        }

        lock (_rustBackendLock)
        {
            _rustPositionBaseSeconds = positionInSeconds;
            _rustMemoryTrack?.Seek(TimeSpan.FromSeconds(positionInSeconds));
            _rustTrack?.Seek(TimeSpan.FromSeconds(positionInSeconds));
        }

        return true;
    }

    /// <summary>
    /// Gets the Rust-native playback position in seconds: the last seek base plus the track's rendered
    /// time. Returns the seek base (or zero) before the backend exists.
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
    /// Replaces the native memory source's buffer with <paramref name="samples"/> in Rust-native mode,
    /// so a dynamic <see cref="SubmitSamples"/> takes effect natively without a managed audio path.
    /// No-op when no backend exists yet (the next backend build picks up the new buffer).
    /// </summary>
    private void RustNativeSubmit(ReadOnlySpan<float> samples)
    {
        lock (_rustBackendLock)
        {
            _rustMemoryTrack?.Reload(samples);
            _rustPositionBaseSeconds = 0.0;
        }
    }

    /// <summary>
    /// Tears down the Rust-native backend. The owned standalone session is disposed; an attached track
    /// is left to its owning mixer session.
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
            _rustMemoryTrack = null;
            _rustTrack = null;
        }
    }
}
