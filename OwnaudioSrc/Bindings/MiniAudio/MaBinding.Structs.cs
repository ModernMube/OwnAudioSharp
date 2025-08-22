using System;
using System.Runtime.InteropServices;
using System.Text;
using static Ownaudio.Bindings.Miniaudio.MaBinding;

namespace Ownaudio.Bindings.Miniaudio;

internal static partial class MaBinding
{
    /// <summary>
    /// Field representing struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MaDeviceConfig
    {
        /// <summary>
        /// Field representing MaDeviceType.
        /// </summary>
        public MaDeviceType deviceType;

        /// <summary>
        /// Field representing uint.
        /// </summary>
        public uint sampleRate;

        /// <summary>
        /// Field representing uint.
        /// </summary>
        public uint periodSizeInFrames;

        /// <summary>
        /// Field representing uint.
        /// </summary>
        public uint periodSizeInMilliseconds;

        /// <summary>
        /// Field representing uint.
        /// </summary>
        public uint periods;

        /// <summary>
        /// Field representing MaPerformanceProfile.
        /// </summary>
        public MaPerformanceProfile performanceProfile;

        /// <summary>
        /// Field representing Mabool8.
        /// </summary>
        public Mabool8 noPreSilencedOutputBuffer;

        /// <summary>
        /// Field representing Mabool8.
        /// </summary>
        public Mabool8 noClip;

        /// <summary>
        /// Field representing Mabool8.
        /// </summary>
        public Mabool8 noDisableDenormals;

        /// <summary>
        /// Field representing Mabool8.
        /// </summary>
        public Mabool8 noFixedSizedCallback;

        /// <summary>
        /// Field representing MaDataCallback.
        /// </summary>
        public MaDataCallback dataCallback;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr notificationCallback;

        /// <summary>
        /// Field representing MaStopCallback.
        /// </summary>
        public MaStopCallback stopCallback;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr pUserData;

        /// <summary>
        /// Field representing MaResamplerConfig.
        /// </summary>
        public MaResamplerConfig resampling;

        /// <summary>
        /// Field representing MaDevicePlaybackConfig.
        /// </summary>
        public MaDevicePlaybackConfig playback;

        /// <summary>
        /// Field representing MaDeviceCaptureConfig.
        /// </summary>
        public MaDeviceCaptureConfig capture;

        /// <summary>
        /// Field representing MaWasapiConfig.
        /// </summary>
        public MaWasapiConfig wasapi;

        /// <summary>
        /// Field representing MaAlsaConfig.
        /// </summary>
        public MaAlsaConfig alsa;

        /// <summary>
        /// Field representing MaPulseConfig.
        /// </summary>
        public MaPulseConfig pulse;

        /// <summary>
        /// Field representing MaCoreAudioConfig.
        /// </summary>
        public MaCoreAudioConfig coreaudio;

        /// <summary>
        /// Field representing MaOpenSLConfig.
        /// </summary>
        public MaOpenSLConfig opensl;

        /// <summary>
        /// Field representing MaAAudioConfig.
        /// </summary>
        public MaAAudioConfig aaudio;
    }

    /// <summary>
    /// Field representing struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MaDevicePlaybackConfig
    {
        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr pDeviceID;

        /// <summary>
        /// Field representing MaFormat.
        /// </summary>
        public MaFormat format;

        /// <summary>
        /// Field representing uint.
        /// </summary>
        public uint channels;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr pChannelMap;

        /// <summary>
        /// Field representing MaChannelMixMode.
        /// </summary>
        public MaChannelMixMode channelMixMode;

        /// <summary>
        /// Field representing Mabool32.
        /// </summary>
        public Mabool32 calculateLFEFromSpatialChannels;

        /// <summary>
        /// Field representing MaShareMode.
        /// </summary>
        public MaShareMode shareMode;
    }

    /// <summary>
    /// Field representing struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MaDeviceCaptureConfig
    {
        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr pDeviceID;

        /// <summary>
        /// Field representing MaFormat.
        /// </summary>
        public MaFormat format;

        /// <summary>
        /// Field representing uint.
        /// </summary>
        public uint channels;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr pChannelMap;

        /// <summary>
        /// Field representing MaChannelMixMode.
        /// </summary>
        public MaChannelMixMode channelMixMode;

        /// <summary>
        /// Field representing Mabool32.
        /// </summary>
        public Mabool32 calculateLFEFromSpatialChannels;

        /// <summary>
        /// Field representing MaShareMode.
        /// </summary>
        public MaShareMode shareMode;
    }

    /// <summary>
    /// Field representing struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MaWasapiConfig
    {
        /// <summary>
        /// Field representing MaWasapiUsage.
        /// </summary>
        public MaWasapiUsage usage;

        /// <summary>
        /// Field representing Mabool8.
        /// </summary>
        public Mabool8 noAutoConvertSRC;

        /// <summary>
        /// Field representing Mabool8.
        /// </summary>
        public Mabool8 noDefaultQualitySRC;

        /// <summary>
        /// Field representing Mabool8.
        /// </summary>
        public Mabool8 noAutoStreamRouting;

        /// <summary>
        /// Field representing Mabool8.
        /// </summary>
        public Mabool8 noHardwareOffloading;

        /// <summary>
        /// Field representing uint.
        /// </summary>
        public uint loopbackProcessID;

        /// <summary>
        /// Field representing Mabool8.
        /// </summary>
        public Mabool8 loopbackProcessExclude;
    }

    /// <summary>
    /// Field representing struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MaAlsaConfig
    {
        /// <summary>
        /// Field representing Mabool32.
        /// </summary>
        public Mabool32 noMMap;

        /// <summary>
        /// Field representing Mabool32.
        /// </summary>
        public Mabool32 noAutoFormat;

        /// <summary>
        /// Field representing Mabool32.
        /// </summary>
        public Mabool32 noAutoChannels;

        /// <summary>
        /// Field representing Mabool32.
        /// </summary>
        public Mabool32 noAutoResample;
    }

    /// <summary>
    /// Field representing struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MaPulseConfig
    {
        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr pStreamNamePlayback;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr pStreamNameCapture;

        /// <summary>
        /// Field representing int.
        /// </summary>
        public int channelMap;
    }

    /// <summary>
    /// Field representing struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MaCoreAudioConfig
    {
        /// <summary>
        /// Field representing Mabool32.
        /// </summary>
        public Mabool32 allowNominalSampleRateChange;
    }

    /// <summary>
    /// Field representing struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MaOpenSLConfig
    {
        /// <summary>
        /// Field representing MaOpenSLStreamType.
        /// </summary>
        public MaOpenSLStreamType streamType;

        /// <summary>
        /// Field representing MaOpenSLRecordingPreset.
        /// </summary>
        public MaOpenSLRecordingPreset recordingPreset;

        /// <summary>
        /// Field representing Mabool32.
        /// </summary>
        public Mabool32 enableCompatibilityWorkarounds;
    }

    /// <summary>
    /// Field representing struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MaAAudioConfig
    {
        /// <summary>
        /// Field representing MaAAudioUsage.
        /// </summary>
        public MaAAudioUsage usage;

        /// <summary>
        /// Field representing MaAAudioContentType.
        /// </summary>
        public MaAAudioContentType contentType;

        /// <summary>
        /// Field representing MaAAudioInputPreset.
        /// </summary>
        public MaAAudioInputPreset inputPreset;

        /// <summary>
        /// Field representing MaAAudioAllowedCapturePolicy.
        /// </summary>
        public MaAAudioAllowedCapturePolicy allowedCapturePolicy;

        /// <summary>
        /// Field representing Mabool32.
        /// </summary>
        public Mabool32 noAutoStartAfterReroute;

        /// <summary>
        /// Field representing Mabool32.
        /// </summary>
        public Mabool32 enableCompatibilityWorkarounds;

        /// <summary>
        /// Field representing Mabool32.
        /// </summary>
        public Mabool32 allowSetBufferCapacity;
    }

    /// <summary>
    /// A descriptor of the native data format as listed in ma_device_info.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MaNativeDataFormatDescriptor
    {
        /// <summary>
        /// Mintavételi formátum. Ha ma_format_unknown, minden formátum támogatott.
        /// </summary>
        public MaFormat format;

        /// <summary>
        /// Csatornák száma. Ha 0, minden csatornaszám támogatott.
        /// </summary>
        public uint channels;

        /// <summary>
        /// Mintavételi ráta. Ha 0, minden mintavételi ráta támogatott.
        /// </summary>
        public uint sampleRate;

        /// <summary>
        /// MA_DATA_FORMAT_FLAG_* jelzőbitek kombinációja.
        /// </summary>
        public uint flags;
    }

    /// <summary>
    /// The id of the device information
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MaDeviceId
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MA_MAX_DEVICE_ID_LENGTH)]
        public byte[] id;
    }

    /// <summary>
    /// Field representing struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct MaDeviceInfo
    {
        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public MaDeviceId Id;

        /// <summary>
        /// Field representing byte array.
        /// </summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaBinding.MA_MAX_DEVICE_NAME_LENGTH + 1)]
        public byte[] nameBytes;

        /// <summary>
        /// Field representing bool.
        /// </summary>
        public Mabool32 isDefault;

        /// <summary>
        /// Field representing uint.
        /// </summary>
        public uint NativeDataFormatCount;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MA_DEVICE_INFO_NATIVE_DATA_FORMAT_ARRAY_SIZE)]
        public MaNativeDataFormatDescriptor[] nativeDataFormats;

        /// <summary>
        /// Returns the device name as a C# string (with UTF-8 decoding).
        /// </summary>
        public string Name
        {
            get
            {
                if (nameBytes == null) return string.Empty;  // Find the first null terminator in the byte array
                int firstNull = Array.IndexOf(nameBytes, (byte)0);
                int length = (firstNull == -1) ? nameBytes.Length : firstNull; // The length of the string up to the null terminator, or the total length of the array if there is no null terminator
                return Encoding.UTF8.GetString(nameBytes, 0, length);
            }
        }

        /// <summary>
        /// Returns whether the device is default as a C# bool value.
        /// </summary>
        public bool IsDefault
        {
            get { return isDefault != Mabool32.False; } // MA_FALSE (0), MA_TRUE (1 or anything non-zero)
        }
    }

    /// <summary>
    /// Field representing struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MaContextConfig
    {
        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr allocationCallbacks;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr pUserData;

        /// <summary>
        /// Field representing bool.
        /// </summary>
        public bool threadPrioritiesEnabled;

        /// <summary>
        /// Field representing int.
        /// </summary>
        public int threadPriority;

        /// <summary>
        /// Field representing uint.
        /// </summary>
        public uint threadStackSize;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr pLog;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr notificationCallback;

        /// <summary>
        /// Field representing bool.
        /// </summary>
        public bool coreaudio_sessionCategory;

        /// <summary>
        /// Field representing bool.
        /// </summary>
        public bool coreaudio_sessionCategoryOptions;

        /// <summary>
        /// Field representing bool.
        /// </summary>
        public bool coreaudio_noAudioSessionActivate;

        /// <summary>
        /// Field representing bool.
        /// </summary>
        public bool coreaudio_noAudioSessionDeactivate;

        /// <summary>
        /// Field representing string.
        /// </summary>
        public string? jack_pClientName;

        /// <summary>
        /// Field representing bool.
        /// </summary>
        public bool jack_tryStartServer;

        /// <summary>
        /// Field representing string.
        /// </summary>
        public string? pulse_pApplicationName;

        /// <summary>
        /// Field representing string.
        /// </summary>
        public string? pulse_pServerName;

        /// <summary>
        /// Field representing bool.
        /// </summary>
        public bool pulse_tryAutoSpawn;

        /// <summary>
        /// Field representing string.
        /// </summary>
        public string? alsa_pcm_device;

        /// <summary>
        /// Field representing bool.
        /// </summary>
        public bool alsa_useVerboseDeviceEnumeration;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr custom_pContextUserData;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr custom_customBackendFunctions;
    }

    /// <summary>
    /// Field representing struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MaDeviceID
    {
        private IntPtr pWasapi;
        private IntPtr pDSound;
        private IntPtr pWinMM;
        private IntPtr pALSA;
        private IntPtr pPulse;
        private IntPtr pAAudio;
        private IntPtr pOpenSL;
        private IntPtr pJack;
        private IntPtr pCoreAudio;
    }

    /// <summary>
    /// Structure of the configuration for Decoder
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MaDecoderConfig
    {
        public MaFormat format;
        public uint channels;
        public uint sampleRate;
        public MaChannelMixMode channelMixMode;

        public IntPtr pChannelMap;

        public MaDitherMode ditherMode;
        public MaResamplerConfig resampling;
        public IntPtr allocationCallbacks;
        public MaEncodingFormat encodingFormat;
        public uint seekPointCount;
        public IntPtr ppCustomBackendVTables;
        public uint customBackendCount;
        public IntPtr pCustomBackendUserData;
    }

    /// <summary>
    /// Field representing struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MaResamplerConfig
    {
        /// <summary>
        /// Field representing MaFormat.
        /// </summary>
        public MaFormat format;

        /// <summary>
        /// Field representing uint.
        /// </summary>
        public uint channels;

        /// <summary>
        /// Field representing uint.
        /// </summary>
        public uint sampleRateIn;

        /// <summary>
        /// Field representing uint.
        /// </summary>
        public uint sampleRateOut;

        /// <summary>
        /// Field representing MaResampleAlgorithm.
        /// </summary>
        public MaResampleAlgorithm algorithm;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr pAllocationCallbacks;

        /// <summary>
        /// Field representing MaResamplerLinearConfig.
        /// </summary>
        public MaResamplerLinearConfig linear;

        /// <summary>
        /// Field representing MaResamplerSincConfig.
        /// </summary>
        public MaResamplerSincConfig sinc;
    }

    /// <summary>
    /// Field representing struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MaResamplerLinearConfig
    {
        /// <summary>
        /// Field representing uint.
        /// </summary>
        public uint lpfOrder;
    }

    /// <summary>
    /// Field representing struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MaResamplerSincConfig
    {
        /// <summary>
        /// Field representing MaSincResamplerWindowFunction.
        /// </summary>
        public MaSincResamplerWindowFunction windowFunction;

        /// <summary>
        /// Field representing double.
        /// </summary>
        public double windowWidth;

        /// <summary>
        /// Field representing double.
        /// </summary>
        public double transitionWidth;
    }

    /// <summary>
    /// Field representing struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MaDecoder
    {
        /// <summary>
        /// Field representing MaDataSourceBase.
        /// </summary>
        public MaDataSourceBase dataSource;

        /// <summary>
        /// Field representing MaDecoderConfig.
        /// </summary>
        public MaDecoderConfig config;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        private byte[] _reserved;
    }

    /// <summary>
    /// Field representing struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MaDataSourceBase
    {
        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr onRead;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr onSeek;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr pUserData;
    }

    /// <summary>
    /// Field representing struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MaEncoder
    {
        /// <summary>
        /// Field representing MaEncoderConfig.
        /// </summary>
        public MaEncoderConfig config;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr onWrite;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr onSeek;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr onInit;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr onUninit;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr onWritePCMFrames;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr pUserData;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr pInternalEncoder;

        /// <summary>
        /// Field representing MaEncoderData.
        /// </summary>
        public MaEncoderData data;
    }

    /// <summary>
    /// Field representing struct.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct MaEncoderData
    {
        /// <summary>
        /// Field representing MaEncoderVFS.
        /// </summary>
        [FieldOffset(0)] public MaEncoderVFS vfs;
    }

    /// <summary>
    /// Field representing struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MaEncoderVFS
    {
        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr pVFS;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr file;
    }

    /// <summary>
    /// Field representing struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MaContext
    {
        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr callbacks;

        /// <summary>
        /// Field representing MaBackend.
        /// </summary>
        public MaBackend backend;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr pLog;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr log;

        /// <summary>
        /// Field representing int.
        /// </summary>
        public int threadPriority;

        /// <summary>
        /// Field representing UIntPtr.
        /// </summary>
        public UIntPtr threadStackSize;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr pUserData;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr allocationCallbacks;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr deviceEnumLock;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr deviceInfoLock;

        /// <summary>
        /// Field representing uint.
        /// </summary>
        public uint deviceInfoCapacity;

        /// <summary>
        /// Field representing uint.
        /// </summary>
        public uint playbackDeviceInfoCount;

        /// <summary>
        /// Field representing uint.
        /// </summary>
        public uint captureDeviceInfoCount;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr pDeviceInfos;

        /// <summary>
        /// Field representing byte.
        /// </summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4096)]
        public byte[] platformSpecificData;

        /// <summary>
        /// Field representing byte.
        /// </summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public byte[] osSpecificData;
    }

    /// <summary>
    /// Field representing struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MaDevice
    {
        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr pContext;

        /// <summary>
        /// Field representing MaDeviceType.
        /// </summary>
        public MaDeviceType type;

        /// <summary>
        /// Field representing uint.
        /// </summary>
        public uint sampleRate;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr state;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr onData;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr onNotification;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr onStop;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr pUserData;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr startStopLock;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr wakeupEvent;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr startEvent;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr stopEvent;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr thread;

        /// <summary>
        /// Field representing MaResult.
        /// </summary>
        public MaResult workResult;

        /// <summary>
        /// Field representing byte.
        /// </summary>
        public byte isOwnerOfContext;

        /// <summary>
        /// Field representing byte.
        /// </summary>
        public byte noPreSilencedOutputBuffer;

        /// <summary>
        /// Field representing byte.
        /// </summary>
        public byte noClip;

        /// <summary>
        /// Field representing byte.
        /// </summary>
        public byte noDisableDenormals;

        /// <summary>
        /// Field representing byte.
        /// </summary>
        public byte noFixedSizedCallback;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr masterVolumeFactor;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr duplexRB;

        /// <summary>
        /// Field representing struct.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]

        public struct ResamplingInfo
        {
            /// <summary>
            /// Field representing int.
            /// </summary>
            public int algorithm;

            /// <summary>
            /// Field representing IntPtr.
            /// </summary>
            public IntPtr pBackendVTable;

            /// <summary>
            /// Field representing IntPtr.
            /// </summary>
            public IntPtr pBackendUserData;

            /// <summary>
            /// Field representing struct.
            /// </summary>
            [StructLayout(LayoutKind.Sequential)]

            public struct LinearInfo
            {
                /// <summary>
                /// Field representing uint.
                /// </summary>
                public uint lpfOrder;
            }

            /// <summary>
            /// Field representing LinearInfo.
            /// </summary>
            public LinearInfo linear;
        }

        /// <summary>
        /// Field representing ResamplingInfo.
        /// </summary>
        public ResamplingInfo resampling;

        /// <summary>
        /// Field representing struct.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct PlaybackInfo
        {
            /// <summary>
            /// Field representing IntPtr.
            /// </summary>
            public IntPtr pID;

            /// <summary>
            /// Field representing IntPtr.
            /// </summary>
            public IntPtr id;

            /// <summary>
            /// Field representing byte.
            /// </summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public byte[] name;

            /// <summary>
            /// Field representing int.
            /// </summary>
            public int shareMode;

            /// <summary>
            /// Field representing MaFormat.
            /// </summary>
            public MaFormat format;

            /// <summary>
            /// Field representing uint.
            /// </summary>
            public uint channels;

            /// <summary>
            /// Field representing int.
            /// </summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public int[] channelMap;

            /// <summary>
            /// Field representing MaFormat.
            /// </summary>
            public MaFormat internalFormat;

            /// <summary>
            /// Field representing uint.
            /// </summary>
            public uint internalChannels;

            /// <summary>
            /// Field representing uint.
            /// </summary>
            public uint internalSampleRate;

            /// <summary>
            /// Field representing int.
            /// </summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public int[] internalChannelMap;

            /// <summary>
            /// Field representing uint.
            /// </summary>
            public uint internalPeriodSizeInFrames;

            /// <summary>
            /// Field representing uint.
            /// </summary>
            public uint internalPeriods;

            /// <summary>
            /// Field representing int.
            /// </summary>
            public int channelMixMode;

            /// <summary>
            /// Field representing int.
            /// </summary>
            public int calculateLFEFromSpatialChannels;

            /// <summary>
            /// Field representing IntPtr.
            /// </summary>
            public IntPtr converter;

            /// <summary>
            /// Field representing IntPtr.
            /// </summary>
            public IntPtr pIntermediaryBuffer;

            /// <summary>
            /// Field representing uint.
            /// </summary>
            public uint intermediaryBufferCap;

            /// <summary>
            /// Field representing uint.
            /// </summary>
            public uint intermediaryBufferLen;

            /// <summary>
            /// Field representing IntPtr.
            /// </summary>
            public IntPtr pInputCache;

            /// <summary>
            /// Field representing ulong.
            /// </summary>
            public ulong inputCacheCap;

            /// <summary>
            /// Field representing ulong.
            /// </summary>
            public ulong inputCacheConsumed;

            /// <summary>
            /// Field representing ulong.
            /// </summary>
            public ulong inputCacheRemaining;
        }

        /// <summary>
        /// Field representing PlaybackInfo.
        /// </summary>
        public PlaybackInfo playback;

        /// <summary>
        /// Field representing struct.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct CaptureInfo
        {
            /// <summary>
            /// Field representing IntPtr.
            /// </summary>
            public IntPtr pID;

            /// <summary>
            /// Field representing IntPtr.
            /// </summary>
            public IntPtr id;

            /// <summary>
            /// Field representing byte.
            /// </summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public byte[] name;

            /// <summary>
            /// Field representing int.
            /// </summary>
            public int shareMode;

            /// <summary>
            /// Field representing MaFormat.
            /// </summary>
            public MaFormat format;

            /// <summary>
            /// Field representing uint.
            /// </summary>
            public uint channels;

            /// <summary>
            /// Field representing int.
            /// </summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public int[] channelMap;

            /// <summary>
            /// Field representing MaFormat.
            /// </summary>
            public MaFormat internalFormat;

            /// <summary>
            /// Field representing uint.
            /// </summary>
            public uint internalChannels;

            /// <summary>
            /// Field representing uint.
            /// </summary>
            public uint internalSampleRate;

            /// <summary>
            /// Field representing int.
            /// </summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public int[] internalChannelMap;

            /// <summary>
            /// Field representing uint.
            /// </summary>
            public uint internalPeriodSizeInFrames;

            /// <summary>
            /// Field representing uint.
            /// </summary>
            public uint internalPeriods;

            /// <summary>
            /// Field representing int.
            /// </summary>
            public int channelMixMode;

            /// <summary>
            /// Field representing int.
            /// </summary>
            public int calculateLFEFromSpatialChannels;

            /// <summary>
            /// Field representing IntPtr.
            /// </summary>
            public IntPtr converter;

            /// <summary>
            /// Field representing IntPtr.
            /// </summary>
            public IntPtr pIntermediaryBuffer;

            /// <summary>
            /// Field representing uint.
            /// </summary>
            public uint intermediaryBufferCap;

            /// <summary>
            /// Field representing uint.
            /// </summary>
            public uint intermediaryBufferLen;
        }

        /// <summary>
        /// Field representing CaptureInfo.
        /// </summary>
        public CaptureInfo capture;

        /// <summary>
        /// Field representing byte.
        /// </summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8192)]
        public byte[] backendSpecificData;
    }

    /// <summary>
    /// Field representing struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MaEncoderConfig
    {
        /// <summary>
        /// Field representing MaEncodingFormat.
        /// </summary>
        public MaEncodingFormat encodingFormat;

        /// <summary>
        /// Field representing MaFormat.
        /// </summary>
        public MaFormat format;

        /// <summary>
        /// Field representing uint.
        /// </summary>
        public uint channels;

        /// <summary>
        /// Field representing uint.
        /// </summary>
        public uint sampleRate;

        /// <summary>
        /// Field representing uint.
        /// </summary>
        public uint allocationCallbacks;

        /// <summary>
        /// Field representing IntPtr.
        /// </summary>
        public IntPtr vfs;

        /// <summary>
        /// Field representing struct.
        /// </summary>
        [StructLayout(LayoutKind.Explicit)]
        public struct FormatSpecificConfig
        {
            /// <summary>
            /// Field representing WavConfig.
            /// </summary>
            [FieldOffset(0)] public WavConfig wav;

            /// <summary>
            /// Field representing FlacConfig.
            /// </summary>
            [FieldOffset(0)] public FlacConfig flac;
        }

        /// <summary>
        /// Field representing FormatSpecificConfig.
        /// </summary>
        public FormatSpecificConfig formatConfig;

        /// <summary>
        /// Field representing struct.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct WavConfig
        {

        }

        /// <summary>
        /// Field representing struct.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct FlacConfig
        {
            /// <summary>
            /// Field representing int.
            /// </summary>
            public int compressionLevel;

        }
    }
}
