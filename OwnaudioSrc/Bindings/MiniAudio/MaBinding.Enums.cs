using System;

namespace Ownaudio.Bindings.Miniaudio;

internal static partial class MaBinding
{
    /// <summary>
    /// Miniaudio result codes - aligned with SoundFlow.Backends.MiniAudio.Enums.Result values
    /// </summary>
    public enum MaResult
    {
        Success = 0, //Operation completed successfully
        Error = -1, //General error
        InvalidArgs = -2, //Invalid arguments were provided
        InvalidOperation = -3, //The operation was invalid in the current context
        OutOfMemory = -4, //Failed to allocate memory
        OutOfRange = -5, //Value is out of valid range
        AccessDenied = -6, //Access to resource was denied
        DoesNotExist = -7, //The requested resource does not exist
        AlreadyExists = -8, //The resource already exists
        TooManyOpenFiles = -9, //Too many files are open
        InvalidFile = -10, //The file is invalid or corrupted
        TooBig = -11, //The resource is too large
        PathTooLong = -12, //The path exceeds maximum length
        NameTooLong = -13, //The name exceeds maximum length
        NotDirectory = -14, //The path is not a directory
        IsDirectory = -15, //The path is a directory
        DirectoryNotEmpty = -16, //The directory is not empty
        AtEnd = -17, //End of file or stream reached
        NoSpace = -18, //No space left on device
        Busy = -19, //Resource is busy
        IoError = -20, //Input/output error occurred
        Interrupt = -21, //Operation was interrupted
        Unavailable = -22, //Resource is unavailable
        AlreadyInUse = -23, //Resource is already in use
        BadAddress = -24, //Bad address
        BadSeek = -25, //Seek operation failed
        BadPipe = -26, //Broken pipe
        Deadlock = -27, //Resource deadlock would occur
        TooManyLinks = -28, //Too many links
        MA_NOT_IMPLEMENTED = -29, //Feature not implemented
        NoMessage = -30, //No message of desired type
        BadMessage = -31, //Invalid message
        NoDataAvailable = -32, //No data available
        InvalidData = -33, //Invalid data
        Timeout = -34, //Operation timed out
        NoNetwork = -35, //Network unavailable
        NotUnique = -36, //Not unique
        NotSocket = -37, //Not a socket
        NoAddress = -38, //No address
        BadProtocol = -39, //Protocol error
        ProtocolUnavailable = -40, //Protocol unavailable
        ProtocolNotSupported = -41, //Protocol not supported
        ProtocolFamilyNotSupported = -42, //Protocol family not supported
        AddressFamilyNotSupported = -43, //Address family not supported
        SocketNotSupported = -44, //Socket type not supported
        ConnectionReset = -45, //Connection reset
        AlreadyConnected = -46, //Already connected
        NotConnected = -47, //Not connected
        ConnectionRefused = -48, //Connection refused
        NoHost = -49, //No host
        InProgress = -50, //Operation in progress
        Cancelled = -51, //Operation cancelled
        MemoryAlreadyMapped = -52, //Memory already mapped

        /* Backend-specific errors */
        FormatNotSupported = -100, //Format not supported
        DeviceTypeNotSupported = -101, //Device type not supported
        ShareModeNotSupported = -102, //Share mode not supported
        NoBackend = -103, //No backend available
        NoDevice = -104, //No device available
        ApiNotFound = -105, //API not found
        InvalidDeviceConfig = -106, //Invalid device configuration
        Loop = -107, //Loop error

        /* Data encoding related errors */
        CrcMismatch = -200, //CRC mismatch

        /* The worst error code a client should ever get */
        Unapplicable = -300 // <summary>Unapplicable error</summary>
    }

    /// <summary>
    /// Miniaudio device types - aligned with SoundFlow.Enums.DeviceType values
    /// </summary>
    public enum MaDeviceType
    {
        Playback = 1, //Playback device
        Capture = 2, //Capture device
        Duplex = Playback | Capture, //Duplex device (playback and capture)
        Loopback = 4 // <summary>Loopback device</summary>
    }

    /// <summary>
    /// Audio formats - aligned with SoundFlow.Enums.SampleFormat values
    /// </summary>
    public enum MaFormat
    {
        Unknown = 0, //Unknown format
        U8 = 1, //8-bit unsigned
        S16 = 2, //16-bit signed
        S24 = 3, //24-bit signed
        S32 = 4, //32-bit signed
        F32 = 5, //32-bit floating point
        Count // <summary>Number of format types</summary>
    }

    /// <summary>
    /// Miniaudio backend types
    /// </summary>
    public enum MaBackend
    {
        Wasapi, //Windows Audio Session API
        Dsound, //DirectSound
        Winmm, //Windows Multimedia
        CoreAudio, //Apple Core Audio
        Sndio, //OpenBSD audio
        Audio4, //NetBSD, OpenBSD audio
        Oss, //FreeBSD, NetBSD, OpenBSD audio
        PulseAudio, //Linux PulseAudio
        Alsa, //Linux Advanced Linux Sound Architecture
        Jack, //Cross Platform audio
        Aaudio, //Android audio
        OpenSL, //Android OpenSL ES
        WebAudio, //Web audio
        Custom, //Custom backend
        Null, //Null/dummy backend
        Count // <summary>Total number of backends</summary>
    }

    /// <summary>
    /// Defines encoding format types in the miniaudio library
    /// </summary>
    public enum MaEncodingFormat
    {
        Unknown = 0, //Unknown encoding format
        WAV, //WAVE format
        FLAC, //Free Lossless Audio Codec
        MP3, //MPEG-1 Audio Layer III
        VORBIS // <summary>Vorbis audio format</summary>
    }

    /// <summary>
    /// Miniaudio performance profile
    /// </summary>
    public enum MaPerformanceProfile
    {
        LowLatency = 0, //Low latency profile
        Conservative // <summary>Conservative profile</summary>
    }

    /// <summary>
    /// Channel mixing mode
    /// </summary>
    public enum MaChannelMixMode
    {
        Rectangular = 0, //Simple averaging based on channel position. When upmixing, the last channel is cloned.
        Simple, //Simple averaging based on channel position. When upmixing, channels are distributed evenly.
        CustomWeights, //Use custom weights specified in ma_channel_converter_config.
        Planar // <summary>No conversion takes place. Excess channels are ignored. Missing channels are set to zero.</summary>
    }

    /// <summary>
    /// Standard channel positions
    /// </summary>
    public enum MaChannelPosition
    {
        None = 0, //No specific position
        FrontLeft, //Front left speaker
        FrontRight, //Front right speaker
        FrontCenter, //Front center speaker
        LFE, //Low frequency effects channel
        BackLeft, //Back left speaker
        BackRight, //Back right speaker
        FrontLeftCenter, //Front left center speaker
        FrontRightCenter, //Front right center speaker
        BackCenter, //Back center speaker
        SideLeft, //Side left speaker
        SideRight, //Side right speaker
        TopCenter, //Top center speaker
        TopFrontLeft, //Top front left speaker
        TopFrontCenter, //Top front center speaker
        TopFrontRight, //Top front right speaker
        TopBackLeft, //Top back left speaker
        TopBackCenter, //Top back center speaker
        TopBackRight, //Top back right speaker
        Aux0, //Auxiliary channel 0
        Aux1, //Auxiliary channel 1
        Aux2, //Auxiliary channel 2
        Aux3, //Auxiliary channel 3
        Aux4, //Auxiliary channel 4
        Aux5, //Auxiliary channel 5
        Aux6, //Auxiliary channel 6
        Aux7, //Auxiliary channel 7
        Aux8, //Auxiliary channel 8
        Aux9, //Auxiliary channel 9
        Aux10, //Auxiliary channel 10
        Aux11, //Auxiliary channel 11
        Aux12, //Auxiliary channel 12
        Aux13, //Auxiliary channel 13
        Aux14, //Auxiliary channel 14
        Aux15, //Auxiliary channel 15
        Aux16, //Auxiliary channel 16
        Aux17, //Auxiliary channel 17
        Aux18, //Auxiliary channel 18
        Aux19, //Auxiliary channel 19
        Aux20, //Auxiliary channel 20
        Aux21, //Auxiliary channel 21
        Aux22, //Auxiliary channel 22
        Aux23, //Auxiliary channel 23
        Aux24, //Auxiliary channel 24
        Aux25, //Auxiliary channel 25
        Aux26, //Auxiliary channel 26
        Aux27, //Auxiliary channel 27
        Aux28, //Auxiliary channel 28
        Aux29, //Auxiliary channel 29
        Aux30, //Auxiliary channel 30
        Aux31 // <summary>Auxiliary channel 31</summary>
    }

    /// <summary>
    /// Standard channel mappings
    /// </summary>
    [Flags]
    public enum MaStandardChannelMap
    {
        Default = 0, //Default channel mapping
        Microsoft, //Microsoft channel mapping
        Alsa, //ALSA channel mapping
        Rfc3551, //Based off AIFF channel mapping
        Flac, //FLAC channel mapping
        Vorbis, //Vorbis channel mapping
        SoundIo // <summary>SoundIO channel mapping</summary>
    }

    /// <summary>
    /// Resampler algorithm types
    /// </summary>
    public enum MaResampleAlgorithm
    {
        Linear = 0, //Linear interpolation. Fast but lower quality.
        Sinc, //Sinc interpolation. Slower but better quality.
        Custom // <summary>Custom/extensible algorithm.</summary>
    }

    /// <summary>
    /// Sinc resampler window function types
    /// </summary>
    public enum MaSincResamplerWindowFunction
    {
        Rectangular = 0, //Rectangular window (no windowing).
        Hann, //Hann window.
        Hamming, //Hamming window.
        Blackman, //Blackman window.
        BlackmanHarris, //Blackman-Harris window.
        BlackmanNuttall // <summary>Blackman-Nuttall window.</summary>
    }

    /// <summary>
    /// Miniaudio seek origin
    /// </summary>
    public enum SeekPoint
    {
        FromStart, //Seek from the beginning of the file.
        FromCurrent // <summary>Seek from the current position.</summary>
    }

    /// <summary>
    /// 8-bit boolean type for miniaudio
    /// </summary>
    public enum Mabool8 : byte
    {
        False = 0, //False value
        True = 1 // <summary>True value</summary>
    }

    /// <summary>
    /// 32-bit boolean type for miniaudio
    /// </summary>
    public enum Mabool32 : int
    {
        False = 0, //False value
        True = 1 // <summary>True value</summary>
    }

    /// <summary>
    /// Miniaudio share mode
    /// </summary>
    public enum MaShareMode
    {
        Shared, //Shared mode
        Exclusive // <summary>Exclusive mode</summary>
    }

    /// <summary>
    /// WASAPI usage type
    /// </summary>
    public enum MaWasapiUsage
    {
        Default, //Default usage
        Games, //Games usage
        Raw // <summary>Raw usage</summary>
    }

    /// <summary>
    /// OpenSL stream type
    /// </summary>
    public enum MaOpenSLStreamType
    {
        Default, //Default stream
        Voice, //Voice stream
        System, //System stream
        RingTone, //Ringtone stream
        Media, //Media stream
        Alarm, //Alarm stream
        Notification // <summary>Notification stream</summary>
    }

    /// <summary>
    /// OpenSL recording preset
    /// </summary>
    public enum MaOpenSLRecordingPreset
    {
        Default, //Default preset
        Voice, //Voice preset
        Communication, //Communication preset
        Unprocessed // <summary>Unprocessed preset</summary>
    }

    /// <summary>
    /// AAudio usage type
    /// </summary>
    public enum MaAAudioUsage
    {
        Default, //Default usage
        Media, //Media usage
        VoiceCommunication, //Voice communication usage
        VoiceCommunicationSignalling, //Voice communication signalling usage
        Alarm, //Alarm usage
        Notification, //Notification usage
        NotificationRingtone, //Notification ringtone usage
        NotificationEvent, //Notification event usage
        AssistanceAccessibility, //Assistance accessibility usage
        AssistanceNavigationGuidance, //Assistance navigation guidance usage
        AssistanceSonification, //Assistance sonification usage
        Game // <summary>Game usage</summary>
    }

    /// <summary>
    /// AAudio content type
    /// </summary>
    public enum MaAAudioContentType
    {
        Default, //Default content type
        Speech, //Speech content type
        Music, //Music content type
        Movie, //Movie content type
        Sonification // <summary>Sonification content type</summary>
    }

    /// <summary>
    /// AAudio input preset
    /// </summary>
    public enum MaAAudioInputPreset
    {
        Default, //Default input preset
        VoiceCommunication, //Voice communication input preset
        Unprocessed // <summary>Unprocessed input preset</summary>
    }

    /// <summary>
    /// AAudio allowed capture policy
    /// </summary>
    public enum MaAAudioAllowedCapturePolicy
    {
        Default, //Default capture policy
        All, //Allow all captures
        ExcludeRecord, //Exclude record
        ExcludeAll // <summary>Exclude all captures</summary>
    }

    /// <summary>
    /// Decoder Dither Mode
    /// </summary>
    public enum MaDitherMode
{
        None = 0,
        Rectangle,
        Triangle
    }
}
