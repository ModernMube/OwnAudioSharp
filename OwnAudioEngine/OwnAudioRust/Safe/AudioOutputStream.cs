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

        var handle = new AudioOutputStreamHandle();
        Marshal.InitHandle(handle, rawStream);

        return new AudioOutputStream(handle, marshaller);
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
