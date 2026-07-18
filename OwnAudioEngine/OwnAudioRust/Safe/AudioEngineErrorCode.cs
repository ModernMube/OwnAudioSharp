namespace Ownaudio.Safe;

/// <summary>
/// Error codes coming back from the rust engine. The numbers mirror OwnAudioErrorCode
/// in ownaudio_ffi.h, touching them is a breaking abi change.
/// </summary>
public enum AudioEngineErrorCode
{
    /// All good.
    Success = 0,

    /// Nothing matched the requested device.
    DeviceNotFound = 1,

    /// The os side failed to list the devices.
    DeviceEnumerationFailed = 2,

    /// The device does not support these stream params.
    UnsupportedConfig = 3,

    /// Could not build the stream.
    StreamBuildFailed = 4,

    /// Start or stop failed on an existing stream.
    StreamControlFailed = 5,

    /// A pointer argument was null.
    NullPointer = 6,

    /// The handle does not point to a live object.
    InvalidHandle = 7,

    /// A panic got caught at the ffi boundary.
    InternalPanic = 8,

    /// Anything else that went wrong inside.
    InternalError = 9,

    /// <summary>
    /// The host api (asio for example) is not compiled into this binary,
    /// or there is no compatible driver on the machine.
    /// </summary>
    HostApiNotAvailable = 10,

    /// Asio is compiled in but no driver is installed here.
    AsioDriverNotFound = 11,

    /// <summary>
    /// The native binary reports a different abi version than the managed side
    /// was built against. Reinstall the nuget package.
    /// </summary>
    AbiVersionMismatch = 12,

    /// Could not open the file or probe its format.
    DecoderOpenFailed = 13,

    /// No backend handles this container or codec.
    DecoderUnsupportedFormat = 14,

    /// Decoding blew up mid stream.
    DecoderReadFailed = 15,

    /// Seek failed inside the stream.
    DecoderSeekFailed = 16,
}
