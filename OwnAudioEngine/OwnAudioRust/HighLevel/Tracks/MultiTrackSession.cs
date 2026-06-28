using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe;
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
    private readonly ushort _channels;
    private readonly List<AudioTrack> _tracks = new();
    private readonly List<TrackFeeder> _feeders = new();
    private AudioOutputStream? _outputStream;
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
        _channels = channels;

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

        var track = new AudioTrack(handle, _mixerHandle.DangerousGetHandle(), _sampleRate, _channels);
        _tracks.Add(track);
        return track;
    }

    /// <summary>
    /// Opens an audio file, adds a new track, and wires a started
    /// <see cref="TrackFeeder"/> that streams the decoded samples into the track.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The file is decoded to the session's sample rate and channel count, so the
    /// resulting audio is layout-matched to the mixer.  The feeder begins filling the
    /// track's buffer immediately, but playback does not start until transport is
    /// driven (for example <see cref="PlayAll"/>).
    /// </para>
    /// <para>
    /// The returned feeder, its decoder, and the track are all owned by the session
    /// and released when the session is disposed (or the track is removed via
    /// <see cref="RemoveTrack"/>).  Use <see cref="TrackFeeder.Track"/> to reach the
    /// created track and <see cref="TrackFeeder.Completed"/> to observe end-of-stream.
    /// </para>
    /// </remarks>
    /// <param name="filePath">Path to the audio file to stream.</param>
    /// <returns>The started <see cref="TrackFeeder"/> driving the new track.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the session is disposed.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="filePath"/> is null/blank.</exception>
    /// <exception cref="Ownaudio.Safe.Exceptions.OwnAudioException">
    /// Thrown when the file cannot be opened or the native track cannot be created.
    /// </exception>
    public TrackFeeder AddFileTrack(string filePath)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var decoder = new StreamingAudioDecoder(
            filePath,
            targetSampleRate: (int)_sampleRate,
            targetChannels: _channels);

        AudioTrack track;
        try
        {
            track = AddTrack();
        }
        catch
        {
            decoder.Dispose();
            throw;
        }

        var feeder = new TrackFeeder(decoder, track);
        _feeders.Add(feeder);
        feeder.Start();
        return feeder;
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
            // Stop and dispose any feeder targeting this track before the track is
            // removed, so the pump thread cannot write into a destroyed track.
            for (int i = _feeders.Count - 1; i >= 0; i--)
            {
                if (_feeders[i].Track == track)
                {
                    _feeders[i].Dispose();
                    _feeders.RemoveAt(i);
                }
            }

            OwnAudioNative.ownaudio_v1_track_remove(
                _mixerHandle.DangerousGetHandle(),
                track.GetNativeHandle());

            track.Dispose();
        }
    }

    #endregion

    #region Output

    /// <summary>
    /// Opens an output stream driven directly by this session's native mixer and
    /// starts rendering on the real-time audio thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mixer is moved onto the cpal audio thread: every buffer is rendered
    /// natively (draining the lock-free command queue and summing all active
    /// tracks) with no per-buffer managed callback.  Track and effect changes
    /// keep flowing through the session's mixer handle while audio plays.
    /// </para>
    /// <para>
    /// The returned stream is owned by the session and disposed with it; it can
    /// also be paused/resumed directly.  Only one output stream may be opened per
    /// session.
    /// </para>
    /// </remarks>
    /// <param name="engine">The native audio engine that owns the output device.</param>
    /// <param name="device">
    /// The output device to use, or <see langword="null"/> for the system default.
    /// </param>
    /// <returns>The started <see cref="AudioOutputStream"/>.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the session is disposed.</exception>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="engine"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when an output stream has already been opened for this session.
    /// </exception>
    public AudioOutputStream OpenOutput(Safe.AudioEngine engine, AudioDevice? device = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(engine);

        if (_outputStream is not null)
        {
            throw new InvalidOperationException(
                "An output stream has already been opened for this session.");
        }

        var config = new AudioStreamConfig((int)_sampleRate, _channels);
        AudioOutputStream stream = engine.OpenMixerOutputStream(_mixerHandle, device, config);
        stream.Play();

        _outputStream = stream;
        return stream;
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
    /// Pauses all tracks simultaneously against the shared central clock.
    /// </summary>
    /// <remarks>
    /// All tracks are paused in a single native call so they pause on the same
    /// audio callback, avoiding the per-track P/Invoke round-trips (and the
    /// synchronization drift they could introduce) of pausing each individually.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">Thrown when the session is disposed.</exception>
    public void PauseAll()
    {
        ThrowIfDisposed();

        int code = OwnAudioNative.ownaudio_v1_mixer_pause_all(_mixerHandle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(PauseAll));
    }

    /// <summary>
    /// Stops all tracks simultaneously against the shared central clock.
    /// </summary>
    /// <remarks>
    /// All tracks are stopped in a single native call so they stop on the same
    /// audio callback, avoiding the per-track P/Invoke round-trips (and the
    /// synchronization drift they could introduce) of stopping each individually.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">Thrown when the session is disposed.</exception>
    public void StopAll()
    {
        ThrowIfDisposed();

        int code = OwnAudioNative.ownaudio_v1_mixer_stop_all(_mixerHandle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(StopAll));
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

        // Stop and tear down the output stream first so the audio thread releases
        // the mixer before the mixer handle (and tracks) are destroyed.
        _outputStream?.Dispose();
        _outputStream = null;

        // Stop the feeder pump threads (and their decoders) before disposing the
        // tracks they write into, so no pump can touch a destroyed track.
        foreach (TrackFeeder feeder in _feeders)
        {
            feeder.Dispose();
        }

        _feeders.Clear();

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
