namespace Ownaudio.Safe;

/// <summary>
/// Error codes returned by the native Rust audio engine, surfaced as a public managed enum.
/// Values mirror <c>OwnAudioErrorCode</c> in <c>ownaudio_ffi.h</c> — changes are a breaking
/// FFI ABI change and require a major version bump.
/// </summary>
public enum AudioEngineErrorCode
{
    /// <summary>Operation succeeded.</summary>
    Success = 0,

    /// <summary>No audio device matched the requested criteria.</summary>
    DeviceNotFound = 1,

    /// <summary>The OS audio subsystem failed to enumerate devices.</summary>
    DeviceEnumerationFailed = 2,

    /// <summary>The requested stream parameters are not supported by the device.</summary>
    UnsupportedConfig = 3,

    /// <summary>The audio stream could not be built.</summary>
    StreamBuildFailed = 4,

    /// <summary>Starting or stopping the stream failed.</summary>
    StreamControlFailed = 5,

    /// <summary>A required pointer argument was null.</summary>
    NullPointer = 6,

    /// <summary>The supplied handle does not point to a valid object.</summary>
    InvalidHandle = 7,

    /// <summary>A panic was caught inside the FFI boundary.</summary>
    InternalPanic = 8,

    /// <summary>An internal error not covered by the above codes.</summary>
    InternalError = 9,

    /// <summary>
    /// The requested host API (e.g. ASIO) is not compiled into this binary,
    /// or no compatible driver is installed on this machine.
    /// </summary>
    HostApiNotAvailable = 10,

    /// <summary>
    /// The ASIO host API is compiled in but no ASIO driver is installed on this machine.
    /// </summary>
    AsioDriverNotFound = 11,
}
