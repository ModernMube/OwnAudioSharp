using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.Utils;
using Logger;

namespace Ownaudio.Native.MiniAudio;

internal static partial class MaBinding
{
    private static readonly object _initLock = new object();
    private static LibraryLoader? _libraryLoader;
    private static bool _isInitialized = false;

    /// <summary>
    /// Initializes MiniAudio bindings. Thread-safe and idempotent.
    /// Only the first call will actually load the library and initialize bindings.
    /// Subsequent calls will be ignored.
    /// </summary>
    public static void InitializeBindings(LibraryLoader loader)
    {
        if (_isInitialized)
            return;

        lock (_initLock)
        {
            if (_isInitialized)
                return;

            _libraryLoader = loader;

            // Context functions
            _contextInit = loader.LoadFunc<ContextInit>(nameof(ma_context_init));
            _contextUninit = loader.LoadFunc<ContextUninit>(nameof(ma_context_uninit));
            _contextGetDeviceInfo = loader.LoadFunc<ContextGetDeviceInfo>(nameof(ma_context_get_device_info));
            _contextGetDevices = loader.LoadFunc<ContextGetDevices>(nameof(ma_context_get_devices));
            _contextEnumerateDevices = loader.LoadFunc<ContextEnumerateDevices>(nameof(ma_context_enumerate_devices));

            // Device functions
            _deviceInit = loader.LoadFunc<DeviceInit>(nameof(ma_device_init));
            _deviceUninit = loader.LoadFunc<DeviceUninit>(nameof(ma_device_uninit));
            _deviceStart = loader.LoadFunc<DeviceStart>(nameof(ma_device_start));
            _deviceStop = loader.LoadFunc<DeviceStop>(nameof(ma_device_stop));
            _deviceIsStarted = loader.LoadFunc<DeviceIsStarted>(nameof(ma_device_is_started));
            _deviceGetInfo = loader.LoadFunc<DeviceGetInfo>(nameof(ma_device_get_info));
            _deviceGetContext = loader.LoadFunc<DeviceGetContext>(nameof(ma_device_get_context));
            _getDevices = loader.LoadFunc<GetDevices>(nameof(sf_get_devices));

            // Engine functions
            _engineInit = loader.LoadFunc<EngineInit>(nameof(ma_engine_init));
            _engineUninit = loader.LoadFunc<EngineUninit>(nameof(ma_engine_uninit));
            _enginePlay = loader.LoadFunc<EnginePlay>(nameof(ma_engine_play_sound));

            // Sound functions
            _soundInit = loader.LoadFunc<SoundInit>(nameof(ma_sound_init_from_file));
            _soundUninit = loader.LoadFunc<SoundUninit>(nameof(ma_sound_uninit));
            _soundStart = loader.LoadFunc<SoundStart>(nameof(ma_sound_start));
            _soundStop = loader.LoadFunc<SoundStop>(nameof(ma_sound_stop));
            _soundIsPlaying = loader.LoadFunc<SoundIsPlaying>(nameof(ma_sound_is_playing));
            _soundSetVolume = loader.LoadFunc<SoundSetVolume>(nameof(ma_sound_set_volume));

            // Decoder functions
            _decoderInit = loader.LoadFunc<DecoderInit>(nameof(ma_decoder_init_file));
            _decoderUninit = loader.LoadFunc<DecoderUninit>(nameof(ma_decoder_uninit));
            _decoderRead = loader.LoadFunc<DecoderRead>(nameof(ma_decoder_read_pcm_frames));
            _decoderSeek = loader.LoadFunc<DecoderSeek>(nameof(ma_decoder_seek_to_pcm_frame));
            _decoderGetLength = loader.LoadFunc<DecoderGetLength>(nameof(ma_decoder_get_length_in_pcm_frames));
            _decoderGetFormat = loader.LoadFunc<DecoderGetFormat>(nameof(ma_decoder_get_data_format));
            _decoderGetCursorPosition = loader.LoadFunc<DecoderGetCursorPosition>(nameof(ma_decoder_get_cursor_in_pcm_frames));
            _decoderConfigInit = loader.LoadFunc<DecoderConfigInit>(nameof(ma_decoder_config_init));
            _decoderInitStream = loader.LoadFunc<DecoderInitStream>(nameof(ma_decoder_init));

            // Encoder functions
            _encoderInit = loader.LoadFunc<EncoderInit>(nameof(ma_encoder_init_file));
            _encoderUninit = loader.LoadFunc<EncoderUninit>(nameof(ma_encoder_uninit));
            _encoderWrite = loader.LoadFunc<EncoderWrite>(nameof(ma_encoder_write_pcm_frames));

            // Allocation functions
            _allocateDecoder = loader.LoadFunc<AllocateDecoder>(nameof(sf_allocate_decoder));
            _allocateDecoderConfig = loader.LoadFunc<AllocateDecoderConfig>(nameof(sf_allocate_decoder_config));

            // Resampler functions
            _resamplerInit = loader.LoadFunc<ResamplerInit>(nameof(ma_resampler_init));
            _resamplerUninit = loader.LoadFunc<ResamplerUninit>(nameof(ma_resampler_uninit));
            _resamplerProcess = loader.LoadFunc<ResamplerProcess>(nameof(ma_resampler_process_pcm_frames));
            _resamplerSetRate = loader.LoadFunc<ResamplerSetRate>(nameof(ma_resampler_set_rate));
            _resamplerConfigInit = loader.LoadFunc<ResamplerConfigInit>(nameof(ma_resampler_config_init));

            // Memory management functions
            _maMalloc = loader.LoadFunc<MaMalloc>(nameof(ma_malloc));
            _maFree = loader.LoadFunc<MaFree>(nameof(ma_free));
            _free = loader.LoadFunc<Free>(nameof(ma_free));
            _maDeviceConfigInit = loader.LoadFunc<MaDeviceConfigInit>(nameof(ma_device_config_init));

            // Error handling functions
            _getErrorString = loader.LoadFunc<GetErrorString>(nameof(ma_result_description));

            _isInitialized = true;
        }
    }

    /// <summary>
    /// Ensures MiniAudio bindings are initialized.
    /// If not already initialized, loads the library and initializes bindings.
    /// </summary>
    public static void EnsureInitialized()
    {
        if (_isInitialized)
            return;

        var loader = new LibraryLoader("libminiaudio");
        InitializeBindings(loader);
    }

#nullable disable
    #region Context Operations
    public static unsafe MaResult ma_context_init(MaBackend[] backends, uint backendCount, IntPtr contextConfig, IntPtr context)
    {
        if (backends == null || backends.Length == 0)
        {
            return _contextInit(IntPtr.Zero, 0, contextConfig, context);
        }

        fixed (MaBackend* pBackends = backends)
        {
            return _contextInit((nint)pBackends, backendCount, contextConfig, context);
        }
    }

    public static void ma_context_uninit(IntPtr context)
    {
        _contextUninit(context);
    }

    public static MaResult ma_context_get_device_info(IntPtr context, MaDeviceType deviceType, IntPtr deviceId, IntPtr deviceInfo)
    {
        return _contextGetDeviceInfo(context, deviceType, deviceId, deviceInfo);
    }

    public static unsafe MaResult ma_context_get_devices(IntPtr context, out IntPtr pPlaybackDevices, out IntPtr pCaptureDevices, out int playbackDeviceCount, out int captureDeviceCount)
    {
        pPlaybackDevices = IntPtr.Zero;
        pCaptureDevices = IntPtr.Zero;
        playbackDeviceCount = 0;
        captureDeviceCount = 0;

        fixed (IntPtr* pPlaybackDevicesPtr = &pPlaybackDevices)
        fixed (IntPtr* pCaptureDevicesPtr = &pCaptureDevices)
        fixed (int* playbackDeviceCountPtr = &playbackDeviceCount)
        fixed (int* captureDeviceCountPtr = &captureDeviceCount)
        {
            return _contextGetDevices(context, pPlaybackDevicesPtr, playbackDeviceCountPtr, pCaptureDevicesPtr, captureDeviceCountPtr);
        }
    }

    public static MaResult ma_context_enumerate_devices(IntPtr context, MaEnumDevicesCallback callback, IntPtr pUserData)
    {
        return _contextEnumerateDevices(context, callback, pUserData);
    }

    public static MaResult sf_get_devices(IntPtr context, out IntPtr pPlaybackDevices, out IntPtr pCaptureDevices, out IntPtr playbackDeviceCount, out IntPtr captureDeviceCount)
    {
        if (_getDevices == null)
            throw new NotSupportedException("Getting devices list is not supported.");

        return _getDevices(context, out pPlaybackDevices, out pCaptureDevices, out playbackDeviceCount, out captureDeviceCount);
    }

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

    public static MaResult ma_device_init(IntPtr context, IntPtr config, IntPtr device)
    {
        return _deviceInit(context, config, device);
    }

    public static void ma_device_uninit(IntPtr device)
    {
        _deviceUninit(device);
    }

    public static MaResult ma_device_start(IntPtr device)
    {
        return _deviceStart(device);
    }

    public static MaResult ma_device_stop(IntPtr device)
    {
        return _deviceStop(device);
    }

    public static bool ma_device_is_started(IntPtr device)
    {
        return _deviceIsStarted(device);
    }

    public static MaResult ma_device_get_info(IntPtr device, MaDeviceType type, IntPtr deviceInfo)
    {
        return _deviceGetInfo(device, type, deviceInfo);
    }

    public static IntPtr ma_device_get_context(IntPtr device)
    {
        return _deviceGetContext(device);
    }

    public static MaDeviceConfig ma_device_config_init(MaDeviceType deviceType)
    {
        return _maDeviceConfigInit(deviceType);
    }

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

    public static IntPtr allocate_device()
    {
        ulong size = (ulong)Marshal.SizeOf<MaDevice>();
        IntPtr ptr = ma_malloc(size, IntPtr.Zero);

        if (ptr != IntPtr.Zero)
            ZeroMemory(ptr, size);

        return ptr;
    }

    public static IntPtr allocate_device_config(MaDeviceType deviceType, MaFormat format, uint channels, uint sampleRate,
                                           MaDataCallback dataCallback, IntPtr playbackDeviceId, IntPtr captureDeviceId, uint sizeinframe = 512)
    {
        return ma_device_config_alloc(deviceType, format, channels, sampleRate, dataCallback, playbackDeviceId, captureDeviceId, sizeinframe);
    }

    #endregion

    #region Engine Operations

    public static MaResult ma_engine_init(IntPtr engineConfig, IntPtr engine)
    {
        return _engineInit(engineConfig, engine);
    }

    public static void ma_engine_uninit(IntPtr engine)
    {
        _engineUninit(engine);
    }

    public static MaResult ma_engine_play_sound(IntPtr engine, string filePath, IntPtr pGroup)
    {
        return _enginePlay(engine, filePath, pGroup);
    }

    #endregion

    #region Sound Operations

    public static MaResult ma_sound_init_from_file(IntPtr engine, string filePath, uint flags, IntPtr pGroup, IntPtr pAllocationCallbacks, IntPtr pSound)
    {
        return _soundInit(engine, filePath, flags, pGroup, pAllocationCallbacks, pSound);
    }

    public static void ma_sound_uninit(IntPtr sound)
    {
        _soundUninit(sound);
    }

    public static MaResult ma_sound_start(IntPtr sound)
    {
        return _soundStart(sound);
    }

    public static MaResult ma_sound_stop(IntPtr sound)
    {
        return _soundStop(sound);
    }

    public static bool ma_sound_is_playing(IntPtr sound)
    {
        return _soundIsPlaying(sound);
    }

    public static void ma_sound_set_volume(IntPtr sound, float volume)
    {
        _soundSetVolume(sound, volume);
    }

    #endregion

    #region Decoder Operations

    public static MaResult ma_decoder_init_file(string filePath, ref MaDecoderConfig config, IntPtr decoder)
    {
        return _decoderInit(filePath, ref config, decoder);
    }

    public static MaResult ma_decoder_init_file(string filePath, IntPtr nullConfig, IntPtr decoder)
    {
        if (nullConfig != IntPtr.Zero)
        {
            throw new ArgumentException("A nullConfig paraméternek IntPtr.Zero-nak kell lennie");
        }

        MaDecoderConfig emptyConfig = new MaDecoderConfig();
        return _decoderInit(filePath, ref emptyConfig, decoder);
    }

    public static void ma_decoder_uninit(IntPtr decoder)
    {
        _decoderUninit(decoder);
    }

    public static MaResult ma_decoder_seek_to_pcm_frame(IntPtr decoder, ulong frameIndex)
    {
        return _decoderSeek(decoder, frameIndex);
    }

    public static MaResult ma_decoder_get_data_format(IntPtr decoder, ref MaFormat pFormat, ref uint pChannels, ref uint pSampleRate, IntPtr pChannelMap, ulong channelMapCap)
    {
        if (_decoderGetFormat == null)
            return MaResult.MA_NOT_IMPLEMENTED;

        return _decoderGetFormat(decoder, ref pFormat, ref pChannels, ref pSampleRate, pChannelMap, channelMapCap);
    }

    public static MaResult ma_decoder_get_cursor_in_pcm_frames(IntPtr decoder, ref ulong pCursor)
    {
        if (_decoderGetCursorPosition == null)
            return MaResult.MA_NOT_IMPLEMENTED;

        return _decoderGetCursorPosition(decoder, ref pCursor);
    }

    public static MaDecoderConfig ma_decoder_config_init(MaFormat format, uint channels, uint sampleRate)
    {
        if (_decoderConfigInit.Target == null)
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

    public static MaResult ma_decoder_init(DecoderReadProc readCallback, DecoderSeekProc seekCallback, IntPtr pUserData, IntPtr config, IntPtr decoder)
    {
        if (_decoderInitStream == null)
            throw new NotSupportedException("Stream-based decoder init is not available.");

        return _decoderInitStream(readCallback, seekCallback, pUserData, config, decoder);
    }

    public static MaResult ma_decoder_read_pcm_frames(IntPtr decoder, IntPtr pFramesOut, ulong frameCount, out ulong pFramesRead)
    {
        if (_decoderRead == null)
            throw new NotSupportedException("Decoder read operation is not supported.");

        return _decoderRead(decoder, pFramesOut, frameCount, out pFramesRead);
    }

    public static MaResult ma_decoder_get_length_in_pcm_frames(IntPtr decoder, out ulong pLength)
    {
        if (_decoderGetLength == null)
            throw new NotSupportedException("Getting decoder length is not supported.");

        return _decoderGetLength(decoder, out pLength);
    }

    public static IntPtr sf_allocate_decoder()
    {
        if (_allocateDecoder == null)
            throw new NotSupportedException("Decoder allocation is not supported.");

        return _allocateDecoder();
    }

    public static IntPtr allocate_decoder()
    {
        ulong size = (ulong)Marshal.SizeOf<MaDecoder>();
        IntPtr ptr = ma_malloc(size, IntPtr.Zero);

        if (ptr != IntPtr.Zero)
            ZeroMemory(ptr, size);

        return ptr;
    }

    public static IntPtr sf_allocate_decoder_config(MaFormat format, uint channels, uint sampleRate)
    {
        if (_allocateDecoderConfig == null)
            throw new NotSupportedException("Decoder config allocation is not supported.");

        return _allocateDecoderConfig(format, channels, sampleRate);
    }

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

    public static MaResult ma_encoder_init_file(string filePath, IntPtr pConfig, IntPtr pEncoder)
    {
        if (_encoderInit == null)
            throw new NotSupportedException("Encoder initialization is not supported.");

        return _encoderInit(filePath, pConfig, pEncoder);
    }

    public static void ma_encoder_uninit(IntPtr pEncoder)
    {
        if (_encoderUninit == null)
            throw new NotSupportedException("Encoder uninitialization is not supported.");

        _encoderUninit(pEncoder);
    }

    public static MaResult ma_encoder_write_pcm_frames(IntPtr pEncoder, IntPtr pFramesIn, ulong frameCount, out ulong pFramesWritten)
    {
        if (_encoderWrite == null)
            throw new NotSupportedException("Encoder write operation is not supported.");

        return _encoderWrite(pEncoder, pFramesIn, frameCount, out pFramesWritten);
    }

    public static IntPtr allocate_encoder()
    {
        ulong size = (ulong)Marshal.SizeOf<MaEncoder>();
        IntPtr ptr = ma_malloc(size, IntPtr.Zero);

        if (ptr != IntPtr.Zero)
            ZeroMemory(ptr, size);

        return ptr;
    }

    public static IntPtr allocate_encoder_config(MaEncodingFormat encodingFormat, MaFormat format, uint channels, uint sampleRate)
    {
        ulong size = (ulong)Marshal.SizeOf<MaEncoderConfig>();
        IntPtr ptr = ma_malloc(size, IntPtr.Zero);

        if (ptr != IntPtr.Zero)
        {
            ZeroMemory(ptr, size);

            unsafe
            {
                MaEncoderConfig* pConfig = (MaEncoderConfig*)ptr.ToPointer();
                pConfig->encodingFormat = encodingFormat;
                pConfig->format = format;
                pConfig->channels = channels;
                pConfig->sampleRate = sampleRate;
            }
        }

        return ptr;
    }

    #endregion

    #region Resampler Operations

    public static MaResult ma_resampler_init(ref MaResamplerConfig pConfig, IntPtr pResampler)
    {
        if (_resamplerInit == null)
            return MaResult.MA_NOT_IMPLEMENTED;

        return _resamplerInit(ref pConfig, pResampler);
    }

    public static void ma_resampler_uninit(IntPtr pResampler)
    {
        if (_resamplerUninit != null)
            _resamplerUninit(pResampler);
    }

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

    public static MaResult ma_resampler_set_rate(IntPtr pResampler, uint rateIn, uint rateOut)
    {
        if (_resamplerSetRate == null)
            return MaResult.MA_NOT_IMPLEMENTED;

        return _resamplerSetRate(pResampler, rateIn, rateOut);
    }

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
                pAllocationCallbacks = IntPtr.Zero
            };

            if (algorithm == MaResampleAlgorithm.Linear)
            {
                config.linear.lpfOrder = 4;
            }

            return config;
        }

        return _resamplerConfigInit(formatIn, formatOut, channelsIn, channelsOut, sampleRateIn, sampleRateOut, algorithm);
    }

    #endregion

    #region Memory Management

    public static IntPtr ma_malloc(ulong size, IntPtr pUserData)
    {
        if (_maMalloc == null)
            throw new NotSupportedException("Memory allocation is not supported.");

        return _maMalloc(size, pUserData);
    }

    public static void ma_free(IntPtr ptr, IntPtr pUserData, string message)
    {
        if (_maFree == null)
            throw new NotSupportedException("Memory free operation is not supported.");

        if (string.IsNullOrEmpty(message)) Log.Write(message);
        _maFree(ptr, pUserData);
    }

    public static void ma_free(IntPtr ptr)
    {
        if (_free == null)
            throw new NotSupportedException("Memory free operation is not supported.");

        _free(ptr);
    }

    private static unsafe void ZeroMemory(IntPtr ptr, ulong size)
    {
        //Using native memset - much faster
        byte* p = (byte*)ptr.ToPointer();
        ulong i = 0;

        //Zeroing 8-byte blocks (faster)
        ulong* p64 = (ulong*)p;
        for (; i + 8 <= size; i += 8)
            *p64++ = 0;

        //Remaining bytes
        p = (byte*)p64;
        for (; i < size; i++)
            *p++ = 0;
    }

    #endregion

    #region Error Handling

    public static IntPtr ma_result_description(MaResult result)
    {
        return _getErrorString(result);
    }

    #endregion
#nullable restore
}
