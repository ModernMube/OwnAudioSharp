using System;

namespace Ownaudio.Native.PortAudio;

internal static partial class PaBinding
{
    public enum PaSampleFormat : long
    {
        paFloat32 = 0x00000001,
        paInt32 = 0x00000002,
        paInt24 = 0x00000004,
        paInt16 = 0x00000008,
        paInt8 = 0x00000010,
        paUInt8 = 0x00000020,
        paCustomFormat = 0x00010000,
        paNonInterleaved = 0x80000000,
    }

    public enum PaStreamCallbackFlags : long
    {
        paInputUnderflow = 0x00000001,
        paInputOverflow = 0x00000002,
        paOutputUnderflow = 0x00000004,
        paOutputOverflow = 0x00000008,
        paPrimingOutput = 0x00000010
    }

    public enum PaStreamCallbackResult
    {
        paContinue = 0,
        paComplete = 1,
        paAbort = 2
    }

    public enum PaStreamFlags : long
    {
        paNoFlag = 0,
        paClipOff = 0x00000001,
        paDitherOff = 0x00000002,
        paPrimeOutputBuffersUsingStreamCallback = 0x00000008,
        paPlatformSpecificFlags = 0xFFFF0000
    }

    public enum PaHostApiTypeId
    {
        paInDevelopment = 0, /* use while developing support for a new host API */
        paDirectSound = 1,
        paMME = 2,
        paASIO = 3,
        paSoundManager = 4,
        paCoreAudio = 5,
        paOSS = 7,
        paALSA = 8,
        paAL = 9,
        paBeOS = 10,
        paWDMKS = 11,
        paJACK = 12,
        paWASAPI = 13,
        paAudioScienceHPI = 14
    }

    /// <summary>
    /// ASIO-specific stream flags for advanced configuration.
    /// </summary>
    [Flags]
    public enum PaAsioFlags : uint
    {
        /// <summary>
        /// Use channel selectors to specify which ASIO channels to use.
        /// When set, the channelSelectors field in PaAsioStreamInfo must point to an array of channel indices.
        /// </summary>
        UseChannelSelectors = 0x01
    }

    [Flags]
    public enum PaWasapiFlags
    {
        /// <summary>
        /// Puts WASAPI into exclusive mode
        /// </summary>
        Exclusive = 1 << 0,

        /// <summary>
        /// Allows to skip internal PA processing completely
        /// </summary>
        RedirectHostProcessor = 1 << 1,

        /// <summary>
        /// Assigns custom channel mask
        /// </summary>
        UseChannelMask = 1 << 2,

        /// <summary>
        /// Selects non-Event driven method of data read/write
        /// Note: WASAPI Event driven core is capable of 2ms latency, but Polling method can only provide 15-20ms latency.
        /// </summary>
        Polling = 1 << 3,

        /// <summary>
        /// Forces custom thread priority setting
        /// Must be used if PaWasapiStreamInfo.threadPriority is set to custom value
        /// </summary>
        ThreadPriority = 1 << 4
    }

    /// <summary>
    /// Thread priority levels for WASAPI operations.
    /// </summary>
    public enum PaWasapiThreadPriority
    {
        /// <summary>
        /// No specific priority.
        /// </summary>
        None = 0,

        /// <summary>
        /// Audio thread priority.
        /// </summary>
        Audio,

        /// <summary>
        /// Capture thread priority.
        /// </summary>
        Capture,

        /// <summary>
        /// Distribution thread priority.
        /// </summary>
        Distribution,

        /// <summary>
        /// Games thread priority.
        /// </summary>
        Games,

        /// <summary>
        /// Playback thread priority.
        /// </summary>
        Playback,

        /// <summary>
        /// Pro audio thread priority.
        /// </summary>
        ProAudio,

        /// <summary>
        /// Window manager thread priority.
        /// </summary>
        WindowManager
    }

    /// <summary>
    /// Specifies the category of the WASAPI audio stream, providing hints to the OS about its usage.
    /// </summary>
    public enum PaWasapiStreamCategory
    {
        /// <summary>
        /// General or uncategorized audio.
        /// </summary>
        Other = 0,

        /// <summary>
        /// Audio that should play only in the foreground.
        /// </summary>
        ForegroundOnlyMedia,

        /// <summary>
        /// Audio that can continue playing in the background.
        /// </summary>
        BackgroundCapableMedia,

        /// <summary>
        /// Audio used for communication, such as VoIP.
        /// </summary>
        Communications,

        /// <summary>
        /// Audio used for system alerts.
        /// </summary>
        Alerts,

        /// <summary>
        /// Sound effects, such as UI sounds.
        /// </summary>
        SoundEffects,

        /// <summary>
        /// Audio effects used in games.
        /// </summary>
        GameEffects,

        /// <summary>
        /// Media playback for games.
        /// </summary>
        GameMedia
    }

    /// <summary>
    /// Additional options for configuring the WASAPI audio stream.
    /// </summary>
    public enum PaWasapiStreamOption
    {
        /// <summary>
        /// No additional options.
        /// </summary>
        None = 0,

        /// <summary>
        /// Enables RAW mode for the audio stream, bypassing audio effects.
        /// </summary>
        Raw = 1
    }

    /// <summary>
    /// Passthrough formats for encoded audio streams in WASAPI.
    /// The values are derived from the Microsoft documentation "Representing Formats for IEC 61937 Transmissions."
    /// </summary>
    public enum PaWasapiPassthroughFormat : uint
    {
        /// <summary>
        /// PCM IEC 60958 format.
        /// </summary>
        PcmIec60958 = 0x00000000,

        /// <summary>
        /// Dolby Digital (AC3) format.
        /// </summary>
        DolbyDigital = 0x00920000,

        /// <summary>
        /// MPEG-1 format.
        /// </summary>
        Mpeg1 = 0x00030cea,

        /// <summary>
        /// MPEG-3 format.
        /// </summary>
        Mpeg3 = 0x00040cea,

        /// <summary>
        /// MPEG-2 format.
        /// </summary>
        Mpeg2 = 0x00050cea,

        /// <summary>
        /// AAC format.
        /// </summary>
        Aac = 0x00060cea,

        /// <summary>
        /// DTS format.
        /// </summary>
        Dts = 0x00080cea,

        /// <summary>
        /// Dolby Digital Plus format.
        /// </summary>
        DolbyDigitalPlus = 0x000a0cea,

        /// <summary>
        /// Dolby Digital Plus with Atmos format.
        /// </summary>
        DolbyDigitalPlusAtmos = 0x010a0cea,

        /// <summary>
        /// DTS-HD format.
        /// </summary>
        DtsHd = 0x000b0cea,

        /// <summary>
        /// DTS X E1 format.
        /// </summary>
        DtsXE1 = 0x010b0cea,

        /// <summary>
        /// DTS X E2 format.
        /// </summary>
        DtsXE2 = 0x030b0cea,

        /// <summary>
        /// Dolby MLP format.
        /// </summary>
        DolbyMlp = 0x000c0cea,

        /// <summary>
        /// Dolby MAT 2.0 format.
        /// </summary>
        DolbyMat20 = 0x010c0cea,

        /// <summary>
        /// Dolby MAT 2.1 format.
        /// </summary>
        DolbyMat21 = 0x030c0cea,

        /// <summary>
        /// WMA Pro format.
        /// </summary>
        WmaPro = 0x01640000,

        /// <summary>
        /// ATRAC format.
        /// </summary>
        Atrac = 0x00080cea,

        /// <summary>
        /// One-bit audio format.
        /// </summary>
        OneBitAudio = 0x00090cea,

        /// <summary>
        /// Direct Stream Transfer (DST) format.
        /// </summary>
        Dst = 0x000d0cea
    }
}
