using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe;
using Ownaudio.Safe.Exceptions;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Tracks;

/// <summary>
/// A bunch of <see cref="AudioTrack"/>s riding one shared sample-accurate clock. Owns the
/// native MultiTrackMixer; PlayAll starts everything in a single native call so they
/// really do begin on the same callback. Disposing kills every track it handed out.
/// </summary>
public sealed class MultiTrackSession : IDisposable
{
    #region Fields

    private readonly MixerHandle _mixerHandle;
    private readonly float _sampleRate;
    private readonly ushort _channels;
    private readonly List<AudioTrack> _tracks = new();
    private readonly IReadOnlyList<AudioTrack> _tracksView;
    private readonly List<FileTrack> _fileTracks = new();
    private readonly List<MemoryTrack> _memoryTracks = new();
    private readonly List<InputTrack> _inputTracks = new();
    private readonly MasterEffectChain _masterEffects;
    private AudioOutputStream? _outputStream;
    private float _masterGain = 1.0f;
    private float _masterPan = 0.0f;
    private bool _disposed;

    #endregion

    #region Construction

    /// <summary>
    /// Spins up a session at the given output rate and channel count (1 = mono, 2 = stereo).
    /// </summary>
    public MultiTrackSession(float sampleRate, ushort channels)
    {
        _sampleRate = sampleRate;
        _channels = channels;

        int code = OwnAudioNative.ownaudio_v1_mixer_create(sampleRate, channels, out IntPtr rawMixer);
        ErrorCodeMapper.ThrowIfError(code, nameof(MultiTrackSession));

        _mixerHandle = new MixerHandle();
        Marshal.InitHandle(_mixerHandle, rawMixer);

        _tracksView = _tracks.AsReadOnly();
        _masterEffects = new MasterEffectChain(_mixerHandle.DangerousGetHandle());
    }

    #endregion

    #region Propertyes

    /// <summary>
    /// The tracks registered here. Same view instance every time, it wraps the live list.
    /// </summary>
    public IReadOnlyList<AudioTrack> Tracks => _tracksView;

    /// <summary>
    /// Native effect chain over the fully summed mix.
    /// </summary>
    public MasterEffectChain MasterEffects => _masterEffects;

    /// <summary>
    /// Master gain over the summed mix, clamped non-negative. Ramped on the audio thread
    /// so it doesn't click, and it keeps working after OpenOutput moved the mixer there.
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

    /// <summary>
    /// Master pan, -1..+1, equal-power law normalized at center so a centered master
    /// leaves the mix alone. Ramped too.
    /// </summary>
    public float MasterPan
    {
        get => _masterPan;
        set
        {
            _masterPan = Math.Clamp(value, -1.0f, 1.0f);
            if (!_disposed)
            {
                int code = OwnAudioNative.ownaudio_v1_mixer_set_master_pan(
                    _mixerHandle.DangerousGetHandle(),
                    _masterPan);
                ErrorCodeMapper.ThrowIfError(code, nameof(MasterPan));
            }
        }
    }

    #endregion

    #region Track management

    /// <summary>
    /// Adds an empty track to the session.
    /// </summary>
    public AudioTrack AddTrack()
    {
        _throwIfDisposed();

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
    /// Opens a file, adds a track and hangs a native file source on it — decoding and
    /// feeding both run on a Rust prefetch thread, nothing managed in the audio path.
    /// The prefetch starts filling right away but playback waits for PlayAll. Both the
    /// file track and its track belong to the session.
    /// </summary>
    /// <param name="filePath"></param>
    public FileTrack AddFileTrack(string filePath)
    {
        _throwIfDisposed();
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
            RemoveTrack(track);
            throw;
        }
    }

    /// <summary>
    /// Adds a track served straight from an interleaved buffer by the audio thread. The
    /// samples must already be at session rate/channels; they're copied into native
    /// memory once here, never on the audio path.
    /// </summary>
    /// <param name="samples">Interleaved samples at session rate/channels.</param>
    /// <param name="loop">Loop seamlessly at end-of-buffer.</param>
    public MemoryTrack AddMemoryTrack(ReadOnlySpan<float> samples, bool loop = false)
    {
        _throwIfDisposed();

        AudioTrack track = AddTrack();

        try
        {
            ref readonly float first = ref samples.IsEmpty
                ? ref Unsafe.NullRef<float>()
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
            RemoveTrack(track);
            throw;
        }
    }

    /// <summary>
    /// Adds a track fed by a native device capture — the callback writes into the track's
    /// ring on the native side, so no audio data crosses into managed code. Opened at
    /// session rate/channels and starts paused; call <see cref="InputTrack.Play"/>.
    /// </summary>
    /// <param name="engine">The engine owning the input device.</param>
    /// <param name="device">null = system default.</param>
    /// <param name="bufferFrames">Device buffer in frames, 0 lets the engine pick.</param>
    public InputTrack AddInputTrack(Safe.AudioEngine engine, Safe.AudioDevice? device = null, uint bufferFrames = 0)
    {
        _throwIfDisposed();
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
    /// Removes and disposes a track. Any source wrapper pointing at it goes first, so no
    /// poll timer is still touching the source when the track leaves the mixer.
    /// </summary>
    /// <param name="track"></param>
    public void RemoveTrack(AudioTrack track)
    {
        _throwIfDisposed();
        ArgumentNullException.ThrowIfNull(track);

        if (_tracks.Remove(track))
        {
            for (int i = _fileTracks.Count - 1; i >= 0; i--)
            {
                if (_fileTracks[i].Track == track)
                {
                    _fileTracks[i].Dispose();
                    _fileTracks.RemoveAt(i);
                }
            }

            for (int i = _memoryTracks.Count - 1; i >= 0; i--)
            {
                if (_memoryTracks[i].Track == track)
                {
                    _memoryTracks[i].Dispose();
                    _memoryTracks.RemoveAt(i);
                }
            }

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
    /// Opens an output stream driven by this session's mixer and starts rendering on the
    /// RT thread — every buffer summed natively, no per-buffer managed callback. Track and
    /// effect changes keep flowing through the mixer handle while it plays. One stream per
    /// session, owned and disposed by it.
    /// </summary>
    /// <param name="engine">The engine owning the output device.</param>
    /// <param name="device">null = system default.</param>
    public AudioOutputStream OpenOutput(Safe.AudioEngine engine, AudioDevice? device = null)
    {
        _throwIfDisposed();
        ArgumentNullException.ThrowIfNull(engine);

        if (_outputStream is not null)
            throw new InvalidOperationException("An output stream has already been opened for this session.");

        var config = new AudioStreamConfig((int)_sampleRate, _channels);
        AudioOutputStream stream = engine.OpenMixerOutputStream(_mixerHandle, device, config);
        stream.Play();

        _outputStream = stream;
        return stream;
    }

    #endregion

    #region Transport

    /// <summary>
    /// Starts every track on the same audio callback — one native call, no per-track
    /// P/Invoke round-trips to drift on.
    /// </summary>
    public void PlayAll()
    {
        _throwIfDisposed();

        int code = OwnAudioNative.ownaudio_v1_mixer_play_all(_mixerHandle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(PlayAll));
    }

    /// <summary>
    /// Pauses everything on the same callback, same single-call deal.
    /// </summary>
    public void PauseAll()
    {
        _throwIfDisposed();

        int code = OwnAudioNative.ownaudio_v1_mixer_pause_all(_mixerHandle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(PauseAll));
    }

    /// <summary>
    /// Stops everything on the same callback.
    /// </summary>
    public void StopAll()
    {
        _throwIfDisposed();

        int code = OwnAudioNative.ownaudio_v1_mixer_stop_all(_mixerHandle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(StopAll));
    }

    /// <summary>
    /// Master peaks of the last rendered block, after master effects and gain. Updated
    /// every block by the audio thread, fine to poll from anywhere for a meter.
    /// </summary>
    /// <returns>Left and right peak; can go above 1.0 when the mix clips.</returns>
    public (float Left, float Right) GetMasterPeaks()
    {
        _throwIfDisposed();

        int code = OwnAudioNative.ownaudio_v1_mixer_get_master_peaks(
            _mixerHandle.DangerousGetHandle(),
            out float left,
            out float right);
        ErrorCodeMapper.ThrowIfError(code, nameof(GetMasterPeaks));
        return (left, right);
    }

    /// <summary>
    /// Starts tapping the master output into a lock-free ring so the control thread can
    /// write it out somewhere (a WAV, say). Drain it with ReadCapture. If the drain falls
    /// behind we drop samples rather than stall rendering. Calling it again replaces the ring.
    /// </summary>
    /// <param name="capacitySamples">Ring capacity in interleaved samples.</param>
    public void StartCapture(int capacitySamples)
    {
        _throwIfDisposed();

        int code = OwnAudioNative.ownaudio_v1_mixer_capture_start(
            _mixerHandle.DangerousGetHandle(),
            (nuint)Math.Max(1, capacitySamples));
        ErrorCodeMapper.ThrowIfError(code, nameof(StartCapture));
    }

    /// <summary>
    /// Drains captured master samples into destination. Single consumer: one thread only,
    /// and never next to StopCapture.
    /// </summary>
    /// <returns>How many samples actually landed there, 0 on an empty ring.</returns>
    public int ReadCapture(Span<float> destination)
    {
        _throwIfDisposed();

        if (destination.IsEmpty) { return 0; }

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
    /// Stops the master capture. Fine to call when it isn't running, just don't race it
    /// against ReadCapture.
    /// </summary>
    public void StopCapture()
    {
        _throwIfDisposed();

        int code = OwnAudioNative.ownaudio_v1_mixer_capture_stop(_mixerHandle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(StopCapture));
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Tears down the output stream first so the audio thread lets go of the mixer, then
    /// the source wrappers (their poll timers), then the tracks and the mixer itself.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _outputStream?.Dispose();
        _outputStream = null;

        foreach (FileTrack fileTrack in _fileTracks) fileTrack.Dispose();
        _fileTracks.Clear();

        foreach (MemoryTrack memoryTrack in _memoryTracks) memoryTrack.Dispose();
        _memoryTracks.Clear();

        foreach (InputTrack inputTrack in _inputTracks) inputTrack.Dispose();
        _inputTracks.Clear();

        foreach (AudioTrack track in _tracks) track.Dispose();

        _tracks.Clear();
        _mixerHandle.Dispose();
    }

    #endregion

    #region Private helpers

    private void _throwIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MultiTrackSession));
    }

    #endregion
}
