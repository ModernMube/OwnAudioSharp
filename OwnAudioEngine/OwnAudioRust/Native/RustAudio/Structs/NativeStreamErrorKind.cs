namespace Ownaudio.Native.RustAudio.Structs;

/// <summary>
/// Mirrors the <c>OwnAudioStreamErrorKind</c> enum published by
/// <c>ownaudio_v1_output_stream_get_error_state</c> /
/// <c>ownaudio_v1_input_stream_get_error_state</c>.
/// </summary>
/// <remarks>
/// The numeric values are part of the FFI contract and must stay in sync with the
/// Rust <c>StreamErrorKind</c> discriminants; the underlying type is
/// <c>uint</c> to match the Rust <c>u32</c> width.
/// </remarks>
internal enum NativeStreamErrorKind : uint
{
    /// <summary>No error has been observed on the stream.</summary>
    None = 0,

    /// <summary>
    /// The audio device is no longer available (unplugged, disabled, or lost on
    /// sleep/wake or a mixer sample-rate change). The stream has stopped and must
    /// be reopened.
    /// </summary>
    DeviceNotAvailable = 1,

    /// <summary>
    /// A backend-specific error that is not a plain device removal.
    /// </summary>
    BackendSpecific = 2,
}
