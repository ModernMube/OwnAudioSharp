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
    private readonly AudioInputCallbackMarshaller _marshaller;
    private bool _disposed;

    /// <summary>
    /// Fires on a threadpool thread when the capture callback throws. We swallow it at the ffi
    /// boundary so the rt thread keeps running.
    /// </summary>
    public event EventHandler<Exception>? CallbackError;

    private AudioInputStream(AudioInputStreamHandle handle, AudioInputCallbackMarshaller marshaller)
    {
        _handle     = handle;
        _marshaller = marshaller;
        _marshaller.CallbackError += (_, ex) => CallbackError?.Invoke(this, ex);
    }

    // engine only
    internal static unsafe AudioInputStream Open(
        AudioEngineHandle engine,
        AudioDevice? device,
        AudioStreamConfig config,
        AudioInputCallbackHandler callback)
    {
        var marshaller = new AudioInputCallbackMarshaller(callback);

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
                marshaller.NativeFunctionPointer,
                IntPtr.Zero,
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

        var handle = new AudioInputStreamHandle();
        Marshal.InitHandle(handle, rawStream);

        return new AudioInputStream(handle, marshaller);
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
    /// Native stream goes first so the callback is quiet before we drop the delegate pin.
    /// Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _handle.Dispose();
        _marshaller.Dispose();
    }
}
