using System;
using System.Runtime.InteropServices;

namespace Ownaudio.Native.MiniAudio;

/// <summary>
/// Contains the unmanaged function pointer fields, constant definitions,
/// and callback delegates required for binding to the native MiniAudio library.
/// </summary>
internal static unsafe partial class MaBinding
{
    /// <summary>
    /// Function pointer to the native ma_context_init function.
    /// Initializes a MiniAudio context for device discovery and management.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<nint, uint, nint, nint, MaResult> _contextInit;

    /// <summary>
    /// Function pointer to the native ma_context_uninit function.
    /// Uninitializes and cleans up a previously initialized MiniAudio context.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _contextUninit;

    /// <summary>
    /// Function pointer to the native ma_context_get_device_info function.
    /// Retrieves detailed capability information for a specific audio device.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, MaDeviceType, IntPtr, IntPtr, MaResult> _contextGetDeviceInfo;

    /// <summary>
    /// Function pointer to the native ma_context_get_devices function.
    /// Retrieves list arrays of all available playback and capture devices.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, int*, IntPtr*, int*, MaResult> _contextGetDevices;

    /// <summary>
    /// Function pointer to the native ma_context_enumerate_devices function.
    /// Enumerates all connected audio devices using a callback function.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, MaResult> _contextEnumerateDevices;

    /// <summary>
    /// Function pointer to the native ma_device_init function.
    /// Initializes a playback or capture audio device structure.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, MaResult> _deviceInit;

    /// <summary>
    /// Function pointer to the native ma_device_uninit function.
    /// Uninitializes and frees resources used by an audio device.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _deviceUninit;

    /// <summary>
    /// Function pointer to the native ma_device_start function.
    /// Starts the audio data processing stream on the device.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, MaResult> _deviceStart;

    /// <summary>
    /// Function pointer to the native ma_device_stop function.
    /// Stops the active audio data processing stream on the device.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, MaResult> _deviceStop;

    /// <summary>
    /// Function pointer to the native ma_device_is_started function.
    /// Checks whether the audio device stream is currently active.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _deviceIsStarted;

    /// <summary>
    /// Function pointer to the native ma_device_get_info function.
    /// Retrieves the current configuration information for an active device.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, MaDeviceType, IntPtr, MaResult> _deviceGetInfo;

    /// <summary>
    /// Function pointer to the native ma_device_get_context function.
    /// Retrieves a pointer to the context associated with a device.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> _deviceGetContext;

    /// <summary>
    /// Function pointer to the native sf_get_devices function.
    /// Retrieves lists of registered playback and capture devices.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, out IntPtr, out IntPtr, out IntPtr, out IntPtr, MaResult> _getDevices;

    /// <summary>
    /// Function pointer to the native ma_device_config_init function.
    /// Initializes a device configuration structure with default settings.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<MaDeviceType, MaDeviceConfig> _maDeviceConfigInit;

    /// <summary>
    /// Function pointer to the native ma_engine_init function.
    /// Initializes the high-level audio playback engine instance.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, MaResult> _engineInit;

    /// <summary>
    /// Function pointer to the native ma_engine_uninit function.
    /// Uninitializes and releases the audio playback engine resources.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _engineUninit;

    /// <summary>
    /// Function pointer to the native ma_engine_play_sound function.
    /// Plays an audio file directly using the high-level engine.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, MaResult> _enginePlay;

    /// <summary>
    /// Function pointer to the native ma_sound_init_from_file function.
    /// Initializes a sound instance from a specified audio file path.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, IntPtr, IntPtr, IntPtr, MaResult> _soundInit;

    /// <summary>
    /// Function pointer to the native ma_sound_uninit function.
    /// Uninitializes and frees resources used by a sound instance.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _soundUninit;

    /// <summary>
    /// Function pointer to the native ma_sound_start function.
    /// Starts playback of the initialized sound instance.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, MaResult> _soundStart;

    /// <summary>
    /// Function pointer to the native ma_sound_stop function.
    /// Stops playback of the initialized sound instance.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, MaResult> _soundStop;

    /// <summary>
    /// Function pointer to the native ma_sound_is_playing function.
    /// Determines whether the sound instance is currently playing.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _soundIsPlaying;

    /// <summary>
    /// Function pointer to the native ma_sound_set_volume function.
    /// Adjusts the volume multiplier of a sound instance.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, float, void> _soundSetVolume;

    /// <summary>
    /// Function pointer to the native ma_decoder_init_file function.
    /// Initializes a file-based decoder with custom config settings.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, ref MaDecoderConfig, IntPtr, MaResult> _decoderInit;

    /// <summary>
    /// Function pointer to the native ma_decoder_uninit function.
    /// Uninitializes and releases file-based decoder resources.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _decoderUninit;

    /// <summary>
    /// Function pointer to the native ma_decoder_read_pcm_frames function.
    /// Reads decoded PCM audio frames into the output buffer.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, ulong, out ulong, MaResult> _decoderRead;

    /// <summary>
    /// Function pointer to the native ma_decoder_seek_to_pcm_frame function.
    /// Seeks the decoder reading cursor to a specific frame offset.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, ulong, MaResult> _decoderSeek;

    /// <summary>
    /// Function pointer to the native ma_decoder_config_init function.
    /// Initializes default values inside a decoder config structure.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<MaFormat, uint, uint, MaDecoderConfig> _decoderConfigInit;

    /// <summary>
    /// Function pointer to the native ma_decoder_init function.
    /// Initializes a decoder from custom memory/stream callbacks.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, MaResult> _decoderInitStream;

    /// <summary>
    /// Function pointer to the native ma_decoder_get_data_format function.
    /// Queries the sample format, channels, and sample rate from a decoder.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, ref MaFormat, ref uint, ref uint, IntPtr, ulong, MaResult> _decoderGetFormat;

    /// <summary>
    /// Function pointer to the native ma_decoder_get_cursor_in_pcm_frames function.
    /// Queries the current playback cursor position inside the decoder.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, ref ulong, MaResult> _decoderGetCursorPosition;

    /// <summary>
    /// Function pointer to the native ma_decoder_get_length_in_pcm_frames function.
    /// Queries the total number of PCM frames in the loaded stream.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, out ulong, MaResult> _decoderGetLength;

    /// <summary>
    /// Function pointer to the native sf_allocate_decoder function.
    /// Dynamically allocates memory for a decoder instance struct.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr> _allocateDecoder;

    /// <summary>
    /// Function pointer to the native sf_allocate_decoder_config function.
    /// Dynamically allocates memory for a decoder configuration struct.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<MaFormat, uint, uint, IntPtr> _allocateDecoderConfig;

    /// <summary>
    /// Function pointer to the native ma_encoder_init_file function.
    /// Initializes an audio encoder to write to a specified file.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, MaResult> _encoderInit;

    /// <summary>
    /// Function pointer to the native ma_encoder_uninit function.
    /// Uninitializes and finalizes resources used by an encoder.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _encoderUninit;

    /// <summary>
    /// Function pointer to the native ma_encoder_write_pcm_frames function.
    /// Encodes and writes PCM frames to the output file container.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, ulong, out ulong, MaResult> _encoderWrite;

    /// <summary>
    /// Function pointer to the native ma_resampler_init function.
    /// Initializes an audio resampler with the configured parameters.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<ref MaResamplerConfig, IntPtr, MaResult> _resamplerInit;

    /// <summary>
    /// Function pointer to the native ma_resampler_uninit function.
    /// Uninitializes and frees resources used by an audio resampler.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _resamplerUninit;

    /// <summary>
    /// Function pointer to the native ma_resampler_process_pcm_frames function.
    /// Processes audio samples to convert between different sample rates.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, ref ulong, IntPtr, ref ulong, MaResult> _resamplerProcess;

    /// <summary>
    /// Function pointer to the native ma_resampler_set_rate function.
    /// Changes the input/output sample rates dynamically on a resampler.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, uint, uint, MaResult> _resamplerSetRate;

    /// <summary>
    /// Function pointer to the native ma_resampler_config_init function.
    /// Initializes resampler settings structure with default parameters.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<MaFormat, MaFormat, uint, uint, uint, uint, MaResampleAlgorithm, MaResamplerConfig> _resamplerConfigInit;

    /// <summary>
    /// Function pointer to the native ma_free function.
    /// Frees unmanaged memory blocks allocated via miniaudio memory APIs.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _free;

    /// <summary>
    /// Function pointer to the native ma_result_description function.
    /// Retrieves static text describing a specific MiniAudio result code.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<MaResult, IntPtr> _getErrorString;

    /// <summary>
    /// Function pointer to the native ma_malloc function.
    /// Allocates unmanaged memory blocks through the MiniAudio allocator.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<ulong, IntPtr, IntPtr> _maMalloc;

    /// <summary>
    /// Function pointer to the native ma_free function with userData.
    /// Frees unmanaged memory blocks that were allocated using context userData.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> _maFree;

    /// <summary>
    /// The maximum length of a device name supported by miniaudio.h.
    /// Derived from the native headers where the value is defined as 255.
    /// </summary>
    public const int MA_MAX_DEVICE_NAME_LENGTH = 255;

    /// <summary>
    /// The maximum length of a device identifier supported by miniaudio.h.
    /// Set to 256 bytes to store native GUIDs or hardware path strings.
    /// </summary>
    public const int MA_MAX_DEVICE_ID_LENGTH = 256;

    /// <summary>
    /// The maximum number of native formats supported per audio device.
    /// Used for allocating descriptor arrays in device capability lists.
    /// </summary>
    public const int MA_MAX_NATIVE_DATA_FORMATS_PER_DEVICE = 256;

    /// <summary>
    /// The size of the native data format array in the device information struct.
    /// Defined to prevent memory overflows when copying format descriptors.
    /// </summary>
    public const int MA_DEVICE_INFO_NATIVE_DATA_FORMAT_ARRAY_SIZE = 64;

    /// <summary>
    /// Unmanaged delegate invoked by MiniAudio when a device needs to process audio frames.
    /// Handlers copy input samples or write output samples for real-time play/rec.
    /// </summary>
    /// <param name="pDevice">Pointer to the native device instance struct.</param>
    /// <param name="pOutput">Pointer to output buffer to write playback frames into.</param>
    /// <param name="pInput">Pointer to input buffer to read captured frames from.</param>
    /// <param name="frameCount">Number of frames to process in the callback.</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public unsafe delegate void MaDataCallback(
        IntPtr pDevice,
        void* pOutput,
        void* pInput,
        uint frameCount
    );

    /// <summary>
    /// Unmanaged delegate invoked by MiniAudio when a device is stopped.
    /// Used to notify the managed code of stream completion or hardware errors.
    /// </summary>
    /// <param name="pDevice">Pointer to the native device instance struct.</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public unsafe delegate void MaStopCallback(
        IntPtr pDevice
    );

    /// <summary>
    /// Unmanaged delegate invoked by MiniAudio during device enumeration loops.
    /// Called once for each detected audio device on the system.
    /// </summary>
    /// <param name="context">Pointer to the native context structure.</param>
    /// <param name="deviceType">Type of the device being enumerated (playback or capture).</param>
    /// <param name="deviceInfo">Pointer to the device information structure details.</param>
    /// <param name="pUserData">User data pointer passed to the enumeration method.</param>
    /// <returns>Result code indicating whether to continue enumeration.</returns>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public unsafe delegate MaResult MaEnumDevicesCallback(
        IntPtr context,
        MaDeviceType deviceType,
        IntPtr deviceInfo,
        void* pUserData
    );

    /// <summary>
    /// Unmanaged delegate invoked by a custom decoder to read raw audio data from a stream.
    /// Copies binary source data from a managed stream into native buffers.
    /// </summary>
    /// <param name="pDecoder">Pointer to the native decoder instance struct.</param>
    /// <param name="pBufferOut">Pointer to the native destination buffer to write into.</param>
    /// <param name="bytesToRead">Number of bytes requested by the decoder structure.</param>
    /// <param name="pBytesRead">Receives the number of bytes successfully read.</param>
    /// <returns>Result code indicating success or specific custom read errors.</returns>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public unsafe delegate MaResult DecoderReadProc(
        IntPtr pDecoder,
        IntPtr pBufferOut,
        UInt64 bytesToRead,
        out UInt64 pBytesRead
    );

    /// <summary>
    /// Unmanaged delegate invoked by a custom decoder to seek inside a stream.
    /// Repositions the reading cursor of a managed stream for decoding.
    /// </summary>
    /// <param name="pDecoder">Pointer to the native decoder instance struct.</param>
    /// <param name="byteOffset">The signed byte offset to seek to in the source.</param>
    /// <param name="origin">The starting reference point for the seek operation.</param>
    /// <returns>Result code indicating success or specific custom seek errors.</returns>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate MaResult DecoderSeekProc(
        IntPtr pDecoder,
        long byteOffset,
        SeekPoint origin
    );
}
