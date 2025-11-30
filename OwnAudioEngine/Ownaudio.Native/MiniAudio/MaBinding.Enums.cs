using System;

namespace Ownaudio.Native.MiniAudio;

internal static partial class MaBinding
{
    /// <summary>
    /// Miniaudio result codes - aligned with MiniAudio.Enums.Result values
    /// </summary>
    public enum MaResult
    {
        /// <summary>Operation completed successfully</summary>
        Success = 0,
        /// <summary>General error</summary>
        Error = -1,
        /// <summary>Invalid arguments were provided</summary>
        InvalidArgs = -2,
        /// <summary>The operation was invalid in the current context</summary>
        InvalidOperation = -3,
        /// <summary>Failed to allocate memory</summary>
        OutOfMemory = -4,
        /// <summary>Value is out of valid range</summary>
        OutOfRange = -5,
        /// <summary>Access to resource was denied</summary>
        AccessDenied = -6,
        /// <summary>The requested resource does not exist</summary>
        DoesNotExist = -7,
        /// <summary>The resource already exists</summary>
        AlreadyExists = -8,
        /// <summary>Too many files are open</summary>
        TooManyOpenFiles = -9,
        /// <summary>The file is invalid or corrupted</summary>
        InvalidFile = -10,
        /// <summary>The resource is too large</summary>
        TooBig = -11,
        /// <summary>The path exceeds maximum length</summary>
        PathTooLong = -12,
        /// <summary>The name exceeds maximum length</summary>
        NameTooLong = -13,
        /// <summary>The path is not a directory</summary>
        NotDirectory = -14,
        /// <summary>The path is a directory</summary>
        IsDirectory = -15,
        /// <summary>The directory is not empty</summary>
        DirectoryNotEmpty = -16,
        /// <summary>End of file or stream reached</summary>
        AtEnd = -17,
        /// <summary>No space left on device</summary>
        NoSpace = -18,
        /// <summary>Resource is busy</summary>
        Busy = -19,
        /// <summary>Input/output error occurred</summary>
        IoError = -20,
        /// <summary>Operation was interrupted</summary>
        Interrupt = -21,
        /// <summary>Resource is unavailable</summary>
        Unavailable = -22,
        /// <summary>Resource is already in use</summary>
        AlreadyInUse = -23,
        /// <summary>Bad address</summary>
        BadAddress = -24,
        /// <summary>Seek operation failed</summary>
        BadSeek = -25,
        /// <summary>Broken pipe</summary>
        BadPipe = -26,
        /// <summary>Resource deadlock would occur</summary>
        Deadlock = -27,
        /// <summary>Too many links</summary>
        TooManyLinks = -28,
        /// <summary>Feature not implemented</summary>
        MA_NOT_IMPLEMENTED = -29,
        /// <summary>No message of desired type</summary>
        NoMessage = -30,
        /// <summary>Invalid message</summary>
        BadMessage = -31,
        /// <summary>No data available</summary>
        NoDataAvailable = -32,
        /// <summary>Invalid data</summary>
        InvalidData = -33,
        /// <summary>Operation timed out</summary>
        Timeout = -34,
        /// <summary>Network unavailable</summary>
        NoNetwork = -35,
        /// <summary>Not unique</summary>
        NotUnique = -36,
        /// <summary>Not a socket</summary>
        NotSocket = -37,
        /// <summary>No address</summary>
        NoAddress = -38,
        /// <summary>Protocol error</summary>
        BadProtocol = -39,
        /// <summary>Protocol unavailable</summary>
        ProtocolUnavailable = -40,
        /// <summary>Protocol not supported</summary>
        ProtocolNotSupported = -41,
        /// <summary>Protocol family not supported</summary>
        ProtocolFamilyNotSupported = -42,
        /// <summary>Address family not supported</summary>
        AddressFamilyNotSupported = -43,
        /// <summary>Socket type not supported</summary>
        SocketNotSupported = -44,
        /// <summary>Connection reset</summary>
        ConnectionReset = -45,
        /// <summary>Already connected</summary>
        AlreadyConnected = -46,
        /// <summary>Not connected</summary>
        NotConnected = -47,
        /// <summary>Connection refused</summary>
        ConnectionRefused = -48,
        /// <summary>No host</summary>
        NoHost = -49,
        /// <summary>Operation in progress</summary>
        InProgress = -50,
        /// <summary>Operation cancelled</summary>
        Cancelled = -51,
        /// <summary>Memory already mapped</summary>
        MemoryAlreadyMapped = -52,

        /* Backend-specific errors */
        /// <summary>Format not supported</summary>
        FormatNotSupported = -100,
        /// <summary>Device type not supported</summary>
        DeviceTypeNotSupported = -101,
        /// <summary>Share mode not supported</summary>
        ShareModeNotSupported = -102,
        /// <summary>No backend available</summary>
        NoBackend = -103,
        /// <summary>No device available</summary>
        NoDevice = -104,
        /// <summary>API not found</summary>
        ApiNotFound = -105,
        /// <summary>Invalid device configuration</summary>
        InvalidDeviceConfig = -106,
        /// <summary>Loop error</summary>
        Loop = -107,

        /* Data encoding related errors */
        /// <summary>CRC mismatch</summary>
        CrcMismatch = -200,

        /* The worst error code a client should ever get */
        /// <summary>Unapplicable error</summary>
        Unapplicable = -300
    }

    /// <summary>
    /// Miniaudio device types - aligned with DeviceType values
    /// </summary>
    public enum MaDeviceType
    {
        /// <summary>Playback device</summary>
        Playback = 1,
        /// <summary>Capture device</summary>
        Capture = 2,
        /// <summary>Duplex device (playback and capture)</summary>
        Duplex = Playback | Capture,
        /// <summary>Loopback device</summary>
        Loopback = 4
    }

    /// <summary>
    /// Audio formats - aligned with SampleFormat values
    /// </summary>
    public enum MaFormat
    {
        /// <summary>Unknown format</summary>
        Unknown = 0,
        /// <summary>8-bit unsigned</summary>
        U8 = 1,
        /// <summary>16-bit signed</summary>
        S16 = 2,
        /// <summary>24-bit signed</summary>
        S24 = 3,
        /// <summary>32-bit signed</summary>
        S32 = 4,
        /// <summary>32-bit floating point</summary>
        F32 = 5,
        /// <summary>Number of format types</summary>
        Count
    }

    /// <summary>
    /// Miniaudio backend types
    /// </summary>
    public enum MaBackend
    {
        /// <summary>Windows Audio Session API</summary>
        Wasapi,
        /// <summary>DirectSound</summary>
        Dsound,
        /// <summary>Windows Multimedia</summary>
        Winmm,
        /// <summary>Apple Core Audio</summary>
        CoreAudio,
        /// <summary>OpenBSD audio</summary>
        Sndio,
        /// <summary>NetBSD, OpenBSD audio</summary>
        Audio4,
        /// <summary>FreeBSD, NetBSD, OpenBSD audio</summary>
        Oss,
        /// <summary>Linux PulseAudio</summary>
        PulseAudio,
        /// <summary>Linux Advanced Linux Sound Architecture</summary>
        Alsa,
        /// <summary>Cross Platform audio</summary>
        Jack,
        /// <summary>Android audio</summary>
        Aaudio,
        /// <summary>Android OpenSL ES</summary>
        OpenSL,
        /// <summary>Web audio</summary>
        WebAudio,
        /// <summary>Custom backend</summary>
        Custom,
        /// <summary>Null/dummy backend</summary>
        Null,
        /// <summary>Total number of backends</summary>
        Count
    }

    /// <summary>
    /// Defines encoding format types in the miniaudio library
    /// </summary>
    public enum MaEncodingFormat
    {
        /// <summary>Unknown encoding format</summary>
        Unknown = 0,
        /// <summary>WAVE format</summary>
        WAV,
        /// <summary>Free Lossless Audio Codec</summary>
        FLAC,
        /// <summary>MPEG-1 Audio Layer III</summary>
        MP3,
        /// <summary>Vorbis audio format</summary>
        VORBIS
    }

    /// <summary>
    /// Miniaudio performance profile
    /// </summary>
    public enum MaPerformanceProfile
    {
        /// <summary>Low latency profile</summary>
        LowLatency = 0,
        /// <summary>Conservative profile</summary>
        Conservative
    }

    /// <summary>
    /// Channel mixing mode
    /// </summary>
    public enum MaChannelMixMode
    {
        /// <summary>Simple averaging based on channel position. When upmixing, the last channel is cloned.</summary>
        Rectangular = 0,
        /// <summary>Simple averaging based on channel position. When upmixing, channels are distributed evenly.</summary>
        Simple,
        /// <summary>Use custom weights specified in ma_channel_converter_config.</summary>
        CustomWeights,
        /// <summary>No conversion takes place. Excess channels are ignored. Missing channels are set to zero.</summary>
        Planar
    }

    /// <summary>
    /// Standard channel positions
    /// </summary>
    public enum MaChannelPosition
    {
        /// <summary>No specific position</summary>
        None = 0,
        /// <summary>Front left speaker</summary>
        FrontLeft,
        /// <summary>Front right speaker</summary>
        FrontRight,
        /// <summary>Front center speaker</summary>
        FrontCenter,
        /// <summary>Low frequency effects channel</summary>
        LFE,
        /// <summary>Back left speaker</summary>
        BackLeft,
        /// <summary>Back right speaker</summary>
        BackRight,
        /// <summary>Front left center speaker</summary>
        FrontLeftCenter,
        /// <summary>Front right center speaker</summary>
        FrontRightCenter,
        /// <summary>Back center speaker</summary>
        BackCenter,
        /// <summary>Side left speaker</summary>
        SideLeft,
        /// <summary>Side right speaker</summary>
        SideRight,
        /// <summary>Top center speaker</summary>
        TopCenter,
        /// <summary>Top front left speaker</summary>
        TopFrontLeft,
        /// <summary>Top front center speaker</summary>
        TopFrontCenter,
        /// <summary>Top front right speaker</summary>
        TopFrontRight,
        /// <summary>Top back left speaker</summary>
        TopBackLeft,
        /// <summary>Top back center speaker</summary>
        TopBackCenter,
        /// <summary>Top back right speaker</summary>
        TopBackRight,
        /// <summary>Auxiliary channel 0</summary>
        Aux0,
        /// <summary>Auxiliary channel 1</summary>
        Aux1,
        /// <summary>Auxiliary channel 2</summary>
        Aux2,
        /// <summary>Auxiliary channel 3</summary>
        Aux3,
        /// <summary>Auxiliary channel 4</summary>
        Aux4,
        /// <summary>Auxiliary channel 5</summary>
        Aux5,
        /// <summary>Auxiliary channel 6</summary>
        Aux6,
        /// <summary>Auxiliary channel 7</summary>
        Aux7,
        /// <summary>Auxiliary channel 8</summary>
        Aux8,
        /// <summary>Auxiliary channel 9</summary>
        Aux9,
        /// <summary>Auxiliary channel 10</summary>
        Aux10,
        /// <summary>Auxiliary channel 11</summary>
        Aux11,
        /// <summary>Auxiliary channel 12</summary>
        Aux12,
        /// <summary>Auxiliary channel 13</summary>
        Aux13,
        /// <summary>Auxiliary channel 14</summary>
        Aux14,
        /// <summary>Auxiliary channel 15</summary>
        Aux15,
        /// <summary>Auxiliary channel 16</summary>
        Aux16,
        /// <summary>Auxiliary channel 17</summary>
        Aux17,
        /// <summary>Auxiliary channel 18</summary>
        Aux18,
        /// <summary>Auxiliary channel 19</summary>
        Aux19,
        /// <summary>Auxiliary channel 20</summary>
        Aux20,
        /// <summary>Auxiliary channel 21</summary>
        Aux21,
        /// <summary>Auxiliary channel 22</summary>
        Aux22,
        /// <summary>Auxiliary channel 23</summary>
        Aux23,
        /// <summary>Auxiliary channel 24</summary>
        Aux24,
        /// <summary>Auxiliary channel 25</summary>
        Aux25,
        /// <summary>Auxiliary channel 26</summary>
        Aux26,
        /// <summary>Auxiliary channel 27</summary>
        Aux27,
        /// <summary>Auxiliary channel 28</summary>
        Aux28,
        /// <summary>Auxiliary channel 29</summary>
        Aux29,
        /// <summary>Auxiliary channel 30</summary>
        Aux30,
        /// <summary>Auxiliary channel 31</summary>
        Aux31
    }

    /// <summary>
    /// Standard channel mappings
    /// </summary>
    [Flags]
    public enum MaStandardChannelMap
    {
        /// <summary>Default channel mapping</summary>
        Default = 0,
        /// <summary>Microsoft channel mapping</summary>
        Microsoft,
        /// <summary>ALSA channel mapping</summary>
        Alsa,
        /// <summary>Based off AIFF channel mapping</summary>
        Rfc3551,
        /// <summary>FLAC channel mapping</summary>
        Flac,
        /// <summary>Vorbis channel mapping</summary>
        Vorbis,
        /// <summary>SoundIO channel mapping</summary>
        SoundIo
    }

    /// <summary>
    /// Resampler algorithm types
    /// </summary>
    public enum MaResampleAlgorithm
    {
        /// <summary>Linear interpolation. Fast but lower quality.</summary>
        Linear = 0,
        /// <summary>Sinc interpolation. Slower but better quality.</summary>
        Sinc,
        /// <summary>Custom/extensible algorithm.</summary>
        Custom
    }

    /// <summary>
    /// Sinc resampler window function types
    /// </summary>
    public enum MaSincResamplerWindowFunction
    {
        /// <summary>Rectangular window (no windowing).</summary>
        Rectangular = 0,
        /// <summary>Hann window.</summary>
        Hann,
        /// <summary>Hamming window.</summary>
        Hamming,
        /// <summary>Blackman window.</summary>
        Blackman,
        /// <summary>Blackman-Harris window.</summary>
        BlackmanHarris,
        /// <summary>Blackman-Nuttall window.</summary>
        BlackmanNuttall
    }

    /// <summary>
    /// Miniaudio seek origin
    /// </summary>
    public enum SeekPoint
    {
        /// <summary>Seek from the beginning of the file.</summary>
        FromStart,
        /// <summary>Seek from the current position.</summary>
        FromCurrent
    }

    /// <summary>
    /// 8-bit boolean type for miniaudio
    /// </summary>
    public enum Mabool8 : byte
    {
        /// <summary>False value</summary>
        False = 0,
        /// <summary>True value</summary>
        True = 1
    }

    /// <summary>
    /// 32-bit boolean type for miniaudio
    /// </summary>
    public enum Mabool32 : int
    {
        /// <summary>False value</summary>
        False = 0,
        /// <summary>True value</summary>
        True = 1
    }

    /// <summary>
    /// Miniaudio share mode
    /// </summary>
    public enum MaShareMode
    {
        /// <summary>Shared mode</summary>
        Shared,
        /// <summary>Exclusive mode</summary>
        Exclusive
    }

    /// <summary>
    /// WASAPI usage type
    /// </summary>
    public enum MaWasapiUsage
    {
        /// <summary>Default usage</summary>
        Default,
        /// <summary>Games usage</summary>
        Games,
        /// <summary>Raw usage</summary>
        Raw
    }

    /// <summary>
    /// OpenSL stream type
    /// </summary>
    public enum MaOpenSLStreamType
    {
        /// <summary>Default stream</summary>
        Default,
        /// <summary>Voice stream</summary>
        Voice,
        /// <summary>System stream</summary>
        System,
        /// <summary>Ringtone stream</summary>
        RingTone,
        /// <summary>Media stream</summary>
        Media,
        /// <summary>Alarm stream</summary>
        Alarm,
        /// <summary>Notification stream</summary>
        Notification
    }

    /// <summary>
    /// OpenSL recording preset
    /// </summary>
    public enum MaOpenSLRecordingPreset
    {
        /// <summary>Default preset</summary>
        Default,
        /// <summary>Voice preset</summary>
        Voice,
        /// <summary>Communication preset</summary>
        Communication,
        /// <summary>Unprocessed preset</summary>
        Unprocessed
    }

    /// <summary>
    /// AAudio usage type
    /// </summary>
    public enum MaAAudioUsage
    {
        /// <summary>Default usage</summary>
        Default,
        /// <summary>Media usage</summary>
        Media,
        /// <summary>Voice communication usage</summary>
        VoiceCommunication,
        /// <summary>Voice communication signalling usage</summary>
        VoiceCommunicationSignalling,
        /// <summary>Alarm usage</summary>
        Alarm,
        /// <summary>Notification usage</summary>
        Notification,
        /// <summary>Notification ringtone usage</summary>
        NotificationRingtone,
        /// <summary>Notification event usage</summary>
        NotificationEvent,
        /// <summary>Assistance accessibility usage</summary>
        AssistanceAccessibility,
        /// <summary>Assistance navigation guidance usage</summary>
        AssistanceNavigationGuidance,
        /// <summary>Assistance sonification usage</summary>
        AssistanceSonification,
        /// <summary>Game usage</summary>
        Game
    }

    /// <summary>
    /// AAudio content type
    /// </summary>
    public enum MaAAudioContentType
    {
        /// <summary>Default content type</summary>
        Default,
        /// <summary>Speech content type</summary>
        Speech,
        /// <summary>Music content type</summary>
        Music,
        /// <summary>Movie content type</summary>
        Movie,
        /// <summary>Sonification content type</summary>
        Sonification
    }

    /// <summary>
    /// AAudio input preset
    /// </summary>
    public enum MaAAudioInputPreset
    {
        /// <summary>Default input preset</summary>
        Default,
        /// <summary>Voice communication input preset</summary>
        VoiceCommunication,
        /// <summary>Unprocessed input preset</summary>
        Unprocessed
    }

    /// <summary>
    /// AAudio allowed capture policy
    /// </summary>
    public enum MaAAudioAllowedCapturePolicy
    {
        /// <summary>Default capture policy</summary>
        Default,
        /// <summary>Allow all captures</summary>
        All,
        /// <summary>Exclude record</summary>
        ExcludeRecord,
        /// <summary>Exclude all captures</summary>
        ExcludeAll
    }

    /// <summary>
    /// Decoder Dither Mode
    /// </summary>
    public enum MaDitherMode
    {
        /// <summary>No dithering</summary>
        None = 0,
        /// <summary>Rectangle dithering</summary>
        Rectangle,
        /// <summary>Triangle dithering</summary>
        Triangle
    }
}
