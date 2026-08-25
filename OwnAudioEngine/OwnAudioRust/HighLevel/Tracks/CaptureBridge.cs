using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe;
using Ownaudio.Safe.Exceptions;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Tracks;

/// <summary>
/// One capture stream, shared by every live input track. The device opens once at its full
/// physical width and each track taps the channels it wants - no managed callback anywhere,
/// the fan out happens natively. On ASIO this is the only workable shape: a driver takes one
/// client, and every registered callback walks its channel buffers again. Starts paused.
/// </summary>
public sealed class CaptureBridge : IDisposable
{
    #region Fields

    private readonly CaptureHandle _handle;
    private readonly IntPtr _mixerHandle;
    private readonly ushort _channels;
    private readonly object _sync = new object();
    private bool _disposed;

    #endregion

    #region Construction

    internal CaptureBridge(CaptureHandle handle, IntPtr mixerHandle, ushort channels)
    {
        _handle = handle;
        _mixerHandle = mixerHandle;
        _channels = channels;
    }

    #endregion

    #region Propertyes

    /// <summary>
    /// Physical capture channels the bridge actually opened - the range a track's map may
    /// address, and the number of input sockets a UI should draw.
    /// </summary>
    public int ChannelCount => _channels;

    #endregion

    #region Taps

    /// <summary>
    /// Hangs a track off the bridge: capture channel captureChannels[i] becomes the track's
    /// channel i. Also sets the track's own processing width, so a mono vocal costs a mono
    /// chain. Calling it again for the same track replaces the map - that's a live reroute,
    /// and nothing reopens a stream.
    /// </summary>
    /// <param name="track"></param>
    /// <param name="captureChannels">zero based capture channel per track channel</param>
    public void Attach(AudioTrack track, ReadOnlySpan<int> captureChannels)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (captureChannels.IsEmpty)
            throw new ArgumentException("A capture tap needs at least one channel.", nameof(captureChannels));

        lock (_sync)
        {
            if (_disposed) { return; }

            Span<uint> map = stackalloc uint[captureChannels.Length];
            for (int i = 0; i < captureChannels.Length; i++)
            {
                int _ch = captureChannels[i];
                if (_ch < 0 || _ch >= _channels)
                    throw new ArgumentOutOfRangeException(nameof(captureChannels),
                        $"Capture channel {_ch} is outside the {_channels} the device opened.");

                map[i] = (uint)_ch;
            }

            int code = OwnAudioNative.ownaudio_v1_track_attach_capture(
                _mixerHandle,
                track.GetNativeHandle(),
                _handle.DangerousGetHandle(),
                in map[0],
                (nuint)map.Length);
            ErrorCodeMapper.ThrowIfError(code, nameof(Attach));
        }
    }

    /// <summary>
    /// Stops feeding this track. It keeps its ring until its source is replaced, so it just
    /// underruns into silence.
    /// </summary>
    /// <param name="track"></param>
    public void Detach(AudioTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);

        lock (_sync)
        {
            if (_disposed) { return; }

            int code = OwnAudioNative.ownaudio_v1_track_detach_capture(
                _handle.DangerousGetHandle(),
                track.GetNativeHandle());
            ErrorCodeMapper.ThrowIfError(code, nameof(Detach));
        }
    }

    #endregion

    #region Capture control

    /// <summary>
    /// Starts or resumes capture for every attached tap.
    /// </summary>
    public void Play()
    {
        lock (_sync)
        {
            if (_disposed) { return; }

            int code = OwnAudioNative.ownaudio_v1_capture_play(_handle.DangerousGetHandle());
            ErrorCodeMapper.ThrowIfError(code, nameof(Play));
        }
    }

    /// <summary>
    /// Pauses capture. Whatever is already in the rings still plays out.
    /// </summary>
    public void Pause()
    {
        lock (_sync)
        {
            if (_disposed) { return; }

            int code = OwnAudioNative.ownaudio_v1_capture_pause(_handle.DangerousGetHandle());
            ErrorCodeMapper.ThrowIfError(code, nameof(Pause));
        }
    }

    /// <summary>
    /// Device side peaks over the first two physical channels. Per track levels come from the
    /// track itself.
    /// </summary>
    /// <returns>Left and right peak, (0,0) once disposed.</returns>
    public (float Left, float Right) GetInputPeaks()
    {
        lock (_sync)
        {
            if (_disposed) { return (0f, 0f); }

            int code = OwnAudioNative.ownaudio_v1_capture_get_peaks(
                _handle.DangerousGetHandle(),
                out float left,
                out float right);
            ErrorCodeMapper.ThrowIfError(code, nameof(GetInputPeaks));
            return (left, right);
        }
    }

    /// <summary>
    /// Error state the backend recorded on the capture stream. errorCount is a monotonic
    /// total, compare it against the last seen value to catch a fresh fault.
    /// </summary>
    public AudioStreamErrorKind PollErrorState(out ulong errorCount)
    {
        lock (_sync)
        {
            if (_disposed) { errorCount = 0; return AudioStreamErrorKind.None; }

            int code = OwnAudioNative.ownaudio_v1_capture_get_error_state(
                _handle.DangerousGetHandle(),
                out uint kind,
                out errorCount);
            ErrorCodeMapper.ThrowIfError(code, nameof(PollErrorState));

            return (AudioStreamErrorKind)kind;
        }
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Stops capture and releases the stream. Attached tracks stay, they're the session's.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) { return; }
            _disposed = true;
        }

        _handle.Dispose();
    }

    #endregion
}
