namespace Ownaudio.Native.RustAudio.Structs;

/// <summary>
/// OwnAudioStreamErrorKind, as reported by ownaudio_v1_output_stream_get_error_state
/// and its input twin. Values are FFI contract — keep them in sync with the Rust
/// StreamErrorKind discriminants, u32 wide.
/// </summary>
internal enum NativeStreamErrorKind : uint
{
    /// <summary>Nothing went wrong so far.</summary>
    None = 0,

    /// <summary>
    /// Device is gone — unplugged, disabled, or lost on sleep/wake or a mixer
    /// rate change. Stream already stopped, needs a reopen.
    /// </summary>
    DeviceNotAvailable = 1,

    /// <summary>Backend-specific trouble, not a plain device removal.</summary>
    BackendSpecific = 2,
}
