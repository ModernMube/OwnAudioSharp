using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Ownaudio.Native.Utils;
using Logger;

namespace Ownaudio.Native.MiniAudio;

/// <summary>
/// Provides wrapper functions and bindings for accessing the native
/// MiniAudio shared library APIs on all supported platforms.
/// </summary>
internal static unsafe partial class MaBinding
{
    /// <summary>
    /// Thread synchronization lock object used to guard the initialization sequence.
    /// Ensures that library binding is performed thread-safely and only once.
    /// </summary>
    private static readonly object _initLock = new object();

    /// <summary>
    /// The library loader instance used to load the native shared library.
    /// Stored to keep the handle open for the duration of the application.
    /// </summary>
    private static LibraryLoader? _libraryLoader;

    /// <summary>
    /// Indicates whether the MiniAudio bindings have already been initialized.
    /// Helps prevent duplicate loader queries and redundant exports.
    /// </summary>
    private static bool _isInitialized = false;

    /// <summary>
    /// Initializes all MiniAudio function pointers by loading their corresponding
    /// exported symbols from the provided library loader.
    /// </summary>
    /// <param name="loader">The library loader containing the loaded native library handle.</param>
    public static void InitializeBindings(LibraryLoader loader)
    {
        if (_isInitialized)
            return;

        lock (_initLock)
        {
            if (_isInitialized)
                return;

            _libraryLoader = loader;

            _contextInit = (delegate* unmanaged[Cdecl]<nint, uint, nint, nint, MaResult>)
                loader.GetExport("ma_context_init");
            _contextUninit = (delegate* unmanaged[Cdecl]<IntPtr, void>)
                loader.GetExport("ma_context_uninit");
            _contextGetDeviceInfo = (delegate* unmanaged[Cdecl]<IntPtr, MaDeviceType, IntPtr, IntPtr, MaResult>)
                loader.GetExport("ma_context_get_device_info");
            _contextGetDevices = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, int*, IntPtr*, int*, MaResult>)
                loader.GetExport("ma_context_get_devices");
            _contextEnumerateDevices = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, MaResult>)
                loader.GetExport("ma_context_enumerate_devices");

            _deviceInit = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, MaResult>)
                loader.GetExport("ma_device_init");
            _deviceUninit = (delegate* unmanaged[Cdecl]<IntPtr, void>)
                loader.GetExport("ma_device_uninit");
            _deviceStart = (delegate* unmanaged[Cdecl]<IntPtr, MaResult>)
                loader.GetExport("ma_device_start");
            _deviceStop = (delegate* unmanaged[Cdecl]<IntPtr, MaResult>)
                loader.GetExport("ma_device_stop");
            _deviceIsStarted = (delegate* unmanaged[Cdecl]<IntPtr, int>)
                loader.GetExport("ma_device_is_started");
            _deviceGetInfo = (delegate* unmanaged[Cdecl]<IntPtr, MaDeviceType, IntPtr, MaResult>)
                loader.GetExport("ma_device_get_info");
            _deviceGetContext = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)
                loader.GetExport("ma_device_get_context");
            _getDevices = (delegate* unmanaged[Cdecl]<IntPtr, out IntPtr, out IntPtr, out IntPtr, out IntPtr, MaResult>)
                loader.GetExport("sf_get_devices");

            _engineInit = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, MaResult>)
                loader.GetExport("ma_engine_init");
            _engineUninit = (delegate* unmanaged[Cdecl]<IntPtr, void>)
                loader.GetExport("ma_engine_uninit");
            _enginePlay = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, MaResult>)
                loader.GetExport("ma_engine_play_sound");

            _soundInit = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, IntPtr, IntPtr, IntPtr, MaResult>)
                loader.GetExport("ma_sound_init_from_file");
            _soundUninit = (delegate* unmanaged[Cdecl]<IntPtr, void>)
                loader.GetExport("ma_sound_uninit");
            _soundStart = (delegate* unmanaged[Cdecl]<IntPtr, MaResult>)
                loader.GetExport("ma_sound_start");
            _soundStop = (delegate* unmanaged[Cdecl]<IntPtr, MaResult>)
                loader.GetExport("ma_sound_stop");
            _soundIsPlaying = (delegate* unmanaged[Cdecl]<IntPtr, int>)
                loader.GetExport("ma_sound_is_playing");
            _soundSetVolume = (delegate* unmanaged[Cdecl]<IntPtr, float, void>)
                loader.GetExport("ma_sound_set_volume");

            _decoderInit = (delegate* unmanaged[Cdecl]<IntPtr, ref MaDecoderConfig, IntPtr, MaResult>)
                loader.GetExport("ma_decoder_init_file");
            _decoderUninit = (delegate* unmanaged[Cdecl]<IntPtr, void>)
                loader.GetExport("ma_decoder_uninit");
            _decoderRead = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, ulong, out ulong, MaResult>)
                loader.GetExport("ma_decoder_read_pcm_frames");
            _decoderSeek = (delegate* unmanaged[Cdecl]<IntPtr, ulong, MaResult>)
                loader.GetExport("ma_decoder_seek_to_pcm_frame");
            _decoderGetLength = (delegate* unmanaged[Cdecl]<IntPtr, out ulong, MaResult>)
                loader.GetExport("ma_decoder_get_length_in_pcm_frames");
            _decoderGetFormat = (delegate* unmanaged[Cdecl]<IntPtr, ref MaFormat, ref uint, ref uint, IntPtr, ulong, MaResult>)
                loader.GetExport("ma_decoder_get_data_format");
            _decoderGetCursorPosition = (delegate* unmanaged[Cdecl]<IntPtr, ref ulong, MaResult>)
                loader.GetExport("ma_decoder_get_cursor_in_pcm_frames");
            _decoderConfigInit = (delegate* unmanaged[Cdecl]<MaFormat, uint, uint, MaDecoderConfig>)
                loader.GetExport("ma_decoder_config_init");
            _decoderInitStream = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, MaResult>)
                loader.GetExport("ma_decoder_init");

            _encoderInit = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, MaResult>)
                loader.GetExport("ma_encoder_init_file");
            _encoderUninit = (delegate* unmanaged[Cdecl]<IntPtr, void>)
                loader.GetExport("ma_encoder_uninit");
            _encoderWrite = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, ulong, out ulong, MaResult>)
                loader.GetExport("ma_encoder_write_pcm_frames");

            _allocateDecoder = (delegate* unmanaged[Cdecl]<IntPtr>)
                loader.GetExport("sf_allocate_decoder");
            _allocateDecoderConfig = (delegate* unmanaged[Cdecl]<MaFormat, uint, uint, IntPtr>)
                loader.GetExport("sf_allocate_decoder_config");

            _resamplerInit = (delegate* unmanaged[Cdecl]<ref MaResamplerConfig, IntPtr, MaResult>)
                loader.GetExport("ma_resampler_init");
            _resamplerUninit = (delegate* unmanaged[Cdecl]<IntPtr, void>)
                loader.GetExport("ma_resampler_uninit");
            _resamplerProcess = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, ref ulong, IntPtr, ref ulong, MaResult>)
                loader.GetExport("ma_resampler_process_pcm_frames");
            _resamplerSetRate = (delegate* unmanaged[Cdecl]<IntPtr, uint, uint, MaResult>)
                loader.GetExport("ma_resampler_set_rate");
            _resamplerConfigInit = (delegate* unmanaged[Cdecl]<MaFormat, uint, uint, uint, MaResampleAlgorithm, MaResamplerConfig>)
                loader.GetExport("ma_resampler_config_init");

            _maMalloc = (delegate* unmanaged[Cdecl]<ulong, IntPtr, IntPtr>)
                loader.GetExport("ma_malloc");
            _maFree = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)
                loader.GetExport("ma_free");
            _free = (delegate* unmanaged[Cdecl]<IntPtr, void>)
                loader.GetExport("ma_free");
            _maDeviceConfigInit = (delegate* unmanaged[Cdecl]<MaDeviceType, MaDeviceConfig>)
                loader.GetExport("ma_device_config_init");

            _getErrorString = (delegate* unmanaged[Cdecl]<MaResult, IntPtr>)
                loader.GetExport("ma_result_description");

            _isInitialized = true;
        }
    }

    /// <summary>
    /// Ensures that the MiniAudio bindings have been successfully initialized.
    /// If not yet initialized, creates a default library loader and triggers binding.
    /// </summary>
    public static void EnsureInitialized()
    {
        if (_isInitialized)
            return;

        var loader = new LibraryLoader("libminiaudio");
        InitializeBindings(loader);
    }

    #region Context Operations

    /// <summary>
    /// Initializes a MiniAudio context with the specified configuration and backends.
    /// Translates the C# backend array parameters to unmanaged layout.
    /// </summary>
    /// <param name="backends">An array of desired audio backends to search, or null.</param>
    /// <param name="backendCount">The number of backends provided in the array.</param>
    /// <param name="contextConfig">Pointer to custom context configuration settings.</param>
    /// <param name="context">Pointer to the destination context structure to initialize.</param>
    /// <returns>Result code indicating success or specific initialization failure.</returns>
    public static MaResult ma_context_init(MaBackend[]? backends, uint backendCount, IntPtr contextConfig, IntPtr context)
    {
        if (_contextInit == null)
            return MaResult.MA_NOT_IMPLEMENTED;

        if (backends == null || backends.Length == 0)
        {
            return _contextInit(IntPtr.Zero, 0, contextConfig, context);
        }

        fixed (MaBackend* pBackends = backends)
        {
            return _contextInit((nint)pBackends, backendCount, contextConfig, context);
        }
    }

    /// <summary>
    /// Uninitializes a MiniAudio context and releases all allocated platform resources.
    /// The context pointer becomes invalid after this call completes.
    /// </summary>
    /// <param name="context">Pointer to the active context structure to release.</param>
    public static void ma_context_uninit(IntPtr context)
    {
        if (_contextUninit != null)
            _contextUninit(context);
    }

    /// <summary>
    /// Queries the capabilities and info for a specific device identifier under a context.
    /// The details are stored in the provided unmanaged deviceInfo structure.
    /// </summary>
    /// <param name="context">Pointer to the active context structure.</param>
    /// <param name="deviceType">The type of the device to query (playback or capture).</param>
    /// <param name="deviceId">Pointer to the unique device identifier to look up.</param>
    /// <param name="deviceInfo">Pointer to the destination structure to write results into.</param>
    /// <returns>Result code indicating query success or device lookup failure.</returns>
    public static MaResult ma_context_get_device_info(IntPtr context, MaDeviceType deviceType, IntPtr deviceId, IntPtr deviceInfo)
    {
        if (_contextGetDeviceInfo == null)
            return MaResult.MA_NOT_IMPLEMENTED;

        return _contextGetDeviceInfo(context, deviceType, deviceId, deviceInfo);
    }

    /// <summary>
    /// Queries lists of connected audio devices from the context in a single call.
    /// Writes output pointers and device counts directly to the callers' parameters.
    /// </summary>
    /// <param name="context">Pointer to the active context structure.</param>
    /// <param name="pPlaybackDevices">Output parameter to receive the playback device array pointer.</param>
    /// <param name="pCaptureDevices">Output parameter to receive the capture device array pointer.</param>
    /// <param name="playbackDeviceCount">Output parameter to receive the playback device count.</param>
    /// <param name="captureDeviceCount">Output parameter to receive the capture device count.</param>
    /// <returns>Result code indicating query success or hardware failure.</returns>
    public static MaResult ma_context_get_devices(IntPtr context, out IntPtr pPlaybackDevices, out IntPtr pCaptureDevices, out int playbackDeviceCount, out int captureDeviceCount)
    {
        pPlaybackDevices = IntPtr.Zero;
        pCaptureDevices = IntPtr.Zero;
        playbackDeviceCount = 0;
        captureDeviceCount = 0;

        if (_contextGetDevices == null)
            return MaResult.MA_NOT_IMPLEMENTED;

        fixed (IntPtr* pPlaybackDevicesPtr = &pPlaybackDevices)
        {
            fixed (IntPtr* pCaptureDevicesPtr = &pCaptureDevices)
            {
                fixed (int* playbackDeviceCountPtr = &playbackDeviceCount)
                {
                    fixed (int* captureDeviceCountPtr = &captureDeviceCount)
                    {
                        return _contextGetDevices(context, pPlaybackDevicesPtr, playbackDeviceCountPtr, pCaptureDevicesPtr, captureDeviceCountPtr);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Enumerates all connected audio devices in the context using a callback function.
    /// Triggers the callback repeatedly until all devices are handled.
    /// </summary>
    /// <param name="context">Pointer to the active context structure.</param>
    /// <param name="callback">The enumeration callback function pointer.</param>
    /// <param name="pUserData">User-defined context data pointer passed to the callback.</param>
    /// <returns>Result code indicating enumeration completion or error.</returns>
    public static MaResult ma_context_enumerate_devices(IntPtr context, MaEnumDevicesCallback callback, IntPtr pUserData)
    {
        if (_contextEnumerateDevices == null)
            return MaResult.MA_NOT_IMPLEMENTED;

        IntPtr cb = callback != null
            ? Marshal.GetFunctionPointerForDelegate(callback)
            : IntPtr.Zero;

        return _contextEnumerateDevices(context, cb, pUserData);
    }

    /// <summary>
    /// Queries registered devices from the context via the custom wrapper.
    /// Throws NotSupportedException if the native endpoint export is missing.
    /// </summary>
    /// <param name="context">Pointer to the active context structure.</param>
    /// <param name="pPlaybackDevices">Output parameter to receive the playback device array pointer.</param>
    /// <param name="pCaptureDevices">Output parameter to receive the capture device array pointer.</param>
    /// <param name="playbackDeviceCount">Output parameter to receive the playback device count.</param>
    /// <param name="captureDeviceCount">Output parameter to receive the capture device count.</param>
    /// <returns>Result code indicating query success or native backend error.</returns>
    public static MaResult sf_get_devices(IntPtr context, out IntPtr pPlaybackDevices, out IntPtr pCaptureDevices, out IntPtr playbackDeviceCount, out IntPtr captureDeviceCount)
    {
        pPlaybackDevices = IntPtr.Zero;
        pCaptureDevices = IntPtr.Zero;
        playbackDeviceCount = IntPtr.Zero;
        captureDeviceCount = IntPtr.Zero;

        if (_getDevices == null)
            throw new NotSupportedException("Getting devices list is not supported.");

        return _getDevices(context, out pPlaybackDevices, out pCaptureDevices, out playbackDeviceCount, out captureDeviceCount);
    }

    /// <summary>
    /// Allocates unmanaged memory for a context structure and zeroes it out.
    /// Helper helper method that streamlines native allocation.
    /// </summary>
    /// <returns>Pointer to the zero-initialized context structure.</returns>
    public static IntPtr allocate_context()
    {
        ulong size = (ulong)Marshal.SizeOf<MaContext>();
        IntPtr ptr = ma_malloc(size, IntPtr.Zero);

        if (ptr != IntPtr.Zero)
            ZeroMemory(ptr, size);

        return ptr;
    }

    #endregion

    #region Device Operations

    /// <summary>
    /// Initializes an audio device structure under the specified context.
    /// Connects the device to platform hardware based on config parameters.
    /// </summary>
    /// <param name="context">Pointer to the parent context structure, or zero.</param>
    /// <param name="config">Pointer to the device configuration parameters.</param>
    /// <param name="device">Pointer to the destination device structure to initialize.</param>
    /// <returns>Result code indicating initialization success or hardware error.</returns>
    public static MaResult ma_device_init(IntPtr context, IntPtr config, IntPtr device)
    {
        if (_deviceInit == null)
            return MaResult.MA_NOT_IMPLEMENTED;

        return _deviceInit(context, config, device);
    }

    /// <summary>
    /// Uninitializes an audio device structure and releases its connection.
    /// Stops the device first if it is still running.
    /// </summary>
    /// <param name="device">Pointer to the active device structure to release.</param>
    public static void ma_device_uninit(IntPtr device)
    {
        if (_deviceUninit != null)
            _deviceUninit(device);
    }

    /// <summary>
    /// Starts the audio data processing thread for the specified device.
    /// Triggers the registered data callbacks to process real-time audio.
    /// </summary>
    /// <param name="device">Pointer to the active device structure.</param>
    /// <returns>Result code indicating start success or stream error.</returns>
    public static MaResult ma_device_start(IntPtr device)
    {
        if (_deviceStart == null)
            return MaResult.MA_NOT_IMPLEMENTED;

        return _deviceStart(device);
    }

    /// <summary>
    /// Stops the audio data processing thread for the specified device.
    /// Pauses callback invocations and freezes audio flow.
    /// </summary>
    /// <param name="device">Pointer to the active device structure.</param>
    /// <returns>Result code indicating stop success or stream error.</returns>
    public static MaResult ma_device_stop(IntPtr device)
    {
        if (_deviceStop == null)
            return MaResult.MA_NOT_IMPLEMENTED;

        return _deviceStop(device);
    }

    /// <summary>
    /// Checks whether the audio device data processing loop is started.
    /// Returns true if active and running, otherwise false.
    /// </summary>
    /// <param name="device">Pointer to the active device structure to inspect.</param>
    /// <returns>True if the device loop is active, otherwise false.</returns>
    public static bool ma_device_is_started(IntPtr device)
    {
        return _deviceIsStarted != null ? _deviceIsStarted(device) != 0 : false;
    }

    /// <summary>
    /// Queries the current configuration parameters of an active device.
    /// Writes the result data to the provided deviceInfo pointer destination.
    /// </summary>
    /// <param name="device">Pointer to the active device structure to query.</param>
    /// <param name="type">The configuration type to retrieve.</param>
    /// <param name="deviceInfo">Pointer to the destination Cap structure to write into.</param>
    /// <returns>Result code indicating query success or device error.</returns>
    public static MaResult ma_device_get_info(IntPtr device, MaDeviceType type, IntPtr deviceInfo)
    {
        if (_deviceGetInfo == null)
            return MaResult.MA_NOT_IMPLEMENTED;

        return _deviceGetInfo(device, type, deviceInfo);
    }

    /// <summary>
    /// Retrieves a pointer to the context that owns the specified device.
    /// Returns IntPtr.Zero if the device was initialized without a context.
    /// </summary>
    /// <param name="device">Pointer to the active device structure to inspect.</param>
    /// <returns>Pointer to the parent context structure, or zero.</returns>
    public static IntPtr ma_device_get_context(IntPtr device)
    {
        return _deviceGetContext != null ? _deviceGetContext(device) : IntPtr.Zero;
    }

    /// <summary>
    /// Initializes a device configuration structure with default parameters.
    /// Prepares parameters for playback and capture settings.
    /// </summary>
    /// <param name="deviceType">The target device type (playback, capture, or duplex).</param>
    /// <returns>A default-initialized device configuration structure.</returns>
    public static MaDeviceConfig ma_device_config_init(MaDeviceType deviceType)
    {
        return _maDeviceConfigInit != null ? _maDeviceConfigInit(deviceType) : default;
    }

    /// <summary>
    /// Allocates unmanaged memory for a device config and populates it.
    /// Configures layout formats, sample rates, and callback functions.
    /// </summary>
    /// <param name="deviceType">The target device type (playback, capture, or duplex).</param>
    /// <param name="format">The audio format mapping (typically f32 or s16).</param>
    /// <param name="channels">The logical channel count (typically 2 for stereo).</param>
    /// <param name="sampleRate">The target sample rate in Hertz.</param>
    /// <param name="dataCallback">The callback function invoked to process audio samples.</param>
    /// <param name="playbackDeviceId">Pointer to specific hardware playback device, or zero.</param>
    /// <param name="captureDeviceId">Pointer to specific hardware capture device, or zero.</param>
    /// <param name="sizeinframe">The desired period buffer size in audio frames.</param>
    /// <returns>Pointer to the allocated and initialized device configuration struct.</returns>
    public static IntPtr ma_device_config_alloc(MaDeviceType deviceType, MaFormat format, uint channels, uint sampleRate,
                                           MaDataCallback dataCallback, IntPtr playbackDeviceId, IntPtr captureDeviceId, uint sizeinframe)
    {
        MaDeviceConfig config = ma_device_config_init(deviceType);

        config.sampleRate = sampleRate;
        config.playback.format = format;
        config.playback.channels = channels;
        config.capture.format = format;
        config.capture.channels = channels;
        config.dataCallback = dataCallback;
        config.playback.pDeviceID = playbackDeviceId;
        config.capture.pDeviceID = captureDeviceId;
        config.periodSizeInFrames = sizeinframe;

        IntPtr ptr = ma_malloc((ulong)Marshal.SizeOf<MaDeviceConfig>(), IntPtr.Zero);
        if (ptr != IntPtr.Zero)
        {
            Marshal.StructureToPtr(config, ptr, false);
        }

        return ptr;
    }

    /// <summary>
    /// Allocates unmanaged memory for a device structure and zeroes it out.
    /// Helps prepare memory layouts before triggering device initialization.
    /// </summary>
    /// <returns>Pointer to the zero-initialized device structure.</returns>
    public static IntPtr allocate_device()
    {
        ulong size = (ulong)Marshal.SizeOf<MaDevice>();
        IntPtr ptr = ma_malloc(size, IntPtr.Zero);

        if (ptr != IntPtr.Zero)
            ZeroMemory(ptr, size);

        return ptr;
    }

    /// <summary>
    /// Allocates and initializes device configuration using default parameters.
    /// Wraps device config allocation with optional default settings.
    /// </summary>
    /// <param name="deviceType">The target device type (playback, capture, or duplex).</param>
    /// <param name="format">The audio format mapping (typically f32 or s16).</param>
    /// <param name="channels">The logical channel count (typically 2 for stereo).</param>
    /// <param name="sampleRate">The target sample rate in Hertz.</param>
    /// <param name="dataCallback">The callback function invoked to process audio samples.</param>
    /// <param name="playbackDeviceId">Pointer to specific hardware playback device, or zero.</param>
    /// <param name="captureDeviceId">Pointer to specific hardware capture device, or zero.</param>
    /// <param name="sizeinframe">The desired period buffer size in audio frames.</param>
    /// <returns>Pointer to the allocated device configuration structure.</returns>
    public static IntPtr allocate_device_config(MaDeviceType deviceType, MaFormat format, uint channels, uint sampleRate,
                                           MaDataCallback dataCallback, IntPtr playbackDeviceId, IntPtr captureDeviceId, uint sizeinframe = 512)
    {
        return ma_device_config_alloc(deviceType, format, channels, sampleRate, dataCallback, playbackDeviceId, captureDeviceId, sizeinframe);
    }

    #endregion

    #region Engine Operations

    /// <summary>
    /// Initializes the high-level MiniAudio engine with configuration settings.
    /// Prepares the master graph mixer and internal node systems.
    /// </summary>
    /// <param name="engineConfig">Pointer to custom engine configuration parameters, or zero.</param>
    /// <param name="engine">Pointer to the destination engine structure to initialize.</param>
    /// <returns>Result code indicating initialization success or engine error.</returns>
    public static MaResult ma_engine_init(IntPtr engineConfig, IntPtr engine)
    {
        if (_engineInit == null)
            return MaResult.MA_NOT_IMPLEMENTED;

        return _engineInit(engineConfig, engine);
    }

    /// <summary>
    /// Uninitializes the high-level MiniAudio engine and releases its mixer graph.
    /// Stops all active sound resources owned by this engine instance.
    /// </summary>
    /// <param name="engine">Pointer to the active engine structure to release.</param>
    public static void ma_engine_uninit(IntPtr engine)
    {
        if (_engineUninit != null)
            _engineUninit(engine);
    }

    /// <summary>
    /// Plays an audio file directly through the engine in fire-and-forget mode.
    /// Automatically marshals the string file path to unmanaged memory.
    /// </summary>
    /// <param name="engine">Pointer to the active engine structure.</param>
    /// <param name="filePath">The path of the source audio file on disk.</param>
    /// <param name="pGroup">Pointer to an engine sound group, or zero.</param>
    /// <returns>Result code indicating playback start success or file error.</returns>
    public static MaResult ma_engine_play_sound(IntPtr engine, string filePath, IntPtr pGroup)
    {
        if (_enginePlay == null)
            return MaResult.MA_NOT_IMPLEMENTED;

        IntPtr filePathPtr = Marshal.StringToHGlobalAnsi(filePath);
        try
        {
            return _enginePlay(engine, filePathPtr, pGroup);
        }
        finally
        {
            Marshal.FreeHGlobal(filePathPtr);
        }
    }

    #endregion

    #region Sound Operations

    /// <summary>
    /// Initializes a sound instance from a specified audio file path.
    /// Automatically marshals the string file path to unmanaged memory.
    /// </summary>
    /// <param name="engine">Pointer to the parent engine structure.</param>
    /// <param name="filePath">The path of the source audio file on disk.</param>
    /// <param name="flags">Flags controlling streaming and effect options.</param>
    /// <param name="pGroup">Pointer to an engine sound group, or zero.</param>
    /// <param name="pAllocationCallbacks">Pointer to custom allocation callbacks, or zero.</param>
    /// <param name="pSound">Pointer to the destination sound structure to initialize.</param>
    /// <returns>Result code indicating initialization success or file error.</returns>
    public static MaResult ma_sound_init_from_file(IntPtr engine, string filePath, uint flags, IntPtr pGroup, IntPtr pAllocationCallbacks, IntPtr pSound)
    {
        if (_soundInit == null)
            return MaResult.MA_NOT_IMPLEMENTED;

        IntPtr filePathPtr = Marshal.StringToHGlobalAnsi(filePath);
        try
        {
            return _soundInit(engine, filePathPtr, flags, pGroup, pAllocationCallbacks, pSound);
        }
        finally
        {
            Marshal.FreeHGlobal(filePathPtr);
        }
    }

    /// <summary>
    /// Uninitializes a sound instance and releases its decoded file streams.
    /// Stops the sound first if it is currently playing.
    /// </summary>
    /// <param name="sound">Pointer to the active sound structure to release.</param>
    public static void ma_sound_uninit(IntPtr sound)
    {
        if (_soundUninit != null)
            _soundUninit(sound);
    }

    /// <summary>
    /// Starts playback of the sound instance through the engine graph.
    /// Immediately triggers audio processing on the sound's mixer node.
    /// </summary>
    /// <param name="sound">Pointer to the active sound structure to play.</param>
    /// <returns>Result code indicating play success or stream error.</returns>
    public static MaResult ma_sound_start(IntPtr sound)
    {
        if (_soundStart == null)
            return MaResult.MA_NOT_IMPLEMENTED;

        return _soundStart(sound);
    }

    /// <summary>
    /// Stops playback of the sound instance in the engine graph.
    /// Freezes the sound's cursor position without releasing resources.
    /// </summary>
    /// <param name="sound">Pointer to the active sound structure to pause.</param>
    /// <returns>Result code indicating stop success or stream error.</returns>
    public static MaResult ma_sound_stop(IntPtr sound)
    {
        if (_soundStop == null)
            return MaResult.MA_NOT_IMPLEMENTED;

        return _soundStop(sound);
    }

    /// <summary>
    /// Checks whether the sound instance is currently playing.
    /// Returns true if audio is actively processed, otherwise false.
    /// </summary>
    /// <param name="sound">Pointer to the active sound structure to inspect.</param>
    /// <returns>True if the sound is playing, otherwise false.</returns>
    public static bool ma_sound_is_playing(IntPtr sound)
    {
        return _soundIsPlaying != null ? _soundIsPlaying(sound) != 0 : false;
    }

    /// <summary>
    /// Adjusts the volume multiplier of a sound instance.
    /// Value is typically in the range [0.0, 1.0] but can exceed 1.0.
    /// </summary>
    /// <param name="sound">Pointer to the active sound structure.</param>
    /// <param name="volume">The new volume multiplier value.</param>
    public static void ma_sound_set_volume(IntPtr sound, float volume)
    {
        if (_soundSetVolume != null)
            _soundSetVolume(sound, volume);
    }

    #endregion

    #region Decoder Operations

    /// <summary>
    /// Initializes a file-based decoder with custom config settings.
    /// Automatically marshals the string file path to unmanaged memory.
    /// </summary>
    /// <param name="filePath">The path of the source audio file to decode.</param>
    /// <param name="config">Reference to the decoder configuration structure parameters.</param>
    /// <param name="decoder">Pointer to the destination decoder structure to initialize.</param>
    /// <returns>Result code indicating initialization success or file error.</returns>
    public static MaResult ma_decoder_init_file(string filePath, ref MaDecoderConfig config, IntPtr decoder)
    {
        if (_decoderInit == null)
            return MaResult.MA_NOT_IMPLEMENTED;

        IntPtr filePathPtr = Marshal.StringToHGlobalAnsi(filePath);
        try
        {
            return _decoderInit(filePathPtr, ref config, decoder);
        }
        finally
        {
            Marshal.FreeHGlobal(filePathPtr);
        }
    }

    /// <summary>
    /// Initializes a file-based decoder with default config settings.
    /// Automatically marshals the string file path to unmanaged memory.
    /// </summary>
    /// <param name="filePath">The path of the source audio file to decode.</param>
    /// <param name="nullConfig">Must be IntPtr.Zero to request default configuration.</param>
    /// <param name="decoder">Pointer to the destination decoder structure to initialize.</param>
    /// <returns>Result code indicating initialization success or file error.</returns>
    /// <exception cref="ArgumentException">Thrown when nullConfig is not IntPtr.Zero.</exception>
    public static MaResult ma_decoder_init_file(string filePath, IntPtr nullConfig, IntPtr decoder)
    {
        if (nullConfig != IntPtr.Zero)
        {
            throw new ArgumentException("A nullConfig paraméternek IntPtr.Zero-nak kell lennie");
        }

        if (_decoderInit == null)
            return MaResult.MA_NOT_IMPLEMENTED;

        MaDecoderConfig emptyConfig = new MaDecoderConfig();
        IntPtr filePathPtr = Marshal.StringToHGlobalAnsi(filePath);
        try
        {
            return _decoderInit(filePathPtr, ref emptyConfig, decoder);
        }
        finally
        {
            Marshal.FreeHGlobal(filePathPtr);
        }
    }

    /// <summary>
    /// Uninitializes a decoder structure and closes its file descriptors.
    /// Releases all native buffers allocated by the codec backend.
    /// </summary>
    /// <param name="decoder">Pointer to the active decoder structure to release.</param>
    public static void ma_decoder_uninit(IntPtr decoder)
    {
        if (_decoderUninit != null)
            _decoderUninit(decoder);
    }

    /// <summary>
    /// Seeks the decoder reading cursor position to a specific PCM frame offset.
    /// Allows jumping to arbitrary timestamps in the audio stream.
    /// </summary>
    /// <param name="decoder">Pointer to the active decoder structure.</param>
    /// <param name="frameIndex">The target zero-based PCM frame index to seek to.</param>
    /// <returns>Result code indicating seek success or decoder error.</returns>
    public static MaResult ma_decoder_seek_to_pcm_frame(IntPtr decoder, ulong frameIndex)
    {
        if (_decoderSeek == null)
            return MaResult.MA_NOT_IMPLEMENTED;

        return _decoderSeek(decoder, frameIndex);
    }

    /// <summary>
    /// Queries format details from a decoder including channels and sample rate.
    /// Writes results to ref parameters and map buffers.
    /// </summary>
    /// <param name="decoder">Pointer to the active decoder structure.</param>
    /// <param name="pFormat">Reference parameter to write format information into.</param>
    /// <param name="pChannels">Reference parameter to write logical channel count into.</param>
    /// <param name="pSampleRate">Reference parameter to write sample rate in Hertz into.</param>
    /// <param name="pChannelMap">Pointer to target channel map array, or zero.</param>
    /// <param name="channelMapCap">Capacity of the target channel map array in bytes.</param>
    /// <returns>Result code indicating query success or decoder error.</returns>
    public static MaResult ma_decoder_get_data_format(IntPtr decoder, ref MaFormat pFormat, ref uint pChannels, ref uint pSampleRate, IntPtr pChannelMap, ulong channelMapCap)
    {
        if (_decoderGetFormat == null)
            return MaResult.MA_NOT_IMPLEMENTED;

        return _decoderGetFormat(decoder, ref pFormat, ref pChannels, ref pSampleRate, pChannelMap, channelMapCap);
    }

    /// <summary>
    /// Queries the current playback cursor position inside the decoder.
    /// Measured in absolute PCM frames from the beginning of the file.
    /// </summary>
    /// <param name="decoder">Pointer to the active decoder structure.</param>
    /// <param name="pCursor">Reference parameter to write the current cursor position into.</param>
    /// <returns>Result code indicating query success or decoder error.</returns>
    public static MaResult ma_decoder_get_cursor_in_pcm_frames(IntPtr decoder, ref ulong pCursor)
    {
        if (_decoderGetCursorPosition == null)
            return MaResult.MA_NOT_IMPLEMENTED;

        return _decoderGetCursorPosition(decoder, ref pCursor);
    }

    /// <summary>
    /// Initializes default values inside a decoder configuration structure.
    /// Populates defaults for channels, rates, and formats.
    /// </summary>
    /// <param name="format">The target decoding audio format layout.</param>
    /// <param name="channels">The target decoding logical channel count.</param>
    /// <param name="sampleRate">The target decoding sample rate in Hertz.</param>
    /// <returns>A default-initialized decoder configuration structure.</returns>
    public static MaDecoderConfig ma_decoder_config_init(MaFormat format, uint channels, uint sampleRate)
    {
        if (_decoderConfigInit == null)
        {
            return new MaDecoderConfig
            {
                format = format,
                channels = channels,
                sampleRate = sampleRate,
                channelMixMode = MaChannelMixMode.Rectangular,
                pChannelMap = IntPtr.Zero,
                allocationCallbacks = IntPtr.Zero
            };
        }

        return _decoderConfigInit(format, channels, sampleRate);
    }

    /// <summary>
    /// Allocates unmanaged memory for a decoder config and populates it.
    /// Wraps config initialization with unmanaged structure mapping.
    /// </summary>
    /// <param name="format">The target decoding audio format layout.</param>
    /// <param name="channels">The target decoding logical channel count.</param>
    /// <param name="sampleRate">The target decoding sample rate in Hertz.</param>
    /// <returns>Pointer to the allocated decoder configuration structure.</returns>
    public static IntPtr ma_decoder_config_alloc(MaFormat format, uint channels, uint sampleRate)
    {
        MaDecoderConfig config = ma_decoder_config_init(format, channels, sampleRate);

        IntPtr ptr = ma_malloc((ulong)Marshal.SizeOf<MaDecoderConfig>(), IntPtr.Zero);
        if (ptr != IntPtr.Zero)
        {
            Marshal.StructureToPtr(config, ptr, false);
        }

        return ptr;
    }

    /// <summary>
    /// Initializes a decoder from custom memory/stream callbacks.
    /// Enables stream decoding from C# source stream streams.
    /// </summary>
    /// <param name="readCallback">Callback function used to copy binary source data.</param>
    /// <param name="seekCallback">Callback function used to seek source data position.</param>
    /// <param name="pUserData">User-defined context data pointer passed in callbacks.</param>
    /// <param name="config">Pointer to decoder configuration settings.</param>
    /// <param name="decoder">Pointer to destination decoder structure to initialize.</param>
    /// <returns>Result code indicating initialization success or stream error.</returns>
    /// <exception cref="NotSupportedException">Thrown when function pointer is missing.</exception>
    public static MaResult ma_decoder_init(DecoderReadProc readCallback, DecoderSeekProc seekCallback, IntPtr pUserData, IntPtr config, IntPtr decoder)
    {
        if (_decoderInitStream == null)
            throw new NotSupportedException("Stream-based decoder init is not available.");

        IntPtr rcb = readCallback != null ? Marshal.GetFunctionPointerForDelegate(readCallback) : IntPtr.Zero;
        IntPtr scb = seekCallback != null ? Marshal.GetFunctionPointerForDelegate(seekCallback) : IntPtr.Zero;

        return _decoderInitStream(rcb, scb, pUserData, config, decoder);
    }

    /// <summary>
    /// Reads decoded PCM audio frames into the output buffer.
    /// Returns the number of frames successfully read.
    /// </summary>
    /// <param name="decoder">Pointer to the active decoder structure.</param>
    /// <param name="pFramesOut">Pointer to the native destination buffer to write into.</param>
    /// <param name="frameCount">Number of frames requested by the caller.</param>
    /// <param name="pFramesRead">Receives the number of frames successfully read.</param>
    /// <returns>Result code indicating success or decoding error.</returns>
    /// <exception cref="NotSupportedException">Thrown when function pointer is missing.</exception>
    public static MaResult ma_decoder_read_pcm_frames(IntPtr decoder, IntPtr pFramesOut, ulong frameCount, out ulong pFramesRead)
    {
        pFramesRead = 0;
        if (_decoderRead == null)
            throw new NotSupportedException("Decoder read operation is not supported.");

        return _decoderRead(decoder, pFramesOut, frameCount, out pFramesRead);
    }

    /// <summary>
    /// Queries the total length of the audio file in PCM frames.
    /// Can return zero or error if length cannot be resolved (streaming).
    /// </summary>
    /// <param name="decoder">Pointer to the active decoder structure.</param>
    /// <param name="pLength">Receives the total length of the file in frames.</param>
    /// <returns>Result code indicating query success or decoder error.</returns>
    /// <exception cref="NotSupportedException">Thrown when function pointer is missing.</exception>
    public static MaResult ma_decoder_get_length_in_pcm_frames(IntPtr decoder, out ulong pLength)
    {
        pLength = 0;
        if (_decoderGetLength == null)
            throw new NotSupportedException("Getting decoder length is not supported.");

        return _decoderGetLength(decoder, out pLength);
    }

    /// <summary>
    /// Allocates memory for a decoder structure from native heap.
    /// Throws NotSupportedException if allocate endpoint export is missing.
    /// </summary>
    /// <returns>Pointer to the allocated context structure.</returns>
    /// <exception cref="NotSupportedException">Thrown when function pointer is missing.</exception>
    public static IntPtr sf_allocate_decoder()
    {
        if (_allocateDecoder == null)
            throw new NotSupportedException("Decoder allocation is not supported.");

        return _allocateDecoder();
    }

    /// <summary>
    /// Allocates memory for a decoder structure and zeroes it out.
    /// Standard helper method that manages allocator calls.
    /// </summary>
    /// <returns>Pointer to the zero-initialized context structure.</returns>
    public static IntPtr allocate_decoder()
    {
        ulong size = (ulong)Marshal.SizeOf<MaDecoder>();
        IntPtr ptr = ma_malloc(size, IntPtr.Zero);

        if (ptr != IntPtr.Zero)
            ZeroMemory(ptr, size);

        return ptr;
    }

    /// <summary>
    /// Allocates config structure from native heap using target settings.
    /// Throws NotSupportedException if config allocation endpoint is missing.
    /// </summary>
    /// <param name="format">The target decoding audio format layout.</param>
    /// <param name="channels">The target decoding logical channel count.</param>
    /// <param name="sampleRate">The target decoding sample rate in Hertz.</param>
    /// <returns>Pointer to the allocated decoder configuration structure.</returns>
    /// <exception cref="NotSupportedException">Thrown when function pointer is missing.</exception>
    public static IntPtr sf_allocate_decoder_config(MaFormat format, uint channels, uint sampleRate)
    {
        if (_allocateDecoderConfig == null)
            throw new NotSupportedException("Decoder config allocation is not supported.");

        return _allocateDecoderConfig(format, channels, sampleRate);
    }

    /// <summary>
    /// Allocates decoder config structure memory and initializes it.
    /// Prepares parameters for target formats and maps properties.
    /// </summary>
    /// <param name="format">The target decoding audio format layout.</param>
    /// <param name="channels">The target decoding logical channel count.</param>
    /// <param name="sampleRate">The target decoding sample rate in Hertz.</param>
    /// <returns>Pointer to the zero-initialized config structure.</returns>
    public static IntPtr allocate_decoder_config(MaFormat format, uint channels, uint sampleRate)
    {
        ulong size = (ulong)Marshal.SizeOf<MaDecoderConfig>();
        IntPtr ptr = ma_malloc(size, IntPtr.Zero);

        if (ptr != IntPtr.Zero)
        {
            MaDecoderConfig config = ma_decoder_config_init(format, channels, sampleRate);
            Marshal.StructureToPtr(config, ptr, false);
        }

        return ptr;
    }

    #endregion

    #region Encoder Operations

    /// <summary>
    /// Initializes an encoder structure to write output to a file path.
    /// Automatically marshals the string file path to unmanaged memory.
    /// </summary>
    /// <param name="filePath">The destination output file path on disk.</param>
    /// <param name="pConfig">Pointer to custom encoder configuration settings.</param>
    /// <param name="pEncoder">Pointer to destination encoder structure to initialize.</param>
    /// <returns>Result code indicating initialization success or file error.</returns>
    /// <exception cref="NotSupportedException">Thrown when function pointer is missing.</exception>
    public static MaResult ma_encoder_init_file(string filePath, IntPtr pConfig, IntPtr pEncoder)
    {
        if (_encoderInit == null)
            throw new NotSupportedException("Encoder initialization is not supported.");

        IntPtr filePathPtr = Marshal.StringToHGlobalAnsi(filePath);
        try
        {
            return _encoderInit(filePathPtr, pConfig, pEncoder);
        }
        finally
        {
            Marshal.FreeHGlobal(filePathPtr);
        }
    }

    /// <summary>
    /// Uninitializes an encoder structure and closes its file descriptors.
    /// Flushes any remaining samples to the disk before completion.
    /// </summary>
    /// <param name="pEncoder">Pointer to the active encoder structure to release.</param>
    /// <exception cref="NotSupportedException">Thrown when function pointer is missing.</exception>
    public static void ma_encoder_uninit(IntPtr pEncoder)
    {
        if (_encoderUninit == null)
            throw new NotSupportedException("Encoder uninitialization is not supported.");

        _encoderUninit(pEncoder);
    }

    /// <summary>
    /// Encodes and writes PCM frames to the output file container.
    /// Returns the number of frames successfully written.
    /// </summary>
    /// <param name="pEncoder">Pointer to the active encoder structure.</param>
    /// <param name="pFramesIn">Pointer to buffer containing source PCM frames.</param>
    /// <param name="frameCount">Number of source frames to write.</param>
    /// <param name="pFramesWritten">Receives the count of frames successfully written.</param>
    /// <returns>Result code indicating success or encoding error.</returns>
    /// <exception cref="NotSupportedException">Thrown when function pointer is missing.</exception>
    public static MaResult ma_encoder_write_pcm_frames(IntPtr pEncoder, IntPtr pFramesIn, ulong frameCount, out ulong pFramesWritten)
    {
        pFramesWritten = 0;
        if (_encoderWrite == null)
            throw new NotSupportedException("Encoder write operation is not supported.");

        return _encoderWrite(pEncoder, pFramesIn, frameCount, out pFramesWritten);
    }

    /// <summary>
    /// Allocates memory for an encoder structure and zeroes it out.
    /// Helps prepare memory layout before triggering encoder initialization.
    /// </summary>
    /// <returns>Pointer to the zero-initialized encoder structure.</returns>
    public static IntPtr allocate_encoder()
    {
        ulong size = (ulong)Marshal.SizeOf<MaEncoder>();
        IntPtr ptr = ma_malloc(size, IntPtr.Zero);

        if (ptr != IntPtr.Zero)
            ZeroMemory(ptr, size);

        return ptr;
    }

    /// <summary>
    /// Allocates encoder config structure memory and initializes it.
    /// Prepares parameters for target formats and maps properties.
    /// </summary>
    /// <param name="encodingFormat">The output encoding file container format.</param>
    /// <param name="format">The target source audio format layout.</param>
    /// <param name="channels">The target source logical channel count.</param>
    /// <param name="sampleRate">The target source sample rate in Hertz.</param>
    /// <returns>Pointer to the allocated encoder configuration structure.</returns>
    public static IntPtr allocate_encoder_config(MaEncodingFormat encodingFormat, MaFormat format, uint channels, uint sampleRate)
    {
        ulong size = (ulong)Marshal.SizeOf<MaEncoderConfig>();
        IntPtr ptr = ma_malloc(size, IntPtr.Zero);

        if (ptr != IntPtr.Zero)
        {
            ZeroMemory(ptr, size);

            MaEncoderConfig* pConfig = (MaEncoderConfig*)ptr.ToPointer();
            pConfig->encodingFormat = encodingFormat;
            pConfig->format = format;
            pConfig->channels = channels;
            pConfig->sampleRate = sampleRate;
        }

        return ptr;
    }

    #endregion

    #region Resampler Operations

    /// <summary>
    /// Initializes an audio resampler with the configured parameters.
    /// Prepares filters and coefficients for conversion.
    /// </summary>
    /// <param name="pConfig">Reference to the configuration settings structure.</param>
    /// <param name="pResampler">Pointer to the destination resampler structure to initialize.</param>
    /// <returns>Result code indicating resampler initialization success or error.</returns>
    public static MaResult ma_resampler_init(ref MaResamplerConfig pConfig, IntPtr pResampler)
    {
        if (_resamplerInit == null)
            return MaResult.MA_NOT_IMPLEMENTED;

        return _resamplerInit(ref pConfig, pResampler);
    }

    /// <summary>
    /// Uninitializes and frees resources used by an active resampler.
    /// The resampler pointer becomes invalid after this call completes.
    /// </summary>
    /// <param name="pResampler">Pointer to the active resampler structure to release.</param>
    public static void ma_resampler_uninit(IntPtr pResampler)
    {
        if (_resamplerUninit != null)
            _resamplerUninit(pResampler);
    }

    /// <summary>
    /// Processes audio samples to convert between different sample rates.
    /// Writes the resampled outputs directly to the callers' parameters.
    /// </summary>
    /// <param name="pResampler">Pointer to the active resampler structure.</param>
    /// <param name="pFramesIn">Pointer to the input buffer containing source frames.</param>
    /// <param name="pFrameCountIn">Reference parameter specifying the source frame count.</param>
    /// <param name="pFramesOut">Pointer to the destination buffer to write conversion output.</param>
    /// <param name="pFrameCountOut">Reference parameter specifying the destination frame capacity.</param>
    /// <returns>Result code indicating processing success or conversion error.</returns>
    public static MaResult ma_resampler_process_pcm_frames(
        IntPtr pResampler,
        IntPtr pFramesIn,
        ref ulong pFrameCountIn,
        IntPtr pFramesOut,
        ref ulong pFrameCountOut)
    {
        if (_resamplerProcess == null)
            return MaResult.MA_NOT_IMPLEMENTED;

        return _resamplerProcess(pResampler, pFramesIn, ref pFrameCountIn, pFramesOut, ref pFrameCountOut);
    }

    /// <summary>
    /// Changes the input/output sample rates dynamically on a resampler.
    /// Triggers filter coefficient recalculations to adopt new rates.
    /// </summary>
    /// <param name="pResampler">Pointer to the active resampler structure.</param>
    /// <param name="rateIn">The new input sample rate in Hertz.</param>
    /// <param name="rateOut">The new output sample rate in Hertz.</param>
    /// <returns>Result code indicating change success or parameter error.</returns>
    public static MaResult ma_resampler_set_rate(IntPtr pResampler, uint rateIn, uint rateOut)
    {
        if (_resamplerSetRate == null)
            return MaResult.MA_NOT_IMPLEMENTED;

        return _resamplerSetRate(pResampler, rateIn, rateOut);
    }

    /// <summary>
    /// Initializes resampler settings structure with default parameters.
    /// Configures filter orders for conversion algorithm logic.
    /// </summary>
    /// <param name="formatIn">The source input sample format layout.</param>
    /// <param name="formatOut">The target output sample format layout.</param>
    /// <param name="channelsIn">The source input logical channel count.</param>
    /// <param name="channelsOut">The target output logical channel count.</param>
    /// <param name="sampleRateIn">The source input sample rate in Hertz.</param>
    /// <param name="sampleRateOut">The target output sample rate in Hertz.</param>
    /// <param name="algorithm">The resampling algorithm filter to apply.</param>
    /// <returns>A default-initialized resampler configuration structure.</returns>
    public static MaResamplerConfig ma_resampler_config_init(
    MaFormat formatIn,
    MaFormat formatOut,
    uint channelsIn,
    uint channelsOut,
    uint sampleRateIn,
    uint sampleRateOut,
    MaResampleAlgorithm algorithm = MaResampleAlgorithm.Linear)
    {
        if (_resamplerConfigInit == null)
        {
            var config = new MaResamplerConfig
            {
                format = formatIn,
                channels = channelsIn,
                sampleRateIn = sampleRateIn,
                sampleRateOut = sampleRateOut,
                algorithm = algorithm,
                pBackendVTable = IntPtr.Zero,
                pBackendUserData = IntPtr.Zero
            };

            if (algorithm == MaResampleAlgorithm.Linear)
            {
                config.linear.lpfOrder = 4;
            }

            return config;
        }

        // The native function takes a single format/channel pair; formatOut/channelsOut only
        // participate in the managed fallback above (kept for signature compatibility).
        return _resamplerConfigInit(formatIn, channelsIn, sampleRateIn, sampleRateOut, algorithm);
    }

    #endregion

    #region Memory Management

    /// <summary>
    /// Allocates unmanaged memory blocks through the MiniAudio allocator.
    /// Throws NotSupportedException if allocator endpoint export is missing.
    /// </summary>
    /// <param name="size">The size in bytes of the block to allocate.</param>
    /// <param name="pUserData">User-defined allocator context pointer, or zero.</param>
    /// <returns>Pointer to the allocated memory block on the unmanaged heap.</returns>
    /// <exception cref="NotSupportedException">Thrown when function pointer is missing.</exception>
    public static IntPtr ma_malloc(ulong size, IntPtr pUserData)
    {
        if (_maMalloc == null)
            throw new NotSupportedException("Memory allocation is not supported.");

        return _maMalloc(size, pUserData);
    }

    /// <summary>
    /// Frees unmanaged memory blocks that were allocated using context userData.
    /// Logs debug information message when non-null parameter is passed.
    /// </summary>
    /// <param name="ptr">Pointer to the unmanaged memory block to release.</param>
    /// <param name="pUserData">User-defined allocator context pointer, or zero.</param>
    /// <param name="message">An optional log message detailing the free event.</param>
    /// <exception cref="NotSupportedException">Thrown when function pointer is missing.</exception>
    public static void ma_free(IntPtr ptr, IntPtr pUserData, string message)
    {
        if (_maFree == null)
            throw new NotSupportedException("Memory free operation is not supported.");

        if (!string.IsNullOrEmpty(message)) Log.Info(message);
        _maFree(ptr, pUserData);
    }

    /// <summary>
    /// Frees unmanaged memory blocks that were allocated using native malloc.
    /// The pointer becomes invalid for access after this call.
    /// </summary>
    /// <param name="ptr">Pointer to the unmanaged memory block to release.</param>
    /// <exception cref="NotSupportedException">Thrown when function pointer is missing.</exception>
    public static void ma_free(IntPtr ptr)
    {
        if (_free == null)
            throw new NotSupportedException("Memory free operation is not supported.");

        _free(ptr);
    }

    /// <summary>
    /// Fills the specified unmanaged memory block with zero bytes.
    /// Optimized for 8-byte boundaries before doing single-byte fills.
    /// </summary>
    /// <param name="ptr">Pointer to the unmanaged memory block to clear.</param>
    /// <param name="size">The size in bytes of the block to clear.</param>
    private static void ZeroMemory(IntPtr ptr, ulong size)
    {
        byte* p = (byte*)ptr.ToPointer();
        ulong i = 0;

        ulong* p64 = (ulong*)p;
        for (; i + 8 <= size; i += 8)
            *p64++ = 0;

        p = (byte*)p64;
        for (; i < size; i++)
            *p++ = 0;
    }

    #endregion

    #region Error Handling

    /// <summary>
    /// Retrieves static text describing a specific MiniAudio result code.
    /// The returned memory is owned by MiniAudio and must not be modified.
    /// </summary>
    /// <param name="result">The result code returned by a MiniAudio function.</param>
    /// <returns>An unmanaged pointer to a null-terminated description string.</returns>
    public static IntPtr ma_result_description(MaResult result)
    {
        return _getErrorString != null ? _getErrorString(result) : IntPtr.Zero;
    }

    #endregion
}
