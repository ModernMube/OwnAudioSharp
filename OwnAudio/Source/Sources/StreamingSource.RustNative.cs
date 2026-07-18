using System;
using Ownaudio.Audio.Tracks;
using OwnaudioNET.Core;

namespace OwnaudioNET.Sources;

/// <summary>
/// Rust-native backend for <see cref="StreamingSource"/>: a native ring-buffer track owns
/// the audio feed, the managed pump fills it. A standalone source owns a private
/// single-track session; one added to a mixer is attached to the shared one.
/// </summary>
public sealed partial class StreamingSource : IRustNativeChainSource
{
    /// <summary>
    /// Runs on the rust-native chain? Latched once in the ctor, stable for life.
    /// </summary>
    private readonly bool _rustNative;

    /// <summary>
    /// Guards create/attach/teardown of the native backend.
    /// </summary>
    private readonly object _rustBackendLock = new();

    /// <summary>
    /// Owned single-track session in standalone mode, else null.
    /// </summary>
    private MultiTrackSession? _ownedRustSession;

    /// <summary>
    /// Native track carrying our feed, null before the backend exists.
    /// </summary>
    private AudioTrack? _rustTrack;

    /// <summary>
    /// True when the track belongs to a mixer's shared session.
    /// </summary>
    private bool _rustBackendAttached;

    /// <summary>
    /// Timeline position (sec) of the last seek target, the base Position counts from.
    /// </summary>
    private double _rustPositionBase;

    /// <summary>
    /// Was this source built for the rust-native chain?
    /// </summary>
    internal bool IsRustNativeChain => _rustNative;

    /// <inheritdoc/>
    AudioTrack? IRustNativeChainSource.RustTrack => RustTrack;

    /// <summary>
    /// The native track backing us, null before the backend is built.
    /// </summary>
    internal AudioTrack? RustTrack
    {
        get { lock (_rustBackendLock) return _rustTrack; }
    }

    /// <summary>
    /// Sample capacity of a track's feed ring. Every track is created with the same
    /// look-ahead window, so the pump can derive how much it has already queued.
    /// </summary>
    private int FeedCapacitySamples =>
        (int)(_config.SampleRate * Math.Max(1, _config.Channels) * AudioTrack.SourceBufferSeconds);

    /// <summary>
    /// Lazily builds the standalone backend: a private single-track session we feed. Idempotent.
    /// </summary>
    internal AudioTrack EnsureStandaloneRustBackend()
    {
        ThrowIfDisposed();

        lock (_rustBackendLock)
        {
            if (_rustBackendAttached)
                throw new InvalidOperationException("This source is attached to a mixer's shared session; it cannot own a standalone backend.");

            if (_rustTrack != null) return _rustTrack;

            var _session = new MultiTrackSession((float)_config.SampleRate, (ushort)Math.Max(1, _config.Channels));

            try
            {
                AudioTrack _track = _session.AddTrack();
                _ownedRustSession = _session;
                _rustTrack = _track;
                _rustBackendAttached = false;
                _applyControlState();
                return _rustTrack;
            }
            catch
            {
                _session.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Hooks us onto a track owned by a mixer's shared session. We only borrow it, so we
    /// never dispose it.
    /// </summary>
    /// <param name="track"></param>
    internal void AttachRustTrack(AudioTrack track)
    {
        lock (_rustBackendLock)
        {
            if (_ownedRustSession != null)
            {
                _ownedRustSession.Dispose();
                _ownedRustSession = null;
                _rustTrack = null;
            }

            _rustTrack = track;
            _rustBackendAttached = true;
            _applyControlState();

            if (State == AudioState.Playing) _rustTrack.Play();
        }
    }

    /// <summary>
    /// Detaches us from a mixer-owned track. The mixer owns it, so we just drop the ref.
    /// </summary>
    internal void DetachRustTrack()
    {
        lock (_rustBackendLock)
        {
            if (!_rustBackendAttached) return;

            _rustTrack = null;
            _rustBackendAttached = false;
            _rustPositionBase = 0.0;
        }
    }

    /// <summary>
    /// Pushes our control state onto the backing track. Call under the backend lock.
    /// </summary>
    private void _applyControlState()
    {
        if (_rustTrack == null) return;

        _rustTrack.Gain = Volume;
    }

    /// <summary>
    /// Rust-native Play, builds the standalone backend on demand.
    /// </summary>
    private void _rustPlay()
    {
        bool _needBackend;
        lock (_rustBackendLock) _needBackend = _rustTrack == null && !_rustBackendAttached;
        if (_needBackend) EnsureStandaloneRustBackend();

        lock (_rustBackendLock)
        {
            _applyControlState();
            _rustTrack?.Play();
        }

        base.Play();
    }

    /// <summary>
    /// Rust-native Pause.
    /// </summary>
    private void _rustPause()
    {
        lock (_rustBackendLock) _rustTrack?.Pause();

        base.Pause();
    }

    /// <summary>
    /// Rust-native Stop, rewinds the timeline base.
    /// </summary>
    private void _rustStop()
    {
        lock (_rustBackendLock)
        {
            _rustTrack?.Stop();
            _rebasePosition(0.0);
        }

        base.Stop();
    }

    /// <summary>
    /// Re-anchors the reported position after the pump re-armed the feed. A fresh ring
    /// source zeroes the track's frame counter, so the base is just the seek target -
    /// calling this before the reset would double-count.
    /// </summary>
    /// <param name="target"></param>
    private void _rustRebaseAfterFeedReset(double target)
    {
        lock (_rustBackendLock) _rustPositionBase = target;
    }

    /// <summary>
    /// Shifts the base so position reads target right now and keeps ticking with the
    /// track's own counter. Call under the backend lock.
    /// </summary>
    /// <param name="target"></param>
    private void _rebasePosition(double target)
    {
        _rustPositionBase = target - (_rustTrack?.Position.TotalSeconds ?? 0.0);
    }

    /// <summary>
    /// Rust-native position: last seek base plus the track's rendered time.
    /// </summary>
    private double _rustPosition
    {
        get
        {
            lock (_rustBackendLock)
                return _rustPositionBase + (_rustTrack?.Position.TotalSeconds ?? 0.0);
        }
    }

    /// <summary>
    /// Tears the backend down. An owned session is disposed; an attached track is left
    /// to its mixer.
    /// </summary>
    private void _disposeRustBackend()
    {
        lock (_rustBackendLock)
        {
            if (!_rustBackendAttached) _ownedRustSession?.Dispose();

            _ownedRustSession = null;
            _rustTrack = null;
        }
    }
}
