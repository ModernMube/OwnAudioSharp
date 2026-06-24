//! # ownaudio-midi-ffi
//!
//! C ABI FFI layer for the OwnAudioSharp MIDI core.
//!
//! This crate wraps [`ownaudio_midi_core`] with a stable `extern "C"` surface
//! that the managed `OwnAudio.Midi` package calls through `LibraryImport`. It
//! compiles to a `cdylib` (`.dll`/`.so`/`.dylib`) and a `staticlib`.
//!
//! ## Design rules
//! - Every export returns an `i32` [`error_code::MidiErrorCode`]; zero is success.
//! - All handles are opaque; the C# side only holds `SafeHandle` pointers.
//! - Every export wraps its body in `std::panic::catch_unwind` so a Rust panic
//!   never unwinds across the FFI boundary.
//! - Every export name carries the `ownaudio_midi_v1_` prefix marking the ABI
//!   version; a future breaking change would move to `v2`.

// Every `extern "C"` export receives raw pointers from managed code and
// dereferences them after null/validity checks. Marking each one `unsafe` would
// break the C ABI contract the C# `LibraryImport` layer relies on, so the
// raw-pointer-deref lint is intentionally allowed crate-wide at the boundary.
#![allow(clippy::not_unsafe_ptr_arg_deref)]

pub mod error_code;
pub mod ffi_clock;
pub mod ffi_file;
pub mod ffi_port;
pub mod handles;
pub mod types;

// Re-export the types cbindgen needs to find at the crate root.
pub use error_code::MidiErrorCode;
pub use handles::{
    MidiClockHandle, MidiFileHandle, MidiInputPortHandle, MidiOutputPortHandle, MidiWriterHandle,
};
pub use types::{NativeMidiEvent, NativeMidiMessage};
