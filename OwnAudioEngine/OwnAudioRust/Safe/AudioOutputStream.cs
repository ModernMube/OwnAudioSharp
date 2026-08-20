using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Native.RustAudio.Structs;
using Ownaudio.Safe.Callbacks;
using Ownaudio.Safe.Exceptions;
using Ownaudio.Safe.Handles;
using Ownaudio.Safe.Validation;

namespace Ownaudio.Safe;

/// <summary>
/// Safe wrapper around a native output stream. You get one from AudioEngine.OpenOutputStream,
/// paused, call Play to start. Play/Pause/Dispose are not meant to race each other.
/// </summary>
public sealed class AudioOutputStream : IDisposable
{
    private readonly AudioOutputStreamHandle _handle;
    private readonly AudioOutputCallbackMarshaller? _marshaller;
    private bool _disposed;

    /// <summary>
    /// Fires on a threadpool thread when the audio callback throws. We swallow it at the ffi
    /// boundary so the rt thread keeps running.
    /// </summary>
    public event EventHandler<Exception>? CallbackError;

    private AudioOutputStream(AudioOutputStreamHandle handle, AudioOutputCallbackMarshaller? marshaller)
    {
        _handle     = handle;
        _marshaller = marshaller;

        // mixer driven streams render natively, no marshaller, nothing to forward
        if (_marshaller is not null)
            _marshaller.CallbackError += (_, ex) => CallbackError?.Invoke(this, ex);
    }

    // engine only
    internal static unsafe AudioOutputStream Open(
        AudioEngineHandle engine,
        AudioDevice? device,
        AudioStreamConfig config,
        AudioOutputCallbackHandler callback)
    {
        var marshaller = new AudioOutputCallbackMarshaller(callback);

        NativeStreamConfig nativeConfig = config.ToNative();
        IntPtr deviceNamePtr = device is not null ? Marshal.StringToCoTaskMemUTF8(device.Name) : IntPtr.Zero;

        int code;
        IntPtr rawStream;

        try
        {
            code = OwnAudioNative.ownaudio_v1_open_output_stream(
                engine.DangerousGetHandle(),
                deviceNamePtr,
                in nativeConfig,
                AudioOutputCallbackMarshaller.NativeFunctionPointer,
                marshaller.UserData,
                out rawStream);
        }
        finally
        {
            if (deviceNamePtr != IntPtr.Zero) Marshal.FreeCoTaskMem(deviceNamePtr);
        }

        if (code != (int)NativeErrorCode.Success)
        {
            marshaller.Dispose();
            ErrorCodeMapper.ThrowIfError(code, nameof(Open));
        }

        var handle = new AudioOutputStreamHandle();
        Marshal.InitHandle(handle, rawStream);

        return new AudioOutputStream(handle, marshaller);
    }

    // engine only, buffered mode: nothing managed runs on the render thread
    internal static unsafe AudioOutputStream OpenBuffered(
        AudioEngineHandle engine,
        AudioDevice? device,
        AudioStreamConfig config)
    {
        NativeStreamConfig nativeConfig = config.ToNative();
        IntPtr deviceNamePtr = device is not null ? Marshal.StringToCoTaskMemUTF8(device.Name) : IntPtr.Zero;

        int code;
        IntPtr rawStream;

        try
        {
            code = OwnAudioNative.ownaudio_v1_open_output_stream_ex(
                engine.DangerousGetHandle(),
                deviceNamePtr,
                in nativeConfig,
                IntPtr.Zero,
                IntPtr.Zero,
                (uint)config.RenderRingFrames,
                out rawStream);
        }
        finally
        {
            if (deviceNamePtr != IntPtr.Zero) Marshal.FreeCoTaskMem(deviceNamePtr);
        }

        ErrorCodeMapper.ThrowIfError(code, nameof(OpenBuffered));

        var handle = new AudioOutputStreamHandle();
        Marshal.InitHandle(handle, rawStream);

        return new AudioOutputStream(handle, null);
    }

    /// <summary>
    /// Pushes interleaved samples into the native render ring, whole frames only. Returns what it
    /// took; anything short means the ring is full, back off and retry. Buffered streams only.
    /// </summary>
    /// <param name="samples"></param>
    /// <returns>Sample count actually queued.</returns>
    public unsafe int Write(ReadOnlySpan<float> samples)
    {
        Guard.NotDisposed(_disposed, nameof(AudioOutputStream));

        if (samples.IsEmpty) return 0;

        fixed (float* _src = samples)
        {
            int code = OwnAudioNative.ownaudio_v1_output_stream_write(
                _handle.DangerousGetHandle(), _src, (nuint)samples.Length, out nuint _written);
            ErrorCodeMapper.ThrowIfError(code, nameof(Write));

            return (int)_written;
        }
    }

    /// <summary>
    /// Drops whatever is queued for playback. The render callback honours it on its next run,
    /// so a stop doesn't leave stale audio to replay on the next start.
    /// </summary>
    public void Clear()
    {
        Guard.NotDisposed(_disposed, nameof(AudioOutputStream));

        int code = OwnAudioNative.ownaudio_v1_output_stream_clear(_handle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(Clear));
    }

    /// <summary>
    /// Samples queued for playback. Divide by the channel count for the frames of audio still
    /// standing between Write and the DAC.
    /// </summary>
    public int QueuedSamples
    {
        get
        {
            Guard.NotDisposed(_disposed, nameof(AudioOutputStream));

            int code = OwnAudioNative.ownaudio_v1_output_stream_get_queued_samples(
                _handle.DangerousGetHandle(), out nuint _samples);
            ErrorCodeMapper.ThrowIfError(code, nameof(QueuedSamples));

            return (int)_samples;
        }
    }

    /// <summary>
    /// Render ring depth in frames, what the engine settled on after clamping our request.
    /// 0 on a callback mode stream, there is no ring there.
    /// </summary>
    public int RingFrames
    {
        get
        {
            Guard.NotDisposed(_disposed, nameof(AudioOutputStream));

            int code = OwnAudioNative.ownaudio_v1_output_stream_get_ring_frames(
                _handle.DangerousGetHandle(), out uint _frames);
            ErrorCodeMapper.ThrowIfError(code, nameof(RingFrames));

            return (int)_frames;
        }
    }

    /// <summary>
    /// Frames that came out silent because the ring ran dry. Cumulative, 0 on a callback mode stream.
    /// </summary>
    public ulong UnderrunFrames
    {
        get
        {
            Guard.NotDisposed(_disposed, nameof(AudioOutputStream));

            int code = OwnAudioNative.ownaudio_v1_output_stream_get_underrun_frames(
                _handle.DangerousGetHandle(), out ulong _frames);
            ErrorCodeMapper.ThrowIfError(code, nameof(UnderrunFrames));

            return _frames;
        }
    }

    /// <summary>
    /// DSP load tallies. Cheap enough for a UI timer.
    /// </summary>
    public AudioStreamLoad GetLoad()
    {
        Guard.NotDisposed(_disposed, nameof(AudioOutputStream));

        int code = OwnAudioNative.ownaudio_v1_output_stream_get_load_stats(
            _handle.DangerousGetHandle(), out NativeLoadStats _stats);
        ErrorCodeMapper.ThrowIfError(code, nameof(GetLoad));

        return new AudioStreamLoad
        {
            BlockCount = _stats.BlockCount,
            PeakBlock = TimeSpan.FromTicks((long)(_stats.PeakBlockNs / 100)),
            AverageBlock = TimeSpan.FromTicks((long)(_stats.AverageBlockNs / 100)),
            UnderrunFrames = _stats.UnderrunFrames,
            AverageLoad = _stats.AverageLoad,
            PeakLoad = _stats.PeakLoad
        };
    }

    /// <summary>
    /// Zeroes the load tallies, underruns stay. Do it once playback has settled.
    /// </summary>
    public void ResetLoad()
    {
        Guard.NotDisposed(_disposed, nameof(AudioOutputStream));

        int code = OwnAudioNative.ownaudio_v1_output_stream_reset_load_stats(_handle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(ResetLoad));
    }

    // engine only, the mixer fills every buffer on the audio thread, zero per buffer pinvoke
    internal static unsafe AudioOutputStream OpenMixerDriven(
        AudioEngineHandle engine,
        MixerHandle mixer,
        AudioDevice? device,
        AudioStreamConfig config)
    {
        NativeStreamConfig nativeConfig = config.ToNative();
        IntPtr deviceNamePtr = device is not null ? Marshal.StringToCoTaskMemUTF8(device.Name) : IntPtr.Zero;

        int code;
        IntPtr rawStream;

        try
        {
            code = OwnAudioNative.ownaudio_v1_mixer_open_output_stream(
                engine.DangerousGetHandle(),
                mixer.DangerousGetHandle(),
                deviceNamePtr,
                in nativeConfig,
                out rawStream);
        }
        finally
        {
            if (deviceNamePtr != IntPtr.Zero) Marshal.FreeCoTaskMem(deviceNamePtr);
        }

        ErrorCodeMapper.ThrowIfError(code, nameof(OpenMixerDriven));

        var handle = new AudioOutputStreamHandle();
        Marshal.InitHandle(handle, rawStream);

        return new AudioOutputStream(handle, marshaller: null);
    }

    /// <summary>
    /// Starts or resumes playback, the callback begins firing on the rt thread.
    /// </summary>
    public void Play()
    {
        Guard.NotDisposed(_disposed, nameof(AudioOutputStream));

        int code = OwnAudioNative.ownaudio_v1_output_stream_play(_handle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(Play));
    }

    /// <summary>
    /// Stops the callback but keeps the stream alive, Play picks it up again.
    /// </summary>
    public void Pause()
    {
        Guard.NotDisposed(_disposed, nameof(AudioOutputStream));

        int code = OwnAudioNative.ownaudio_v1_output_stream_pause(_handle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(Pause));
    }

    /// <summary>
    /// Reads the error state the backend records into a lock free slot on device loss and
    /// friends, without poking the audio thread. errorCount is a monotonic total since open,
    /// compare it against the last seen value to catch a fresh error when the kind repeats.
    /// </summary>
    public AudioStreamErrorKind PollErrorState(out ulong errorCount)
    {
        Guard.NotDisposed(_disposed, nameof(AudioOutputStream));

        int code = OwnAudioNative.ownaudio_v1_output_stream_get_error_state(
            _handle.DangerousGetHandle(), out uint kind, out errorCount);
        ErrorCodeMapper.ThrowIfError(code, nameof(PollErrorState));

        return (AudioStreamErrorKind)kind;
    }

    /// <summary>
    /// How far ahead of the DAC the callback runs, in frames. 0 before the first buffer plays.
    /// </summary>
    public uint LatencyFrames
    {
        get
        {
            Guard.NotDisposed(_disposed, nameof(AudioOutputStream));

            int code = OwnAudioNative.ownaudio_v1_output_stream_get_latency_frames(
                _handle.DangerousGetHandle(), out uint _frames);
            ErrorCodeMapper.ThrowIfError(code, nameof(LatencyFrames));

            return _frames;
        }
    }

    /// <summary>
    /// Native stream goes first so the callback is quiet before we drop the delegate pin.
    /// Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _handle.Dispose();
        _marshaller?.Dispose();
    }
}
