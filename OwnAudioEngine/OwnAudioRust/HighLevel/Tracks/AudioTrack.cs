using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Exceptions;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Tracks;

/// <summary>
/// Represents a single audio track within a <see cref="MultiTrackSession"/>.
/// </summary>
/// <remarks>
/// <para>
/// A track holds a lock-free ring buffer on the native side; fill it with decoded
/// audio samples by calling <c>Write</c> from any thread.  Playback transport,
/// tempo, and pitch are controlled via the properties below, all of which forward
/// immediately to the native Rust layer.
/// </para>
/// <para>
/// Effects are managed through the <see cref="Effects"/> chain property.
/// </para>
/// </remarks>
public sealed class AudioTrack : IDisposable
{
    #region Fields

    private readonly TrackHandle _handle;
    private readonly IntPtr _mixerHandle;
    private readonly float _sampleRate;
    private bool _disposed;

    private float _gain            = 1.0f;
    private float _tempo           = 1.0f;
    private float _pitchSemitones  = 0.0f;
    private bool  _muted           = false;

    #endregion

    #region Construction

    internal AudioTrack(TrackHandle handle, IntPtr mixerHandle, float sampleRate)
    {
        _handle      = handle;
        _mixerHandle = mixerHandle;
        _sampleRate  = sampleRate;
        Effects      = new TrackEffectChain(mixerHandle, handle.DangerousGetHandle());
    }

    #endregion

    #region Properties

    /// <summary>
    /// Gets the effect chain for this track.
    /// </summary>
    public TrackEffectChain Effects { get; }

    /// <summary>
    /// Gets or sets the track gain (linear amplitude; 1.0 = unity, 0.0 = silence).
    /// </summary>
    public float Gain
    {
        get => _gain;
        set
        {
            _gain = MathF.Max(0f, value);
            if (!_disposed)
            {
                OwnAudioNative.ownaudio_v1_track_set_gain(_handle.DangerousGetHandle(), _gain);
            }
        }
    }

    /// <summary>
    /// Gets or sets the tempo ratio (1.0 = normal speed, 2.0 = double speed).
    /// </summary>
    public float Tempo
    {
        get => _tempo;
        set
        {
            _tempo = Math.Clamp(value, 0.25f, 4.0f);
            if (!_disposed)
            {
                OwnAudioNative.ownaudio_v1_track_set_tempo(_handle.DangerousGetHandle(), _tempo);
            }
        }
    }

    /// <summary>
    /// Gets or sets the pitch shift in semitones (−24 to +24).
    /// </summary>
    public float PitchSemitones
    {
        get => _pitchSemitones;
        set
        {
            _pitchSemitones = Math.Clamp(value, -24f, 24f);
            if (!_disposed)
            {
                OwnAudioNative.ownaudio_v1_track_set_pitch(_handle.DangerousGetHandle(), _pitchSemitones);
            }
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the track output is silenced.
    /// </summary>
    public bool Muted
    {
        get => _muted;
        set
        {
            _muted = value;
            if (!_disposed)
            {
                OwnAudioNative.ownaudio_v1_track_set_mute(_handle.DangerousGetHandle(), value ? 1f : 0f);
            }
        }
    }

    #endregion

    #region Transport

    /// <summary>
    /// Starts or resumes playback of this track.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the track is disposed.</exception>
    public void Play()
    {
        ThrowIfDisposed();
        int code = OwnAudioNative.ownaudio_v1_track_play(_handle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(Play));
    }

    /// <summary>
    /// Pauses playback without resetting the position.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the track is disposed.</exception>
    public void Pause()
    {
        ThrowIfDisposed();
        int code = OwnAudioNative.ownaudio_v1_track_pause(_handle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(Pause));
    }

    /// <summary>
    /// Stops playback and resets the position to zero.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the track is disposed.</exception>
    public void Stop()
    {
        ThrowIfDisposed();
        int code = OwnAudioNative.ownaudio_v1_track_stop(_handle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(Stop));
    }

    /// <summary>
    /// Seeks the track to the specified time position.
    /// </summary>
    /// <param name="position">Target playback position.</param>
    /// <exception cref="ObjectDisposedException">Thrown when the track is disposed.</exception>
    public void Seek(TimeSpan position)
    {
        ThrowIfDisposed();
        ulong sample = (ulong)(position.TotalSeconds * _sampleRate);
        int code = OwnAudioNative.ownaudio_v1_track_seek(_handle.DangerousGetHandle(), sample);
        ErrorCodeMapper.ThrowIfError(code, nameof(Seek));
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes the track handle and releases native resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Dispose();
    }

    #endregion

    #region Internal helpers

    /// <summary>Returns the raw native handle value for use by <see cref="MultiTrackSession"/>.</summary>
    internal IntPtr GetNativeHandle() => _handle.DangerousGetHandle();

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AudioTrack));
        }
    }

    #endregion
}
