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
/// Everything that puts a track into the session or takes it back out, plus opening the
/// shared capture bridge.
/// </summary>
public sealed partial class MultiTrackSession : IDisposable
{
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
    public FileTrack AddFileTrack(string filePath) => AddFileTrack(filePath, _channels);

    /// <summary>
    /// Same, but decoding to a width of your choosing instead of the session's. A stereo file
    /// on an 8 channel bus stays a stereo decode, stretch and effect chain - only the summation
    /// into the bus is wide, which is what keeps a wide session from costing four times the CPU.
    /// Where it lands on the bus is the track's routing, not this.
    /// </summary>
    /// <param name="filePath"></param>
    /// <param name="channels">decode width, 0 keeps the file's own</param>
    public FileTrack AddFileTrack(string filePath, ushort channels)
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
                channels,
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
    /// Opens the shared capture bridge on the given input device, at the device's own physical
    /// width. Live input tracks then tap it with <see cref="CaptureBridge.Attach"/> instead of
    /// each opening a stream of its own - one driver client, however many inputs. Starts paused.
    /// The session disposes it with everything else.
    /// </summary>
    /// <param name="engine">The engine owning the input device.</param>
    /// <param name="device">null = system default.</param>
    /// <param name="bufferFrames">Device buffer in frames, 0 lets the engine pick.</param>
    public CaptureBridge OpenCapture(Safe.AudioEngine engine, Safe.AudioDevice? device = null, uint bufferFrames = 0)
    {
        _throwIfDisposed();
        ArgumentNullException.ThrowIfNull(engine);

        int code = OwnAudioNative.ownaudio_v1_capture_open(
            engine.NativeHandle,
            device?.Name,
            (uint)_sampleRate,
            bufferFrames,
            out IntPtr rawCapture);
        ErrorCodeMapper.ThrowIfError(code, nameof(OpenCapture));

        var handle = new CaptureHandle();
        Marshal.InitHandle(handle, rawCapture);

        code = OwnAudioNative.ownaudio_v1_capture_channel_count(rawCapture, out ushort channels);
        if (code != 0)
        {
            handle.Dispose();
            ErrorCodeMapper.ThrowIfError(code, nameof(OpenCapture));
        }

        var bridge = new CaptureBridge(handle, _mixerHandle.DangerousGetHandle(), channels);
        _captureBridges.Add(bridge);
        return bridge;
    }

    /// <summary>
    /// Which bus channels the master chain, gain and pan run over. Empty (the default) is the
    /// whole bus, exactly as it always was. Narrow it to [0,1] and a click feed on 3/4 reaches
    /// the driver as mixed - a limiter on the main pair no longer squashes the direct out.
    /// </summary>
    public int[] MasterChannelScope
    {
        get => _masterScope;
        set
        {
            int[] _scope = value ?? Array.Empty<int>();
            foreach (int ch in _scope)
                if (ch < 0 || ch >= _channels)
                    throw new ArgumentOutOfRangeException(nameof(value), ch,
                        $"Master scope channel is outside the session's {_channels}.");

            _masterScope = _scope;
            if (_disposed) { return; }

            Span<uint> scope = stackalloc uint[_scope.Length];
            for (int i = 0; i < _scope.Length; i++)
                scope[i] = (uint)_scope[i];

            ref readonly uint first = ref scope.IsEmpty
                ? ref Unsafe.NullRef<uint>()
                : ref MemoryMarshal.GetReference(scope);

            int code = OwnAudioNative.ownaudio_v1_mixer_set_master_channel_scope(
                _mixerHandle.DangerousGetHandle(),
                in first,
                (nuint)scope.Length);
            ErrorCodeMapper.ThrowIfError(code, nameof(MasterChannelScope));
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
}
