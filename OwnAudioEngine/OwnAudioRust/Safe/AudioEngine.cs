using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Ownaudio.Audio;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Native.RustAudio.Structs;
using Ownaudio.Safe.Callbacks;
using Ownaudio.Safe.Exceptions;
using Ownaudio.Safe.Handles;
using Ownaudio.Safe.Validation;

namespace Ownaudio.Safe;

/// <summary>
/// Managed door to the rust engine. One instance per process is the sane setup, more of them
/// work too but every one of them owns a separate native context and device cache.
/// Create and Dispose are fine from any thread, the rest is not meant to be called concurrently.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    /// <summary>
    /// Abi version this assembly was built against, has to match
    /// ownaudio_v1_get_abi_version() in the loaded binary. Bump both together.
    /// </summary>
    public const uint ExpectedAbiVersion = 1u;

    private readonly AudioEngineHandle _handle;
    private bool _disposed;

    // raw handle for the native input capture track source (MultiTrackSession.AddInputTrack)
    internal IntPtr NativeHandle => _handle.DangerousGetHandle();

    private AudioEngine(AudioEngineHandle handle)
    {
        _handle = handle;
    }

    #region Create

    /// <summary>
    /// Spins up an engine on the platform default host.
    /// </summary>
    public static AudioEngine Create() => Create(hostApi: null);

    /// <summary>
    /// Same, but on the given host api. Null means platform default: wasapi on windows,
    /// core audio on mac, alsa on linux.
    /// </summary>
    /// <exception cref="AbiVersionMismatchException"></exception>
    /// <exception cref="HostApiNotAvailableException"></exception>
    /// <exception cref="AsioDriverNotFoundException"></exception>
    public static AudioEngine Create(HostApi? hostApi)
    {
        _verifyAbiVersion();

        int code;
        IntPtr rawHandle;

        if (hostApi.HasValue)
            code = OwnAudioNative.ownaudio_v1_engine_create_with_host((NativeHostApi)(int)hostApi.Value, out rawHandle);
        else
            code = OwnAudioNative.ownaudio_v1_engine_create(out rawHandle);

        ErrorCodeMapper.ThrowIfError(code, nameof(Create));

        var handle = new AudioEngineHandle();
        Marshal.InitHandle(handle, rawHandle);

        return new AudioEngine(handle);
    }

    private static void _verifyAbiVersion()
    {
        uint nativeVersion = OwnAudioNative.ownaudio_v1_get_abi_version();
        if (nativeVersion != ExpectedAbiVersion)
            throw new AbiVersionMismatchException(nativeVersion, ExpectedAbiVersion);
    }

    #endregion

    #region Devices

    /// <summary>
    /// Output devices of the host this engine was created with, so an asio engine
    /// lists asio devices and not wasapi endpoints.
    /// </summary>
    public IReadOnlyList<AudioDevice> EnumerateOutputDevices()
    {
        Guard.NotDisposed(_disposed, nameof(AudioEngine));

        int code = OwnAudioNative.ownaudio_v1_engine_list_output_devices(
            _handle.DangerousGetHandle(), out IntPtr devicePtr, out nuint count);
        ErrorCodeMapper.ThrowIfError(code, nameof(EnumerateOutputDevices));

        return _marshalDeviceList(devicePtr, count);
    }

    /// <summary>
    /// Same for the capture side.
    /// </summary>
    public IReadOnlyList<AudioDevice> EnumerateInputDevices()
    {
        Guard.NotDisposed(_disposed, nameof(AudioEngine));

        int code = OwnAudioNative.ownaudio_v1_engine_list_input_devices(
            _handle.DangerousGetHandle(), out IntPtr devicePtr, out nuint count);
        ErrorCodeMapper.ThrowIfError(code, nameof(EnumerateInputDevices));

        return _marshalDeviceList(devicePtr, count);
    }

    private static unsafe IReadOnlyList<AudioDevice> _marshalDeviceList(IntPtr devicePtr, nuint count)
    {
        if (count == 0 || devicePtr == IntPtr.Zero) return Array.Empty<AudioDevice>();

        var devices = new AudioDevice[(int)count];

        try
        {
            NativeDeviceInfo* ptr = (NativeDeviceInfo*)devicePtr;
            for (int i = 0; i < (int)count; i++)
                devices[i] = new AudioDevice(in ptr[i]);
        }
        finally
        {
            OwnAudioNative.ownaudio_v1_free_device_list(devicePtr, count);
        }

        return devices;
    }

    #endregion

    #region Streams

    /// <summary>
    /// Opens an output stream, paused. The callback runs on the rt thread, so no allocation,
    /// no locking, no blocking io in there. Null device means system default.
    /// </summary>
    public AudioOutputStream OpenOutputStream(AudioDevice? device, AudioStreamConfig config, AudioOutputCallbackHandler callback)
    {
        Guard.NotDisposed(_disposed, nameof(AudioEngine));
        Guard.NotNull(config, nameof(config));
        Guard.NotNull(callback, nameof(callback));

        return AudioOutputStream.Open(_handle, device, config, callback);
    }

    /// <summary>
    /// Output stream driven by a native mixer. Rendering happens on the audio thread itself,
    /// no managed callback, the samples never come back to managed memory. The mixer rate and
    /// channel count has to line up with the config.
    /// </summary>
    public AudioOutputStream OpenMixerOutputStream(MixerHandle mixer, AudioDevice? device, AudioStreamConfig config)
    {
        Guard.NotDisposed(_disposed, nameof(AudioEngine));
        Guard.NotNull(mixer, nameof(mixer));
        Guard.NotNull(config, nameof(config));

        return AudioOutputStream.OpenMixerDriven(_handle, mixer, device, config);
    }

    /// <summary>
    /// Opens a capture stream, paused. Same rt rules for the callback as on the output side.
    /// </summary>
    public AudioInputStream OpenInputStream(AudioDevice? device, AudioStreamConfig config, AudioInputCallbackHandler callback)
    {
        Guard.NotDisposed(_disposed, nameof(AudioEngine));
        Guard.NotNull(config, nameof(config));
        Guard.NotNull(callback, nameof(callback));

        return AudioInputStream.Open(_handle, device, config, callback);
    }

    #endregion

    /// <summary>
    /// Tears down the native context. Dispose every stream opened from here first.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _handle.Dispose();
    }
}
