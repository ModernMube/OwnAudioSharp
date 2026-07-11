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
    private readonly List<FileTrack> _fileTracks = new();
    private readonly List<MemoryTrack> _memoryTracks = new();
    private readonly List<InputTrack> _inputTracks = new();
    private readonly MasterEffectChain _masterEffects;
    private AudioOutputStream? _outputStream;
    private float _masterGain = 1.0f;
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

        _masterEffects = new MasterEffectChain(_mixerHandle.DangerousGetHandle());
    }

    #endregion

    #region Properties

    /// <summary>
    /// Gets the read-only list of tracks registered in this session.
    /// </summary>
    public IReadOnlyList<AudioTrack> Tracks => _tracks.AsReadOnly();

    /// <summary>
    /// Gets the native master effect chain applied once over the fully summed mix (after every
    /// track is rendered). Effects added here process the master bus in native code.
    /// </summary>
    public MasterEffectChain MasterEffects => _masterEffects;

    /// <summary>
    /// Gets or sets the master output gain applied once over the fully summed mix
    /// (linear amplitude; 1.0 = unity, 0.0 = silence). Values are clamped to be
    /// non-negative. The change is ramped on the audio thread, so it fades in
    /// without a click. Keeps working after <see cref="OpenOutput"/> has moved the
    /// mixer onto the audio thread.
    /// </summary>
    public float MasterGain
    {
        get => _masterGain;
        set
        {
            _masterGain = MathF.Max(0f, value);
            if (!_disposed)
            {
                int code = OwnAudioNative.ownaudio_v1_mixer_set_master_gain(
                    _mixerHandle.DangerousGetHandle(),
                    _masterGain);
                ErrorCodeMapper.ThrowIfError(code, nameof(MasterGain));
            }
        }
    }

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
    /// Opens an audio file, adds a new track, and installs a native file source that
    /// decodes and feeds the track on a Rust prefetch thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The file is decoded to the session's sample rate and channel count, so the
    /// resulting audio is layout-matched to the mixer.  The native prefetch thread
    /// begins filling the track's buffer immediately, but playback does not start
    /// until transport is driven (for example <see cref="PlayAll"/>).
    /// </para>
    /// <para>
    /// Unlike the general-purpose <see cref="TrackFeeder"/> (which pumps samples from
    /// managed code into a ring source), the returned <see cref="FileTrack"/> keeps
    /// the entire audio path in native code: no managed pump, decoder, or sample copy
    /// is involved (plan 14 / D.3).  The file track and its track are owned by the
    /// session and released when the session is disposed (or the track is removed via
    /// <see cref="RemoveTrack"/>).  Use <see cref="FileTrack.Track"/> to reach the
    /// created track and <see cref="FileTrack.Completed"/> to observe end-of-stream.
    /// </para>
    /// </remarks>
    /// <param name="filePath">Path to the audio file to stream.</param>
    /// <returns>The <see cref="FileTrack"/> driving the new track.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the session is disposed.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="filePath"/> is null/blank.</exception>
    /// <exception cref="Ownaudio.Safe.Exceptions.OwnAudioException">
    /// Thrown when the file cannot be opened or the native track cannot be created.
    /// </exception>
    public FileTrack AddFileTrack(string filePath)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        AudioTrack track = AddTrack();

        try
        {
            int code = OwnAudioNative.ownaudio_v1_track_open_file(
                _mixerHandle.DangerousGetHandle(),
                track.GetNativeHandle(),
                filePath,
                (uint)_sampleRate,
                _channels,
                prefetchFrames: 0,
                out IntPtr rawSource);
            ErrorCodeMapper.ThrowIfError(code, nameof(AddFileTrack));

            var sourceHandle = new FileSourceHandle();
            Marshal.InitHandle(sourceHandle, rawSource);

            var fileTrack = new FileTrack(track, sourceHandle, _sampleRate);
            _fileTracks.Add(fileTrack);
            return fileTrack;
        }
        catch
        {
            // The file source could not be installed; drop the just-added track so
            // the session does not keep a silent, orphaned track around.
            RemoveTrack(track);
            throw;
        }
    }

    /// <summary>
    /// Adds a new track and installs a native memory source that serves the given
    /// interleaved buffer directly on the audio thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The samples must already be at the session's sample rate and channel count.
    /// They are copied into native memory once (a control-thread copy, never on the
    /// audio path); the returned <see cref="MemoryTrack"/> keeps the entire audio
    /// path in native code — no managed pump or per-block copy is involved, so the
    /// GC can never stall it. The memory track and its track are owned by the session
    /// and released when the session is disposed (or the track is removed via
    /// <see cref="RemoveTrack"/>).
    /// </para>
    /// </remarks>
    /// <param name="samples">Interleaved samples to serve (session rate/channels).</param>
    /// <param name="loop">Whether to loop seamlessly at end-of-buffer.</param>
    /// <returns>The <see cref="MemoryTrack"/> driving the new track.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the session is disposed.</exception>
    /// <exception cref="Ownaudio.Safe.Exceptions.OwnAudioException">
    /// Thrown when the native track or memory source cannot be created.
    /// </exception>
    public MemoryTrack AddMemoryTrack(ReadOnlySpan<float> samples, bool loop = false)
    {
        ThrowIfDisposed();

        AudioTrack track = AddTrack();

        try
        {
            ref readonly float first = ref samples.IsEmpty
                ? ref System.Runtime.CompilerServices.Unsafe.NullRef<float>()
                : ref MemoryMarshal.GetReference(samples);

            int code = OwnAudioNative.ownaudio_v1_track_open_memory(
                _mixerHandle.DangerousGetHandle(),
                track.GetNativeHandle(),
                in first,
                (nuint)samples.Length,
                _channels,
                (byte)(loop ? 1 : 0),
                out IntPtr rawSource);
            ErrorCodeMapper.ThrowIfError(code, nameof(AddMemoryTrack));

            var sourceHandle = new MemorySourceHandle();
            Marshal.InitHandle(sourceHandle, rawSource);

            var memoryTrack = new MemoryTrack(
                track, sourceHandle, _mixerHandle.DangerousGetHandle(), _sampleRate, _channels);
            _memoryTracks.Add(memoryTrack);
            return memoryTrack;
        }
        catch
        {
            // The memory source could not be installed; drop the just-added track so
            // the session does not keep a silent, orphaned track around.
            RemoveTrack(track);
            throw;
        }
    }

    /// <summary>
    /// Adds a new track and opens a native input-capture source that feeds it directly from a
    /// device, with the capture callback writing into the track's ring buffer on the native side.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The device is opened at the session's sample rate and channel count. No managed callback is
    /// involved, so no audio data ever crosses into managed code and the GC can never stall capture
    /// or render. Capture starts paused; call <see cref="InputTrack.Play"/> to begin. The input track
    /// and its track are owned by the session and released when the session is disposed (or the track
    /// is removed via <see cref="RemoveTrack"/>).
    /// </para>
    /// </remarks>
    /// <param name="engine">The native engine that owns the input device.</param>
    /// <param name="device">The input device, or <see langword="null"/> for the system default.</param>
    /// <param name="bufferFrames">Device buffer size in frames; 0 lets the engine choose.</param>
    /// <returns>The <see cref="InputTrack"/> driving the new track.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the session is disposed.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="engine"/> is null.</exception>
    /// <exception cref="Ownaudio.Safe.Exceptions.OwnAudioException">
    /// Thrown when the native track or input stream cannot be created.
    /// </exception>
    public InputTrack AddInputTrack(Safe.AudioEngine engine, Safe.AudioDevice? device = null, uint bufferFrames = 0)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(engine);

        AudioTrack track = AddTrack();

        try
        {
            int code = OwnAudioNative.ownaudio_v1_track_open_input(
                engine.NativeHandle,
                _mixerHandle.DangerousGetHandle(),
                track.GetNativeHandle(),
                device?.Name,
                (uint)_sampleRate,
                _channels,
                bufferFrames,
                out IntPtr rawSource);
            ErrorCodeMapper.ThrowIfError(code, nameof(AddInputTrack));

            var sourceHandle = new InputSourceHandle();
            Marshal.InitHandle(sourceHandle, rawSource);

            var inputTrack = new InputTrack(track, sourceHandle);
            _inputTracks.Add(inputTrack);
            return inputTrack;
        }
        catch
        {
            RemoveTrack(track);
            throw;
        }
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
            // Dispose any file track targeting this track first, so its finished-poll
            // timer stops touching the source before the track (and its native source)
            // is removed from the mixer.
            for (int i = _fileTracks.Count - 1; i >= 0; i--)
            {
                if (_fileTracks[i].Track == track)
                {
                    _fileTracks[i].Dispose();
                    _fileTracks.RemoveAt(i);
                }
            }

            // Likewise dispose any memory track targeting this track before it is
            // removed from the mixer (its finished-poll timer stops touching the source).
            for (int i = _memoryTracks.Count - 1; i >= 0; i--)
            {
                if (_memoryTracks[i].Track == track)
                {
                    _memoryTracks[i].Dispose();
                    _memoryTracks.RemoveAt(i);
                }
            }

            // Dispose any input track targeting this track (stops capture) before removal.
            for (int i = _inputTracks.Count - 1; i >= 0; i--)
            {
                if (_inputTracks[i].Track == track)
                {
                    _inputTracks[i].Dispose();
                    _inputTracks.RemoveAt(i);
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

    /// <summary>
    /// Gets the mixer's most recently measured master output peak levels — the
    /// absolute peak of the summed mix after master effects and the master gain,
    /// for the last rendered block.
    /// </summary>
    /// <remarks>
    /// Values range from <c>0.0</c> (silence) upward, reaching <c>1.0</c> at full
    /// scale (or above when the mix clips). A mono session reports the same value
    /// on both channels. Updated by the audio thread every block; safe to poll from
    /// any thread for metering displays.
    /// </remarks>
    /// <returns>The left and right output peak levels.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the session is disposed.</exception>
    public (float Left, float Right) GetMasterPeaks()
    {
        ThrowIfDisposed();

        int code = OwnAudioNative.ownaudio_v1_mixer_get_master_peaks(
            _mixerHandle.DangerousGetHandle(),
            out float left,
            out float right);
        ErrorCodeMapper.ThrowIfError(code, nameof(GetMasterPeaks));
        return (left, right);
    }

    /// <summary>
    /// Starts capturing the mixer's master output (the summed mix after master
    /// effects and gain, i.e. exactly what reaches the device) into a lock-free
    /// ring so the control thread can persist it — for example, to record a WAV.
    /// </summary>
    /// <remarks>
    /// Drain the ring with <see cref="ReadCapture"/> and stop with
    /// <see cref="StopCapture"/>. Capture is non-blocking: if the drain falls
    /// behind, overflowing samples are dropped rather than stalling rendering.
    /// Calling this while capture is already active replaces the previous ring.
    /// </remarks>
    /// <param name="capacitySamples">Ring capacity in interleaved samples.</param>
    /// <exception cref="ObjectDisposedException">Thrown when the session is disposed.</exception>
    public void StartCapture(int capacitySamples)
    {
        ThrowIfDisposed();

        int code = OwnAudioNative.ownaudio_v1_mixer_capture_start(
            _mixerHandle.DangerousGetHandle(),
            (nuint)Math.Max(1, capacitySamples));
        ErrorCodeMapper.ThrowIfError(code, nameof(StartCapture));
    }

    /// <summary>
    /// Reads up to <paramref name="destination"/><c>.Length</c> captured master
    /// samples into <paramref name="destination"/>, returning the number actually
    /// read (<c>0</c> when the ring is empty or capture is inactive).
    /// </summary>
    /// <remarks>
    /// Single-consumer: call from one thread only, and never concurrently with
    /// <see cref="StopCapture"/>.
    /// </remarks>
    /// <param name="destination">Buffer to fill with captured interleaved samples.</param>
    /// <returns>The number of samples written into <paramref name="destination"/>.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the session is disposed.</exception>
    public int ReadCapture(Span<float> destination)
    {
        ThrowIfDisposed();

        if (destination.IsEmpty)
        {
            return 0;
        }

        nuint read;
        int code;
        unsafe
        {
            fixed (float* ptr = destination)
            {
                code = OwnAudioNative.ownaudio_v1_mixer_capture_read(
                    _mixerHandle.DangerousGetHandle(),
                    ptr,
                    (nuint)destination.Length,
                    out read);
            }
        }

        ErrorCodeMapper.ThrowIfError(code, nameof(ReadCapture));
        return (int)read;
    }

    /// <summary>
    /// Stops master-output capture and releases the ring's read side. Safe to call
    /// when capture is inactive. Must not run concurrently with
    /// <see cref="ReadCapture"/>.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the session is disposed.</exception>
    public void StopCapture()
    {
        ThrowIfDisposed();

        int code = OwnAudioNative.ownaudio_v1_mixer_capture_stop(
            _mixerHandle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(StopCapture));
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

        // Dispose the file tracks (stopping their finished-poll timers and releasing
        // the native file-source handles) before disposing the tracks they render.
        foreach (FileTrack fileTrack in _fileTracks)
        {
            fileTrack.Dispose();
        }

        _fileTracks.Clear();

        foreach (MemoryTrack memoryTrack in _memoryTracks)
        {
            memoryTrack.Dispose();
        }

        _memoryTracks.Clear();

        foreach (InputTrack inputTrack in _inputTracks)
        {
            inputTrack.Dispose();
        }

        _inputTracks.Clear();

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
