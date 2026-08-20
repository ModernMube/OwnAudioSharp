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
/// Safe wrapper around a native capture stream. You get one from AudioEngine.OpenInputStream,
/// paused, call Play to start. Play/Pause/Dispose are not meant to race each other.
/// </summary>
public sealed class AudioInputStream : IDisposable
{
    private readonly AudioInputStreamHandle _handle;
    private readonly AudioInputCallbackMarshaller? _marshaller;
    private bool _disposed;

    /// <summary>
    /// Fires on a threadpool thread when the capture callback throws. We swallow it at the ffi
    /// boundary so the rt thread keeps running. Never fires on a buffered stream, there is no callback.
    /// </summary>
    public event EventHandler<Exception>? CallbackError;

    /// <summary>
    /// True when the stream was opened without a callback and capture lands in the native ring,
    /// which is the only mode where Read works.
    /// </summary>
    public bool IsBuffered => _marshaller is null;

    private AudioInputStream(AudioInputStreamHandle handle, AudioInputCallbackMarshaller? marshaller)
    {
        _handle     = handle;
        _marshaller = marshaller;

        if (_marshaller is not null)
            _marshaller.CallbackError += (_, ex) => CallbackError?.Invoke(this, ex);
    }

    // engine only
    internal static unsafe AudioInputStream Open(
        AudioEngineHandle engine,
        AudioDevice? device,
        AudioStreamConfig config,
        AudioInputCallbackHandler? callback)
    {
        AudioInputCallbackMarshaller? marshaller = callback is not null
            ? new AudioInputCallbackMarshaller(callback)
            : null;

        NativeStreamConfig nativeConfig = config.ToNative();
        IntPtr deviceNamePtr = device is not null ? Marshal.StringToCoTaskMemUTF8(device.Name) : IntPtr.Zero;

        int code;
        IntPtr rawStream;

        try
        {
            code = OwnAudioNative.ownaudio_v1_open_input_stream(
                engine.DangerousGetHandle(),
                deviceNamePtr,
                in nativeConfig,
                marshaller is not null ? AudioInputCallbackMarshaller.NativeFunctionPointer : IntPtr.Zero,
                marshaller?.UserData ?? IntPtr.Zero,
                out rawStream);
        }
        finally
        {
            if (deviceNamePtr != IntPtr.Zero) Marshal.FreeCoTaskMem(deviceNamePtr);
        }

        if (code != (int)NativeErrorCode.Success)
        {
            marshaller?.Dispose();
            ErrorCodeMapper.ThrowIfError(code, nameof(Open));
        }

        var handle = new AudioInputStreamHandle();
        Marshal.InitHandle(handle, rawStream);

        return new AudioInputStream(handle, marshaller);
    }

    /// <summary>
    /// Pulls captured samples out of the native ring, whole frames only. Returns what it got, 0 when
    /// the ring is empty. Buffered streams only, and it never blocks.
    /// </summary>
    /// <param name="destination"></param>
    /// <returns>Sample count copied into destination.</returns>
    public unsafe int Read(Span<float> destination)
    {
        Guard.NotDisposed(_disposed, nameof(AudioInputStream));

        if (destination.IsEmpty) return 0;

        fixed (float* _dst = destination)
        {
            int code = OwnAudioNative.ownaudio_v1_input_stream_read(
                _handle.DangerousGetHandle(), _dst, (nuint)destination.Length, out nuint _read);
            ErrorCodeMapper.ThrowIfError(code, nameof(Read));

            return (int)_read;
        }
    }

    /// <summary>
    /// Throws away whatever is queued in the native ring, so a restart doesn't open with the
    /// previous take's tail. Meant to be called while capture is paused.
    /// </summary>
    public void Clear()
    {
        Guard.NotDisposed(_disposed, nameof(AudioInputStream));

        int code = OwnAudioNative.ownaudio_v1_input_stream_clear(_handle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(Clear));
    }

    /// <summary>
    /// Capture frames the native ring dropped because nobody read it in time. Cumulative for the
    /// life of the stream, 0 on a callback mode one.
    /// </summary>
    public ulong DroppedFrames
    {
        get
        {
            Guard.NotDisposed(_disposed, nameof(AudioInputStream));

            int code = OwnAudioNative.ownaudio_v1_input_stream_get_dropped_frames(
                _handle.DangerousGetHandle(), out ulong _frames);
            ErrorCodeMapper.ThrowIfError(code, nameof(DroppedFrames));

            return _frames;
        }
    }

    /// <summary>
    /// Starts or resumes capture, the callback begins firing on the rt thread.
    /// </summary>
    public void Play()
    {
        Guard.NotDisposed(_disposed, nameof(AudioInputStream));

        int code = OwnAudioNative.ownaudio_v1_input_stream_play(_handle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(Play));
    }

    /// <summary>
    /// Stops the callback but keeps the stream alive, Play picks it up again.
    /// </summary>
    public void Pause()
    {
        Guard.NotDisposed(_disposed, nameof(AudioInputStream));

        int code = OwnAudioNative.ownaudio_v1_input_stream_pause(_handle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(Pause));
    }

    /// <summary>
    /// How long ago the samples in the buffer hit the ADC, in frames. Subtract it from the capture
    /// position to line a take up with the real timeline. 0 before the first buffer arrives.
    /// </summary>
    public uint LatencyFrames
    {
        get
        {
            Guard.NotDisposed(_disposed, nameof(AudioInputStream));

            int code = OwnAudioNative.ownaudio_v1_input_stream_get_latency_frames(
                _handle.DangerousGetHandle(), out uint _frames);
            ErrorCodeMapper.ThrowIfError(code, nameof(LatencyFrames));

            return _frames;
        }
    }

    /// <summary>
    /// Frames the device delivered on the last capture callback — what the driver granted,
    /// not what we asked for. 0 until audio has actually run.
    /// </summary>
    public int CallbackFrames
    {
        get
        {
            Guard.NotDisposed(_disposed, nameof(AudioInputStream));

            int code = OwnAudioNative.ownaudio_v1_input_stream_get_callback_frames(
                _handle.DangerousGetHandle(), out uint _frames);
            ErrorCodeMapper.ThrowIfError(code, nameof(CallbackFrames));

            return (int)_frames;
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
