using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Native.RustAudio.Structs;
using Ownaudio.Safe.Callbacks;
using Ownaudio.Safe.Exceptions;
using Ownaudio.Safe.Handles;
using Ownaudio.Safe.Validation;

namespace Ownaudio.Safe;

/// <summary>
/// Safe, managed entry point to the native Rust audio engine.
/// </summary>
/// <remarks>
/// <para>
/// One instance per process is recommended; multiple instances are supported but each
/// owns an independent native engine context with its own device enumeration cache.
/// </para>
/// <para>
/// <b>Thread safety:</b> <see cref="Create"/> and <see cref="Dispose"/> are safe to call
/// from any thread.  All other methods must not be called concurrently on the same instance
/// unless otherwise documented.
/// </para>
/// </remarks>
public sealed class AudioEngine : IDisposable
{
    #region Fields

    private readonly AudioEngineHandle _handle;
    private bool _disposed;

    #endregion

    #region Construction

    private AudioEngine(AudioEngineHandle handle)
    {
        _handle = handle;
    }

    /// <summary>
    /// Creates a new <see cref="AudioEngine"/> instance backed by a native engine context.
    /// </summary>
    /// <returns>A new, ready-to-use <see cref="AudioEngine"/>.</returns>
    /// <exception cref="OwnAudioException">Thrown when the native engine fails to initialize.</exception>
    public static AudioEngine Create()
    {
        int code = OwnAudioNative.ownaudio_v1_engine_create(out IntPtr rawHandle);
        ErrorCodeMapper.ThrowIfError(code, nameof(Create));

        var handle = new AudioEngineHandle();

        // Transfer the raw handle into the SafeHandle so the runtime manages its lifetime.
        Marshal.InitHandle(handle, rawHandle);

        return new AudioEngine(handle);
    }

    #endregion

    #region Device enumeration

    /// <summary>
    /// Returns all available output devices reported by the OS audio subsystem.
    /// </summary>
    /// <returns>Read-only list of output devices; never <see langword="null"/>.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when this engine has been disposed.</exception>
    /// <exception cref="DeviceException">Thrown when the native enumeration call fails.</exception>
    public IReadOnlyList<AudioDevice> EnumerateOutputDevices()
    {
        Guard.NotDisposed(_disposed, nameof(AudioEngine));

        int code = OwnAudioNative.ownaudio_v1_list_output_devices(
            out IntPtr devicePtr, out nuint count);
        ErrorCodeMapper.ThrowIfError(code, nameof(EnumerateOutputDevices));

        return MarshalDeviceList(devicePtr, count);
    }

    /// <summary>
    /// Returns all available input devices reported by the OS audio subsystem.
    /// </summary>
    /// <returns>Read-only list of input devices; never <see langword="null"/>.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when this engine has been disposed.</exception>
    /// <exception cref="DeviceException">Thrown when the native enumeration call fails.</exception>
    public IReadOnlyList<AudioDevice> EnumerateInputDevices()
    {
        Guard.NotDisposed(_disposed, nameof(AudioEngine));

        int code = OwnAudioNative.ownaudio_v1_list_input_devices(
            out IntPtr devicePtr, out nuint count);
        ErrorCodeMapper.ThrowIfError(code, nameof(EnumerateInputDevices));

        return MarshalDeviceList(devicePtr, count);
    }

    #endregion

    #region Stream factory

    /// <summary>
    /// Opens an output stream with the given configuration and registers the audio fill callback.
    /// The stream starts in the paused state; call <see cref="AudioOutputStream.Play"/> to begin.
    /// </summary>
    /// <param name="device">
    /// The output device to use, or <see langword="null"/> to use the system default.
    /// </param>
    /// <param name="config">Stream parameters (sample rate, channels, format, buffer size).</param>
    /// <param name="callback">
    /// Called from the real-time audio thread for every buffer cycle.
    /// Must not allocate, lock, or perform blocking I/O.
    /// </param>
    /// <returns>A new <see cref="AudioOutputStream"/> in the paused state.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when this engine has been disposed.</exception>
    /// <exception cref="System.ArgumentNullException">
    /// Thrown when <paramref name="config"/> or <paramref name="callback"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="StreamException">Thrown when the stream cannot be opened.</exception>
    public AudioOutputStream OpenOutputStream(
        AudioDevice? device,
        AudioStreamConfig config,
        AudioOutputCallbackHandler callback)
    {
        Guard.NotDisposed(_disposed, nameof(AudioEngine));
        Guard.NotNull(config, nameof(config));
        Guard.NotNull(callback, nameof(callback));

        return AudioOutputStream.Open(_handle, device, config, callback);
    }

    /// <summary>
    /// Opens an input stream with the given configuration and registers the capture callback.
    /// The stream starts in the paused state; call <see cref="AudioInputStream.Play"/> to begin.
    /// </summary>
    /// <param name="device">
    /// The input device to use, or <see langword="null"/> to use the system default.
    /// </param>
    /// <param name="config">Stream parameters (sample rate, channels, format, buffer size).</param>
    /// <param name="callback">
    /// Called from the real-time audio thread for every captured buffer.
    /// Must not allocate, lock, or perform blocking I/O.
    /// </param>
    /// <returns>A new <see cref="AudioInputStream"/> in the paused state.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when this engine has been disposed.</exception>
    /// <exception cref="System.ArgumentNullException">
    /// Thrown when <paramref name="config"/> or <paramref name="callback"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="StreamException">Thrown when the stream cannot be opened.</exception>
    public AudioInputStream OpenInputStream(
        AudioDevice? device,
        AudioStreamConfig config,
        AudioInputCallbackHandler callback)
    {
        Guard.NotDisposed(_disposed, nameof(AudioEngine));
        Guard.NotNull(config, nameof(config));
        Guard.NotNull(callback, nameof(callback));

        return AudioInputStream.Open(_handle, device, config, callback);
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Destroys the native engine context and releases the underlying handle.
    /// All streams opened from this engine must be disposed before calling this method.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
    }

    #endregion

    #region Private helpers

    private static unsafe IReadOnlyList<AudioDevice> MarshalDeviceList(
        IntPtr devicePtr, nuint count)
    {
        if (count == 0 || devicePtr == IntPtr.Zero)
        {
            return Array.Empty<AudioDevice>();
        }

        var devices = new AudioDevice[(int)count];

        try
        {
            NativeDeviceInfo* ptr = (NativeDeviceInfo*)devicePtr;
            for (int i = 0; i < (int)count; i++)
            {
                devices[i] = new AudioDevice(in ptr[i]);
            }
        }
        finally
        {
            OwnAudioNative.ownaudio_v1_free_device_list(devicePtr, count);
        }

        return devices;
    }

    #endregion
}
