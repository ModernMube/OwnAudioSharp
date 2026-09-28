using Logger;
using Ownaudio.Audio.Tracks;
using OwnaudioNET.Core;
using OwnaudioNET.Synchronization;

namespace OwnaudioNET.Sources;

/// <summary>
/// Rust-native backend for GroupSource. A standalone source owns a private single track session,
/// a mixer-added source is attached to the mixer's shared session. Either way the clips are
/// placed on whichever native group is current; a new one gets them all again.
/// </summary>
public sealed partial class GroupSource : IRustNativeChainSource
{
    /// <inheritdoc/>
    AudioTrack? IRustNativeChainSource.RustTrack => RustTrack;

    /// <inheritdoc/>
    void IRustNativeChainSource.DetachRustTrack() => DetachRustTrack();

    /// <inheritdoc/>
    AudioTrack? IRustClockedSource.RustTrack => RustTrack;

    /// <inheritdoc/>
    double IRustClockedSource.RustNativeRealPosition => RustNativeRealPosition;

    /// <inheritdoc/>
    void IRustClockedSource.ApplyRustNativeSync() => ApplyRustNativeSync();

    private readonly bool _rustNative;
    private readonly object _rustBackendLock = new object();

    private MultiTrackSession? _ownedRustSession;
    private GroupTrack? _rustGroupTrack;
    private AudioTrack? _rustTrack;
    private bool _rustBackendAttached;

    /// <summary>
    /// Content time of the last seek, the track position restarts at zero after one.
    /// </summary>
    private double _rustPositionBaseSeconds;

    /// <summary>
    /// Project time of the last seek, the content base divided by tempo.
    /// </summary>
    private double _rustProjectBaseSeconds;

    /// <summary>
    /// Native track or null.
    /// </summary>
    internal AudioTrack? RustTrack
    {
        get { lock (_rustBackendLock) { return _rustTrack; } }
    }

    /// <summary>
    /// Native group or null.
    /// </summary>
    internal GroupTrack? RustGroupTrack
    {
        get { lock (_rustBackendLock) { return _rustGroupTrack; } }
    }

    /// <summary>
    /// Lazily builds the standalone backend, repeated calls return the existing track.
    /// </summary>
    internal AudioTrack EnsureStandaloneRustBackend()
    {
        if (!_rustNative) throw new InvalidOperationException("Rust-native chain is not enabled for this source.");

        ThrowIfDisposed();

        lock (_clipLock)
        lock (_rustBackendLock)
        {
            if (_rustBackendAttached)
                throw new InvalidOperationException("Attached to a mixer session, cannot own a standalone backend.");

            if (_rustTrack is not null) return _rustTrack;

            var _session = new MultiTrackSession(_sampleRate, (ushort)_channels);

            try
            {
                GroupTrack _groupTrack = _session.AddGroupTrack((ushort)_channels);
                _ownedRustSession = _session;
                _bindGroupTrack(_groupTrack);
                _rustBackendAttached = false;

                Log.Info($"[GroupSource] Standalone native backend up: {_sampleRate}Hz {_channels}ch, {_clips.Count} clips");
                return _rustTrack!;
            }
            catch (Exception ex)
            {
                Log.Error("[GroupSource] Native backend failed to come up", ex);
                _session.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Attaches this source to a group living in a mixer's shared session, which we reference
    /// but never dispose. Every clip goes onto it.
    /// </summary>
    internal void AttachRustTrack(GroupTrack groupTrack)
    {
        ArgumentNullException.ThrowIfNull(groupTrack);

        lock (_clipLock)
        lock (_rustBackendLock)
        {
            //We drop the private session if Play() ran before AddSource, the mixer track takes over
            if (_ownedRustSession is not null)
            {
                _unbindGroupTrack();
                _ownedRustSession.Dispose();
                _ownedRustSession = null;
            }

            _bindGroupTrack(groupTrack);
            _rustBackendAttached = true;

            //We keep it audible if the source was already playing before attach
            if (State == AudioState.Playing) _rustTrack!.Play();
        }
    }

    /// <summary>
    /// Inverse of AttachRustTrack, the mixer session keeps the track so we only unref it.
    /// </summary>
    internal void DetachRustTrack()
    {
        lock (_clipLock)
        lock (_rustBackendLock)
        {
            if (!_rustBackendAttached) return;

            _unbindGroupTrack();
            _rustBackendAttached = false;
            _rustPositionBaseSeconds = 0.0;
            _rustProjectBaseSeconds = 0.0;
        }
    }

    /// <summary>
    /// Makes groupTrack current and places every clip on it. Call under both locks.
    /// </summary>
    private void _bindGroupTrack(GroupTrack groupTrack)
    {
        _rustGroupTrack = groupTrack;
        _rustTrack = groupTrack.Track;
        _rustGroupTrack.Completed += _onRustGroupCompleted;
        _applyControlStateToTrack();

        foreach (SourceClip clip in _clips) _placeOnTrack(clip);
    }

    /// <summary>
    /// Forgets the current group; the placements die with it. Call under both locks.
    /// </summary>
    private void _unbindGroupTrack()
    {
        if (_rustGroupTrack is not null) _rustGroupTrack.Completed -= _onRustGroupCompleted;

        foreach (SourceClip clip in _clips) clip.NativeId = null;

        _rustGroupTrack = null;
        _rustTrack = null;
    }

    /// <summary>
    /// The cursor ran past the last clip. Fires on a timer thread.
    /// </summary>
    private void _onRustGroupCompleted(object? sender, TrackFeedCompletedEventArgs e)
    {
        if (_disposed) return;

        _isEndOfStream = true;
        State = AudioState.EndOfStream;
    }

    /// <summary>
    /// Pushes gain, pan, tempo and pitch onto the backing track. Call under _rustBackendLock.
    /// </summary>
    private void _applyControlStateToTrack()
    {
        if (_rustTrack is null) return;

        _rustTrack.Gain = Volume;
        _rustTrack.Pan = Pan;
        //Stretch stage pinned on so the first tempo change lands on a warm FIFO, no click
        _rustTrack.SetStretchAlwaysOn(true);
        _rustTrack.Tempo = _nativeTempo;
        _rustTrack.PitchSemitones = _pitchShift;
    }

    private void _rustNativePlay()
    {
        bool _needBackend;
        lock (_rustBackendLock) { _needBackend = _rustTrack is null && !_rustBackendAttached; }

        if (_needBackend && _rustNative) EnsureStandaloneRustBackend();

        _isEndOfStream = false;

        lock (_rustBackendLock)
        {
            _applyControlStateToTrack();
            _rustTrack?.Play();
        }

        base.Play();
    }

    /// <summary>
    /// Seeks the group and its track, records the content and project bases so Position and the
    /// master clock stay coherent at any tempo. Content time in.
    /// </summary>
    private bool _rustNativeSeek(double positionInSeconds)
    {
        lock (_rustBackendLock)
        {
            _rustPositionBaseSeconds = positionInSeconds;
            float _tempoNow = _tempo <= 0f ? 1f : _tempo;
            _rustProjectBaseSeconds = positionInSeconds / _tempoNow;
            _rustGroupTrack?.Seek(TimeSpan.FromSeconds(positionInSeconds));
            _rustTrack?.Seek(TimeSpan.FromSeconds(positionInSeconds));
        }

        return true;
    }

    /// <summary>
    /// Content position, seek base plus the track's tempo aware content time.
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
    /// Project (wall clock) position, what drives the shared MasterClock.
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
    /// Our tempo times the clock's network trim, what the native track runs at.
    /// </summary>
    private float _nativeTempo => _tempo * (_masterClock?.TempoTrim ?? 1f);

    /// <summary>
    /// Same as on a file source: puts the network follower's trim onto the native track,
    /// from the mixer's control tick.
    /// </summary>
    internal void ApplyRustNativeSync()
    {
        if (!_rustNative) return;

        lock (_rustBackendLock)
        {
            if (_rustTrack is not null) _rustTrack.Tempo = _nativeTempo;
        }
    }

    /// <summary>
    /// Tears down the backend, only the owned standalone session is disposed.
    /// </summary>
    private void _disposeRustBackend()
    {
        lock (_clipLock)
        lock (_rustBackendLock)
        {
            _unbindGroupTrack();

            if (!_rustBackendAttached) _ownedRustSession?.Dispose();

            _ownedRustSession = null;
        }
    }
}
