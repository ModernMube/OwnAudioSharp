using System;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Exceptions;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Tracks;

/// <summary>
/// Live capture track: a native device stream writes straight into the track's ring on
/// its own thread, the mixer reads it. No managed callback, so the GC can't stall it —
/// we're just the controller here (start/stop + metering). Starts paused.
/// </summary>
public sealed class InputTrack : IDisposable
{
    #region Fields

    private readonly AudioTrack _track;
    private readonly InputSourceHandle _sourceHandle;
    private readonly object _sync = new();
    private bool _disposed;

    #endregion

    #region Construction

    /// <summary>
    /// Wraps a native input source already opened on the track.
    /// </summary>
    internal InputTrack(AudioTrack track, InputSourceHandle sourceHandle)
    {
        _track = track;
        _sourceHandle = sourceHandle;
    }

    #endregion

    #region Propertyes

    /// <summary>
    /// The track this capture feeds.
    /// </summary>
    public AudioTrack Track => _track;

    #endregion

    #region Capture control

    /// <summary>
    /// Starts (or resumes) device capture.
    /// </summary>
    public void Play()
    {
        lock (_sync)
        {
            if (_disposed) { return; }

            int code = OwnAudioNative.ownaudio_v1_input_source_play(_sourceHandle.DangerousGetHandle());
            ErrorCodeMapper.ThrowIfError(code, nameof(Play));
        }
    }

    /// <summary>
    /// Pauses capture. Whatever is already in the ring still plays out.
    /// </summary>
    public void Pause()
    {
        lock (_sync)
        {
            if (_disposed) { return; }

            int code = OwnAudioNative.ownaudio_v1_input_source_pause(_sourceHandle.DangerousGetHandle());
            ErrorCodeMapper.ThrowIfError(code, nameof(Pause));
        }
    }

    /// <summary>
    /// Last capture peaks, measured natively in the capture callback.
    /// </summary>
    /// <returns>Left and right peak, (0,0) once disposed.</returns>
    public (float Left, float Right) GetInputPeaks()
    {
        lock (_sync)
        {
            if (_disposed) { return (0f, 0f); }

            int code = OwnAudioNative.ownaudio_v1_input_source_get_peaks(
                _sourceHandle.DangerousGetHandle(),
                out float left,
                out float right);
            ErrorCodeMapper.ThrowIfError(code, nameof(GetInputPeaks));
            return (left, right);
        }
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Stops capture and drops the native control handle. The track stays, it's the
    /// session's.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) { return; }
            _disposed = true;
        }

        _sourceHandle.Dispose();
    }

    #endregion
}
