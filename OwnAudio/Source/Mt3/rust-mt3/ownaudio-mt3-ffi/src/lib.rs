//! # ownaudio-mt3-ffi
//!
//! C ABI FFI layer for the OwnAudioSharp MT3 transcription core.
//!
//! This ships as its own native library rather than riding along in `ownaudio_ffi`, because ONNX
//! Runtime is a heavy dependency and chord detection is desktop-only — there is no reason for an
//! iOS or Android build of the audio engine to carry it.
//!
//! ## Design rules
//! - Every export returns an `i32` [`error_code::Mt3ErrorCode`]; zero is success.
//! - Handles are opaque; the C# side only holds `SafeHandle` pointers.
//! - Every export wraps its body in `catch_unwind` so a panic never crosses the boundary.
//! - Export names carry the `ownaudio_mt3_v1_` prefix marking the ABI version.

#![allow(clippy::not_unsafe_ptr_arg_deref)]

pub mod error_code;
pub mod ffi_mt3;
pub mod handles;
pub mod types;

pub use error_code::Mt3ErrorCode;
pub use handles::Mt3TranscriberHandle;
pub use types::{NativeMt3Note, NativeMt3Options};
