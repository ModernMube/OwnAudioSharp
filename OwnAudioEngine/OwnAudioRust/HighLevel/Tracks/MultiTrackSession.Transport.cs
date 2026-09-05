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
/// Transport for the whole session, the capture reads and the master fx tap. The unsafe
/// blocks here are the pull path the caller drains on its own thread.
/// </summary>
public sealed partial class MultiTrackSession : IDisposable
{
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

    /// <summary>
    /// Starts mirroring the summed mix on both sides of the master effect chain, for an analyzer
    /// that wants to see what the master chain does. Sits ahead of the master gain and pan.
    /// </summary>
    /// <param name="capacitySamples">ring size per side, a few blocks is plenty</param>
    public void StartMasterFxTap(int capacitySamples)
    {
        _throwIfDisposed();

        int code = OwnAudioNative.ownaudio_v1_mixer_master_fx_tap_start(
            _mixerHandle.DangerousGetHandle(),
            (nuint)Math.Max(1, capacitySamples));
        ErrorCodeMapper.ThrowIfError(code, nameof(StartMasterFxTap));
        _masterTapping = true;
    }

    /// <summary>
    /// Drains a chunk of the master tap. Both spans come back the same length, so index i is the
    /// same instant on either side.
    /// </summary>
    /// <returns>How many samples landed in each span.</returns>
    public int ReadMasterFxTap(Span<float> pre, Span<float> post)
    {
        _throwIfDisposed();

        int len = Math.Min(pre.Length, post.Length);
        if (len == 0) { return 0; }

        int code;
        nuint read;
        unsafe
        {
            fixed (float* preptr = pre)
            fixed (float* postptr = post)
            {
                code = OwnAudioNative.ownaudio_v1_mixer_master_fx_tap_read(
                    _mixerHandle.DangerousGetHandle(),
                    preptr,
                    postptr,
                    (nuint)len,
                    out read);
            }
        }

        ErrorCodeMapper.ThrowIfError(code, nameof(ReadMasterFxTap));
        return (int)read;
    }

    /// <summary>
    /// Stops the master tap. Harmless when nothing is tapping.
    /// </summary>
    public void StopMasterFxTap()
    {
        if (_disposed || !_masterTapping) { return; }
        _masterTapping = false;
        int code = OwnAudioNative.ownaudio_v1_mixer_master_fx_tap_stop(_mixerHandle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(StopMasterFxTap));
    }

    #endregion
}
