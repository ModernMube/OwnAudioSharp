using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Exceptions;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Tracks;

/// <summary>
/// Manages a collection of synchronized <see cref="AudioTrack"/> instances sharing a
/// single sample-accurate transport clock.
/// </summary>
/// <remarks>
/// <para>
/// The session owns a native <c>MultiTrackMixer</c> and a set of tracks.  Calling
/// <see cref="PlayAll"/> starts all tracks against the shared clock in a single
/// native operation, guaranteeing perfect synchronization.
/// </para>
/// <para>
/// Dispose the session to release all tracks and the native mixer.  All
/// <see cref="AudioTrack"/> instances obtained from this session are invalidated
/// after the session is disposed.
/// </para>
/// </remarks>
public sealed class MultiTrackSession : IDisposable
{
    #region Fields

    private readonly MixerHandle _mixerHandle;
    private readonly float _sampleRate;
    private readonly List<AudioTrack> _tracks = new();
    private bool _disposed;

    #endregion

    #region Construction

    /// <summary>
    /// Creates a new multi-track session.
    /// </summary>
    /// <param name="sampleRate">Output sample rate in Hz.</param>
    /// <param name="channels">Number of output channels (1 = mono, 2 = stereo).</param>
    /// <exception cref="Ownaudio.Safe.Exceptions.OwnAudioException">
    /// Thrown when the native mixer cannot be created.
    /// </exception>
    public MultiTrackSession(float sampleRate, ushort channels)
    {
        _sampleRate = sampleRate;

        int code = OwnAudioNative.ownaudio_v1_mixer_create(sampleRate, channels, out IntPtr rawMixer);
        ErrorCodeMapper.ThrowIfError(code, nameof(MultiTrackSession));

        _mixerHandle = new MixerHandle();
        Marshal.InitHandle(_mixerHandle, rawMixer);
    }

    #endregion

    #region Properties

    /// <summary>
    /// Gets the read-only list of tracks registered in this session.
    /// </summary>
    public IReadOnlyList<AudioTrack> Tracks => _tracks.AsReadOnly();

    #endregion

    #region Track management

    /// <summary>
    /// Adds a new track to the session.
    /// </summary>
    /// <returns>The newly created <see cref="AudioTrack"/>.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the session is disposed.</exception>
    /// <exception cref="Ownaudio.Safe.Exceptions.OwnAudioException">
    /// Thrown when the native track cannot be created.
    /// </exception>
    public AudioTrack AddTrack()
    {
        ThrowIfDisposed();

        int code = OwnAudioNative.ownaudio_v1_track_create(
            _mixerHandle.DangerousGetHandle(),
            out IntPtr rawTrack);

        ErrorCodeMapper.ThrowIfError(code, nameof(AddTrack));

        var handle = new TrackHandle();
        Marshal.InitHandle(handle, rawTrack);

        var track = new AudioTrack(handle, _mixerHandle.DangerousGetHandle(), _sampleRate);
        _tracks.Add(track);
        return track;
    }

    /// <summary>
    /// Removes and disposes the specified track.
    /// </summary>
    /// <param name="track">Track to remove.</param>
    /// <exception cref="ObjectDisposedException">Thrown when the session is disposed.</exception>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="track"/> is <see langword="null"/>.
    /// </exception>
    public void RemoveTrack(AudioTrack track)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(track);

        if (_tracks.Remove(track))
        {
            OwnAudioNative.ownaudio_v1_track_remove(
                _mixerHandle.DangerousGetHandle(),
                track.GetNativeHandle());

            track.Dispose();
        }
    }

    #endregion

    #region Transport

    /// <summary>
    /// Starts all tracks simultaneously against the shared central clock.
    /// </summary>
    /// <remarks>
    /// All tracks are started in a single native call so they begin on the same
    /// audio callback — a sample-accurate start.  This avoids the per-track
    /// P/Invoke round-trips (and the synchronization drift they could introduce)
    /// of starting each track individually.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">Thrown when the session is disposed.</exception>
    public void PlayAll()
    {
        ThrowIfDisposed();

        int code = OwnAudioNative.ownaudio_v1_mixer_play_all(_mixerHandle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(PlayAll));
    }

    /// <summary>
    /// Pauses all tracks.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the session is disposed.</exception>
    public void PauseAll()
    {
        ThrowIfDisposed();

        foreach (AudioTrack track in _tracks)
        {
            track.Pause();
        }
    }

    /// <summary>
    /// Stops all tracks and resets their positions to zero.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the session is disposed.</exception>
    public void StopAll()
    {
        ThrowIfDisposed();

        foreach (AudioTrack track in _tracks)
        {
            track.Stop();
        }
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes all tracks and the native mixer.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (AudioTrack track in _tracks)
        {
            track.Dispose();
        }

        _tracks.Clear();
        _mixerHandle.Dispose();
    }

    #endregion

    #region Private helpers

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MultiTrackSession));
        }
    }

    #endregion
}
