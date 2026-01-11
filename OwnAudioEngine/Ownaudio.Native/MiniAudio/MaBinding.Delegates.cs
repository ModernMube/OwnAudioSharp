using System;
using System.Text;
using System.Runtime.InteropServices;

namespace Ownaudio.Native.MiniAudio;

internal static partial class MaBinding
{
    private static ContextInit? _contextInit;
    private static ContextUninit? _contextUninit;
    private static ContextGetDeviceInfo? _contextGetDeviceInfo;
    private static ContextGetDevices? _contextGetDevices;
    private static ContextEnumerateDevices? _contextEnumerateDevices;
    //private static AllocateContext? _allocateContext;

    private static DeviceInit? _deviceInit;
    private static DeviceUninit? _deviceUninit;
    private static DeviceStart? _deviceStart;
    private static DeviceStop? _deviceStop;
    private static DeviceIsStarted? _deviceIsStarted;
    private static DeviceGetInfo? _deviceGetInfo;
    private static DeviceGetContext? _deviceGetContext;
    private static GetDevices? _getDevices;
    //private static AllocateDevice? _allocateDevice;
    //private static AllocateDeviceConfig? _allocateDeviceConfig;
    private static MaDeviceConfigInit? _maDeviceConfigInit;

    private static EngineInit? _engineInit;
    private static EngineUninit? _engineUninit;
    private static EnginePlay? _enginePlay;

    private static SoundInit? _soundInit;
    private static SoundUninit? _soundUninit;
    private static SoundStart? _soundStart;
    private static SoundStop? _soundStop;
    private static SoundIsPlaying? _soundIsPlaying;
    private static SoundSetVolume? _soundSetVolume;

    private static DecoderInit? _decoderInit;
    private static DecoderUninit? _decoderUninit;
    private static DecoderRead? _decoderRead;
    private static DecoderSeek? _decoderSeek;
    private static DecoderConfigInit? _decoderConfigInit;
    private static DecoderInitStream? _decoderInitStream;
    private static DecoderGetFormat? _decoderGetFormat;
    private static DecoderGetCursorPosition? _decoderGetCursorPosition;
    private static DecoderGetLength? _decoderGetLength;
    private static AllocateDecoder? _allocateDecoder;
    private static AllocateDecoderConfig? _allocateDecoderConfig;

    private static EncoderInit? _encoderInit;
    private static EncoderUninit? _encoderUninit;
    private static EncoderWrite? _encoderWrite;
    //private static AllocateEncoder? _allocateEncoder;
    //private static AllocateEncoderConfig? _allocateEncoderConfig;

    private static ResamplerInit? _resamplerInit;
    private static ResamplerUninit? _resamplerUninit;
    private static ResamplerProcess? _resamplerProcess;
    private static ResamplerSetRate? _resamplerSetRate;
    private static ResamplerConfigInit? _resamplerConfigInit;

    private static Free? _free;
    private static GetErrorString? _getErrorString;
    private static MaMalloc? _maMalloc;
    private static MaFree? _maFree;

    public const int MA_MAX_DEVICE_NAME_LENGTH = 256;
    public const int MA_MAX_DEVICE_ID_LENGTH = 256;
    public const int MA_MAX_NATIVE_DATA_FORMATS_PER_DEVICE = 256;
    public const int MA_DEVICE_INFO_NATIVE_DATA_FORMAT_ARRAY_SIZE = 64;

    #region Public Delegates
    public unsafe delegate void MaDataCallback(
        IntPtr pDevice,
        void* pOutput,
        void* pInput,
        uint frameCount
    );

    public unsafe delegate void MaStopCallback(
        IntPtr pDevice
    );

    public unsafe delegate MaResult MaEnumDevicesCallback(
        IntPtr context,
        MaDeviceType deviceType,
        IntPtr deviceInfo,
        void* pUserData
    );

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public unsafe delegate MaResult DecoderReadProc(
        IntPtr pDecoder,
        IntPtr pBufferOut,
        UInt64 bytesToRead,
        out UInt64 pBytesRead
    );

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate MaResult DecoderSeekProc(
        IntPtr pDecoder,
        long byteOffset,
        SeekPoint origin
    );
    #endregion

    #region Context Delegates
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate MaResult ContextInit(nint pBackends, uint backendCount, nint config, nint context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ContextUninit(IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate MaResult ContextGetDeviceInfo(IntPtr context, MaDeviceType deviceType, IntPtr deviceId, IntPtr deviceInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate MaResult ContextGetDevices(
        IntPtr context,
        IntPtr* ppPlaybackDeviceInfos,
        int* pPlaybackDeviceCount,
        IntPtr* ppCaptureDeviceInfos,
        int* pCaptureDeviceCount
    );

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate MaResult ContextEnumerateDevices(IntPtr context, MaEnumDevicesCallback callback, IntPtr pUserData);

    //[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    //private delegate IntPtr AllocateContext();
    #endregion

    #region Device delegates
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate MaResult DeviceInit(IntPtr context, IntPtr config, IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DeviceUninit(IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate MaResult DeviceStart(IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate MaResult DeviceStop(IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool DeviceIsStarted(IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate MaResult DeviceGetInfo(IntPtr device, MaDeviceType type, IntPtr deviceInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr DeviceGetContext(IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate MaResult GetDevices(IntPtr context, out IntPtr pPlaybackDevices, out IntPtr pCaptureDevices, out IntPtr playbackDeviceCount, out IntPtr captureDeviceCount);

    //[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    //private delegate IntPtr AllocateDevice();

    //[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    //private delegate IntPtr AllocateDeviceConfig(MaDeviceType capabilityType, MaFormat format, uint channels, uint sampleRate, MaDataCallback dataCallback, IntPtr playbackDevice, IntPtr captureDevice);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate MaDeviceConfig MaDeviceConfigInit(MaDeviceType deviceType);
    #endregion

    #region Engine delegates
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate MaResult EngineInit(IntPtr engineConfig, IntPtr engine);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void EngineUninit(IntPtr engine);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate MaResult EnginePlay(IntPtr engine, string filePath, IntPtr pGroup);
    #endregion

    #region Sound delegates
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate MaResult SoundInit(IntPtr engine, string filePath, uint flags, IntPtr pGroup, IntPtr pAllocationCallbacks, IntPtr pSound);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SoundUninit(IntPtr sound);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate MaResult SoundStart(IntPtr sound);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate MaResult SoundStop(IntPtr sound);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool SoundIsPlaying(IntPtr sound);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SoundSetVolume(IntPtr sound, float volume);
    #endregion

    #region Decoder delegates
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate MaResult DecoderInit([MarshalAs(UnmanagedType.LPStr)] string filePath, [In] ref MaDecoderConfig config, IntPtr decoder);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DecoderUninit(IntPtr decoder);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate MaResult DecoderRead(
        IntPtr decoder,
        IntPtr pFramesOut,
        ulong frameCount,
        out ulong pFramesRead
    );

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate MaResult DecoderSeek(IntPtr decoder, ulong frameIndex);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate MaDecoderConfig DecoderConfigInit(MaFormat format, uint channels, uint sampleRate);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate MaResult DecoderInitStream(
        DecoderReadProc readCallback,
        DecoderSeekProc seekCallback,
        IntPtr pUserData,
        IntPtr config,
        IntPtr decoder);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate MaResult DecoderGetFormat(IntPtr decoder, ref MaFormat pFormat, ref uint pChannels, ref uint pSampleRate, IntPtr pChannelMap, ulong channelMapCap);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate MaResult DecoderGetCursorPosition(IntPtr decoder, ref ulong pCursor);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate MaResult DecoderGetLength(IntPtr decoder, out ulong pLength);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AllocateDecoder();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AllocateDecoderConfig(MaFormat format, uint channels, uint sampleRate);
    #endregion

    #region Encoder delegates
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate MaResult EncoderInit([MarshalAs(UnmanagedType.LPStr)] string filePath, IntPtr pConfig, IntPtr pEncoder);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void EncoderUninit(IntPtr pEncoder);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate MaResult EncoderWrite(IntPtr pEncoder, IntPtr pFramesIn, ulong frameCount, out ulong pFramesWritten);

    #endregion

    #region Resampler delegates  
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate MaResult ResamplerInit(ref MaResamplerConfig pConfig, IntPtr pResampler);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ResamplerUninit(IntPtr pResampler);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate MaResult ResamplerProcess(IntPtr pResampler, IntPtr pFramesIn, ref ulong pFrameCountIn, IntPtr pFramesOut, ref ulong pFrameCountOut);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate MaResult ResamplerSetRate(IntPtr pResampler, uint rateIn, uint rateOut);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate MaResamplerConfig ResamplerConfigInit(
        MaFormat formatIn,
        MaFormat formatOut,
        uint channelsIn,
        uint channelsOut,
        uint sampleRateIn,
        uint sampleRateOut,
        MaResampleAlgorithm algorithm
    );

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong ResamplerGetRequiredSize(ref MaResamplerConfig pConfig);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong ResamplerGetExpectedOutputFrameCount(IntPtr pResampler, ulong inputFrameCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate MaResult ResamplerGetRatio(IntPtr pResampler, ref double pRatio);
    #endregion

    #region Helper and Memory Management Delegates
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Free(IntPtr ptr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetErrorString(MaResult result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr MaMalloc(ulong size, IntPtr pUserData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MaFree(IntPtr p, IntPtr pUserData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MaZeroMemory(IntPtr p, ulong size);
    #endregion
}
