using System;
using System.IO;
using Ownaudio.Audio.Tracks;
using OwnaudioNET.Core;
using OwnaudioNET.Synchronization;

namespace OwnaudioNET.Sources;

/// <summary>
/// Rust-native backend for FileSource. A standalone source owns a private single
/// track session, a mixer-added source is attached to the mixer's shared session.
/// </summary>
public partial class FileSource : IRustNativeChainSource
{
    /// <inheritdoc/>
    AudioTrack? IRustNativeChainSource.RustTrack => RustTrack;

    /// <summary>
    /// Rust-native mode flag, fixed at construction.
    /// </summary>
    private readonly bool _rustNative;

    /// <summary>
    /// Guards backend create/attach/teardown.
    /// </summary>
    private readonly object _rustBackendLock = new object();

    /// <summary>
    /// Private session in standalone mode, null when attached to a mixer.
    /// </summary>
    private MultiTrackSession? _ownedRustSession;

    /// <summary>
    /// Native file source feeding _rustTrack when we own the backend.
    /// </summary>
    private FileTrack? _rustFileTrack;

    /// <summary>
    /// Native track rendering this source, null before the backend exists.
    /// </summary>
    private AudioTrack? _rustTrack;

    /// <summary>
    /// True when the track belongs to a mixer session, we must not dispose it.
    /// </summary>
    private bool _rustBackendAttached;

    /// <summary>
    /// Content time (sec) of the last seek, the track position restarts at zero after a seek.
    /// </summary>
    private double _rustPositionBaseSeconds;

    /// <summary>
    /// Project (wall clock) time of the last seek, content base divided by tempo.
    /// Keeps the two timelines coherent across seeks at non-unity tempo.
    /// </summary>
    private double _rustProjectBaseSeconds;

    /// <summary>
    /// Rust-native mode, captured once at construction.
    /// </summary>
    internal bool IsRustNativeChain => _rustNative;

    /// <summary>
    /// File path for the mixer facade.
    /// </summary>
    internal string? FilePath => _filePath;

    /// <summary>
    /// Native track or null.
    /// </summary>
    internal AudioTrack? RustTrack
    {
        get { lock (_rustBackendLock) { return _rustTrack; } }
    }

    /// <summary>
    /// Native file source or null.
    /// </summary>
    internal FileTrack? RustFileTrack
    {
        get { lock (_rustBackendLock) { return _rustFileTrack; } }
    }

    /// <summary>
    /// Lazily builds the standalone backend, repeated calls return the existing track.
    /// </summary>
    /// <returns></returns>
    internal AudioTrack EnsureStandaloneRustBackend()
    {
        if (!_rustNative) throw new InvalidOperationException("Rust-native chain is not enabled for this source.");

        ThrowIfDisposed();

        lock (_rustBackendLock)
        {
            if (_rustBackendAttached)
                throw new InvalidOperationException("Attached to a mixer session, cannot own a standalone backend.");

            if (_rustTrack is not null) return _rustTrack;

            string _path = _filePath
                ?? throw new InvalidOperationException("The file source has no file path to stream.");

            var _session = new MultiTrackSession(
                (float)_streamInfo.SampleRate,
                (ushort)_streamInfo.Channels);

            try
            {
                FileTrack _fileTrack = _session.AddFileTrack(_path);
                _ownedRustSession = _session;
                _rustFileTrack = _fileTrack;
                _rustTrack = _fileTrack.Track;
                _rustBackendAttached = false;
                _applyControlStateToTrack();
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
    /// Attaches this source to a track living in a mixer's shared session, the source
    /// references but never disposes it.
    /// </summary>
    /// <param name="track"></param>
    /// <param name="fileTrack"></param>
    internal void AttachRustTrack(AudioTrack track, FileTrack? fileTrack)
    {
        ArgumentNullException.ThrowIfNull(track);

        lock (_rustBackendLock)
        {
            //We drop the private session if Play() ran before AddSource, the mixer track takes over
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
            _applyControlStateToTrack();

            //We keep it audible if the source was already playing before attach
            if (State == AudioState.Playing) _rustTrack.Play();
        }
    }

    /// <summary>
    /// Inverse of AttachRustTrack, the mixer session keeps the track so we only unref it.
    /// </summary>
    internal void DetachRustTrack()
    {
        lock (_rustBackendLock)
        {
            if (!_rustBackendAttached) return;

            _rustTrack = null;
            _rustFileTrack = null;
            _rustBackendAttached = false;
            _rustPositionBaseSeconds = 0.0;
            _rustProjectBaseSeconds = 0.0;
        }
    }

    /// <summary>
    /// Pushes gain, tempo, pitch and loop onto the backing track. Call under _rustBackendLock.
    /// </summary>
    private void _applyControlStateToTrack()
    {
        if (_rustTrack is null) return;

        _rustTrack.Gain = Volume;
        //Stretch stage pinned on so the first tempo change lands on a warm FIFO, no click
        _rustTrack.SetStretchAlwaysOn(true);
        _rustTrack.Tempo = _tempo;
        _rustTrack.PitchSemitones = _pitchShift;

        if (_rustFileTrack is not null) _rustFileTrack.Loop = Loop;
    }

    /// <summary>
    /// Builds a standalone backend for transport calls when none exists. Stream/decoder
    /// sources have no real file so nothing is built for them.
    /// </summary>
    private void _ensureRustBackendForTransport()
    {
        lock (_rustBackendLock)
        {
            if (_rustTrack is not null || _rustBackendAttached) return;
        }

        if(!string.IsNullOrEmpty(_filePath) && File.Exists(_filePath)) EnsureStandaloneRustBackend();
    }

    /// <summary>
    /// Native Play, makes sure a backend exists then starts the track.
    /// </summary>
    private void _rustNativePlay()
    {
        _ensureRustBackendForTransport();

        lock (_rustBackendLock)
        {
            _applyControlStateToTrack();
            _rustTrack?.Play();
        }

        base.Play();
    }

    /// <summary>
    /// Native Pause.
    /// </summary>
    private void _rustNativePause()
    {
        lock (_rustBackendLock) { _rustTrack?.Pause(); }
        base.Pause();
    }

    /// <summary>
    /// Native Stop.
    /// </summary>
    private void _rustNativeStop()
    {
        lock (_rustBackendLock) { _rustTrack?.Stop(); }
        base.Stop();
    }

    /// <summary>
    /// Seeks the native decoder and track, records the content and project bases so
    /// Position and the master clock stay coherent at any tempo. Content time in.
    /// </summary>
    /// <param name="positionInSeconds"></param>
    /// <returns></returns>
    private bool _rustNativeSeek(double positionInSeconds)
    {
        lock (_rustBackendLock)
        {
            _rustPositionBaseSeconds = positionInSeconds;
            float _tempoNow = _tempo <= 0f ? 1f : _tempo;
            _rustProjectBaseSeconds = positionInSeconds / _tempoNow;
            _rustFileTrack?.Seek(TimeSpan.FromSeconds(positionInSeconds));
            _rustTrack?.Seek(TimeSpan.FromSeconds(positionInSeconds));
        }

        return true;
    }

    /// <summary>
    /// Content position in seconds, seek base plus the track's tempo aware content time.
    /// </summary>
    private double _rustNativePosition
    {
        get
        {
            lock (_rustBackendLock)
            {
                return _rustPositionBaseSeconds + (_rustTrack?.ContentPosition.TotalSeconds ?? 0.0);
            }
        }
    }

    /// <summary>
    /// Project (wall clock) position, this feeds the shared MasterClock so a stretched
    /// track does not run the clock at content rate.
    /// </summary>
    internal double RustNativeRealPosition
    {
        get
        {
            lock (_rustBackendLock)
            {
                return _rustProjectBaseSeconds + (_rustTrack?.Position.TotalSeconds ?? 0.0);
            }
        }
    }

    /// <summary>
    /// Network driven drift correction on the native track, called from the mixer's
    /// control tick. Green = leave it, yellow = tempo nudge, red = hard seek.
    /// </summary>
    internal void ApplyRustNativeSync()
    {
        if (!_rustNative || State != AudioState.Playing) return;

        MasterClock? _clock = _masterClock;
        if (_clock is null || !_clock.IsNetworkControlled) return;

        AudioTrack? _track;
        double _actual;
        lock (_rustBackendLock)
        {
            _track = _rustTrack;
            if (_track is null) return;

            _actual = _rustProjectBaseSeconds + _track.Position.TotalSeconds;
        }

        double _target = _clock.CurrentTimestamp - _startOffset;
        if (_target < 0.0) return;

        double _signedDrift = _target - _actual;
        double _drift = Math.Abs(_signedDrift);

        if (_drift <= SyncTolerance)
        {
            _track.Tempo = _tempo;
            return;
        }

        if (_drift <= SoftSyncTolerance)
        {
            double _range = SoftSyncTolerance - SyncTolerance;
            double _factor = _range > 0.0 ? Math.Min((_drift - SyncTolerance) / _range, 1.0) : 1.0;
            float _adjustment = (float)(_factor * SoftSyncMaxTempoAdjustment);

            _track.Tempo = _signedDrift > 0.0 ? _tempo + _adjustment : _tempo - _adjustment;
            return;
        }

        //Red zone, the project target is converted to content time via the tempo
        float _t = _tempo <= 0f ? 1f : _tempo;
        Seek(_target * _t);
        _track.Tempo = _tempo;
    }

    /// <summary>
    /// Tears down the backend, only the owned standalone session is disposed.
    /// </summary>
    private void _disposeRustBackend()
    {
        lock (_rustBackendLock)
        {
            if (!_rustBackendAttached) _ownedRustSession?.Dispose();

            _ownedRustSession = null;
            _rustFileTrack = null;
            _rustTrack = null;
        }
    }
}
