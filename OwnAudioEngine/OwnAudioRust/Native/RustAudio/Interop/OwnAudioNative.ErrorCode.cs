namespace Ownaudio.Native.RustAudio.Interop;

/// <summary>
/// OwnAudioErrorCode from ownaudio_ffi.h. cbindgen generates it, so the values have to match exactly.
/// Turning these into exceptions is the safe wrapper's job, not ours.
/// </summary>
internal enum NativeErrorCode : int
{
    /// <summary>All good.</summary>
    Success = 0,

    /// <summary>Nothing matched what we asked for.</summary>
    DeviceNotFound = 1,

    /// <summary>The OS audio subsystem couldn't enumerate at all.</summary>
    DeviceEnumerationFailed = 2,

    /// <summary>Device doesn't do these stream params.</summary>
    UnsupportedConfig = 3,

    /// <summary>Stream build failed.</summary>
    StreamBuildFailed = 4,

    /// <summary>Start/stop on the stream failed.</summary>
    StreamControlFailed = 5,

    /// <summary>Some pointer arg was null.</summary>
    NullPointer = 6,

    /// <summary>Handle doesn't point to anything valid.</summary>
    InvalidHandle = 7,

    /// <summary>A rust panic got caught at the FFI boundary.</summary>
    InternalPanic = 8,

    /// <summary>Anything else that went wrong inside.</summary>
    InternalError = 9,

    /// <summary>
    /// Host api (ASIO and friends) isn't compiled into this binary, or there's no driver for it here.
    /// </summary>
    HostApiNotAvailable = 10,

    /// <summary>ASIO is compiled in but no driver is installed on the machine.</summary>
    AsioDriverNotFound = 11,

    /// <summary>Couldn't open the file or probe its format.</summary>
    DecoderOpenFailed = 12,

    /// <summary>No backend handles this container/codec.</summary>
    DecoderUnsupportedFormat = 13,

    /// <summary>Decoding blew up mid stream.</summary>
    DecoderReadFailed = 14,

    /// <summary>Seek inside the stream failed.</summary>
    DecoderSeekFailed = 15,
}
