using System;
using Ownaudio.Audio.Tracks;
using OwnaudioNET.Core;

namespace OwnaudioNET.Sources;

/// <summary>
/// Rust-native backend for SampleSource: a native MemoryTrack owns the buffer, managed side just drives it.
/// Standalone source owns a private single-track session; one added to a mixer is attached to the shared one.
/// </summary>
public sealed partial class SampleSource : IRustNativeChainSource
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
    /// Native memory source feeding us, when the backend is owned.
    /// </summary>
    private MemoryTrack? _rustMemoryTrack;

    /// <summary>
    /// Native track rendering us, null before the backend exists.
    /// </summary>
    private AudioTrack? _rustTrack;

    /// <summary>
    /// True when the track belongs to a mixer's shared session.
    /// </summary>
    private bool _rustBackendAttached;

    /// <summary>
    /// Content time (sec) of the last seek target.
    /// </summary>
    private double _rustPositionBase;

    /// <summary>
    /// Was this source built for the rust-native chain?
    /// </summary>
    internal bool IsRustNativeChain => _rustNative;

    /// <inheritdoc/>
    AudioTrack? IRustNativeChainSource.RustTrack => RustTrack;

    /// <summary>
    /// The native track backing us, null on legacy or before build.
    /// </summary>
    internal AudioTrack? RustTrack
    {
        get { lock (_rustBackendLock) return _rustTrack; }
    }

    /// <summary>
    /// The native memory source driving our track, null on legacy or before build.
    /// </summary>
    internal MemoryTrack? RustMemoryTrack
    {
        get { lock (_rustBackendLock) return _rustMemoryTrack; }
    }

    /// <summary>
    /// Snapshot of the current buffer so the mixer can open a matching native track.
    /// Under the data lock so it never races SubmitSamples.
    /// </summary>
    /// <returns></returns>
    internal float[] GetRustSampleSnapshot()
    {
        lock (_dataLock) return _sampleData ?? Array.Empty<float>();
    }

    /// <summary>
    /// Lazily builds the standalone backend: a private single-track session over our buffer. Idempotent.
    /// </summary>
    /// <returns></returns>
    internal AudioTrack EnsureStandaloneRustBackend()
    {
        ThrowIfDisposed();

        lock (_rustBackendLock)
        {
            if (_rustBackendAttached)
                throw new InvalidOperationException("This source is attached to a mixer's shared session; it cannot own a standalone backend.");

            if (_rustTrack != null) return _rustTrack;

            var _session = new MultiTrackSession((float)_config.SampleRate, (ushort)_config.Channels);

            try
            {
                MemoryTrack _memoryTrack = _session.AddMemoryTrack(GetRustSampleSnapshot(), Loop);
                _ownedRustSession = _session;
                _rustMemoryTrack = _memoryTrack;
                _rustTrack = _memoryTrack.Track;
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
    /// Attaches us to a track living in a mixer's shared session. We reference but don't own it, so we won't dispose it.
    /// </summary>
    /// <param name="track"></param>
    /// <param name="memoryTrack"></param>
    internal void AttachRustTrack(AudioTrack track, MemoryTrack memoryTrack)
    {
        lock (_rustBackendLock)
        {
            //Played standalone before being added to a mixer? Kill that transient backend, re-home onto the shared track.
            if (_ownedRustSession != null)
            {
                _ownedRustSession.Dispose();
                _ownedRustSession = null;
                _rustMemoryTrack = null;
                _rustTrack = null;
            }

            _rustTrack = track;
            _rustMemoryTrack = memoryTrack;
            _rustBackendAttached = true;
            _applyControlState();

            if (State == AudioState.Playing) _rustTrack.Play();
        }
    }

    /// <summary>
    /// Detaches us from a mixer-owned track. The mixer owns it, so we just drop the refs, never dispose.
    /// </summary>
    internal void DetachRustTrack()
    {
        lock (_rustBackendLock)
        {
            if(!_rustBackendAttached) return;

            _rustTrack = null;
            _rustMemoryTrack = null;
            _rustBackendAttached = false;
            _rustPositionBase = 0.0;
        }
    }

    /// <summary>
    /// Pushes our control state (gain, loop) onto the backing track. Call under the backend lock.
    /// </summary>
    private void _applyControlState()
    {
        if (_rustTrack == null) return;

        _rustTrack.Gain = Volume;
        if (_rustMemoryTrack != null) _rustMemoryTrack.Loop = Loop;
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
    /// Rust-native Stop, rewinds the memory source.
    /// </summary>
    private void _rustStop()
    {
        lock (_rustBackendLock)
        {
            _rustTrack?.Stop();
            _rustMemoryTrack?.Seek(TimeSpan.Zero);
            _rustPositionBase = 0.0;
        }

        base.Stop();
    }

    /// <summary>
    /// Rust-native Seek: repositions the memory source and records the base so Position reads content time.
    /// </summary>
    /// <param name="positionInSeconds"></param>
    /// <returns></returns>
    private bool _rustSeek(double positionInSeconds)
    {
        lock (_rustBackendLock)
        {
            _rustPositionBase = positionInSeconds;
            _rustMemoryTrack?.Seek(TimeSpan.FromSeconds(positionInSeconds));
            _rustTrack?.Seek(TimeSpan.FromSeconds(positionInSeconds));
        }

        return true;
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
    /// Swaps the native memory source's buffer so a dynamic SubmitSamples lands natively.
    /// No-op before a backend exists (next build picks up the new buffer).
    /// </summary>
    /// <param name="samples"></param>
    private void _rustSubmit(ReadOnlySpan<float> samples)
    {
        lock (_rustBackendLock)
        {
            _rustMemoryTrack?.Reload(samples);
            _rustPositionBase = 0.0;
        }
    }

    /// <summary>
    /// Tears the backend down. Owned session gets disposed; an attached track is left to its mixer.
    /// </summary>
    private void _disposeRustBackend()
    {
        lock (_rustBackendLock)
        {
            if (!_rustBackendAttached) _ownedRustSession?.Dispose();

            _ownedRustSession = null;
            _rustMemoryTrack = null;
            _rustTrack = null;
        }
    }
}
