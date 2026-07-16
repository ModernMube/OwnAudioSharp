namespace Ownaudio.Safe;

/// <summary>
/// Sample data format for audio streams.
/// Values mirror <c>OwnAudioSampleFormat</c> in <c>ownaudio_ffi.h</c>;
/// changing them is a breaking FFI ABI change requiring a major version bump.
/// </summary>
public enum SampleFormat
{
    /// <summary>32-bit IEEE 754 float — recommended for all DSP work.</summary>
    F32 = 0,

    /// <summary>Signed 16-bit integer.</summary>
    I16 = 1,

    /// <summary>Unsigned 16-bit integer.</summary>
    U16 = 2,

    /// <summary>Signed 32-bit integer — the native wire format of many ASIO drivers.</summary>
    I32 = 3,
}
