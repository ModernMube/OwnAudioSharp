//! # ownaudio-ffi
//!
//! C ABI FFI layer for OwnAudioSharp.
//!
//! This crate wraps [`ownaudio_core`] with a stable `extern "C"` surface that
//! the C# layer can call via `LibraryImport` / `DllImport`.  It compiles to
//! both a `cdylib` (`.so`/`.dll`/`.dylib`) and a `staticlib` (`.a`/`.lib`).
//!
//! ## Design rules
//! - Every public `extern "C"` function returns an `i32` error code.
//!   Zero means success; use `ownaudio_v1_last_error_message()` for details.
//! - All handles are opaque; the C# side only ever holds `IntPtr` values.
//! - Every function wraps its body in `std::panic::catch_unwind` so that Rust
//!   panics never unwind across the FFI boundary.
//! - The `v1` prefix on every export name marks the ABI version.  Future
//!   breaking changes will use `v2`, keeping old bindings functional.

pub mod callback;
pub mod error_code;
pub mod ffi_abi;
pub mod ffi_config;
pub mod ffi_decoder;
pub mod ffi_device;
pub mod ffi_effects;
pub mod ffi_file_source;
pub mod ffi_source;
pub mod ffi_stream;
pub mod ffi_track;
pub mod handles;
pub mod host_api;

// Re-export the types that cbindgen needs to find at the crate root.
pub use callback::{OwnAudioInputCallback, OwnAudioOutputCallback};
pub use error_code::OwnAudioErrorCode;
// The VST bridge types cross the FFI boundary, so surface them at the crate
// root alongside the other ABI types (cbindgen and the C# layout mirror both
// resolve them here).
pub use ownaudio_core::effects::{VstAudioBuffer, VstProcessFn};
pub use ffi_abi::ABI_VERSION;
pub use ffi_config::{OwnAudioSampleFormat, OwnAudioStreamConfig};
pub use ffi_decoder::OwnAudioStreamInfo;
pub use ffi_device::OwnAudioDeviceInfo;
pub use handles::{
    OwnAudioDecoderHandle, OwnAudioEffectHandle, OwnAudioEngineHandle, OwnAudioFileSourceHandle,
    OwnAudioInputStreamHandle, OwnAudioMixerHandle, OwnAudioOutputStreamHandle,
    OwnAudioTrackHandle, OwnAudioTrackSourceHandle,
};
pub use host_api::OwnHostApi;
