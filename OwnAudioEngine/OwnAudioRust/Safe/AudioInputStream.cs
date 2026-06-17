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
/// Safe wrapper for a native input audio stream opened via <c>ownaudio_v1_open_input_stream</c>.
/// </summary>
/// <remarks>
/// <para>
/// Obtain instances through <see cref="AudioEngine.OpenInputStream"/>.
/// The stream is created in the paused state; call <see cref="Play"/> to start capturing.
/// </para>
/// <para>
/// <b>Thread safety:</b> <see cref="Play"/>, <see cref="Pause"/>, and <see cref="Dispose"/>
/// must not be called concurrently on the same instance.
/// </para>
/// </remarks>
public sealed class AudioInputStream : IDisposable
{
    #region Fields

    private readonly AudioInputStreamHandle _handle;
    private readonly AudioInputCallbackMarshaller _marshaller;
    private bool _disposed;

    #endregion

    #region Events

    /// <summary>
    /// Raised on a <see cref="System.Threading.ThreadPool"/> thread whenever the user-supplied
    /// capture callback throws an unhandled exception.
    /// The exception is swallowed at the FFI boundary so the real-time audio thread is unaffected.
    /// </summary>
    public event EventHandler<Exception>? CallbackError;

    #endregion

    #region Construction

    private AudioInputStream(
        AudioInputStreamHandle handle,
        AudioInputCallbackMarshaller marshaller)
    {
        _handle     = handle;
        _marshaller = marshaller;
        _marshaller.CallbackError += (_, ex) => CallbackError?.Invoke(this, ex);
    }

    /// <summary>
    /// Opens a native input stream. Called exclusively by <see cref="AudioEngine"/>.
    /// </summary>
    internal static unsafe AudioInputStream Open(
        AudioEngineHandle engine,
        AudioDevice? device,
        AudioStreamConfig config,
        AudioInputCallbackHandler callback)
    {
        var marshaller = new AudioInputCallbackMarshaller(callback);

        NativeStreamConfig nativeConfig = config.ToNative();
        IntPtr deviceNamePtr = device is not null
            ? Marshal.StringToCoTaskMemUTF8(device.Name)
            : IntPtr.Zero;

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
            if (deviceNamePtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(deviceNamePtr);
            }
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

    #endregion

    #region Capture control

    /// <summary>
    /// Starts or resumes audio capture on this stream.
    /// The capture callback will begin receiving calls on the real-time thread.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when this stream has been disposed.</exception>
    /// <exception cref="StreamException">Thrown when the native play call fails.</exception>
    public void Play()
    {
        Guard.NotDisposed(_disposed, nameof(AudioInputStream));

        int code = OwnAudioNative.ownaudio_v1_input_stream_play(_handle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(Play));
    }

    /// <summary>
    /// Pauses audio capture without destroying the stream.
    /// The capture callback will stop being called until <see cref="Play"/> is invoked again.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when this stream has been disposed.</exception>
    /// <exception cref="StreamException">Thrown when the native pause call fails.</exception>
    public void Pause()
    {
        Guard.NotDisposed(_disposed, nameof(AudioInputStream));

        int code = OwnAudioNative.ownaudio_v1_input_stream_pause(_handle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(Pause));
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Destroys the native stream and releases the callback delegate pin.
    /// Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Destroy the native stream first so the callback stops firing before the pin is freed.
        _handle.Dispose();
        _marshaller.Dispose();
    }

    #endregion
}
