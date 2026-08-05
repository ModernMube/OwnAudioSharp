//! Blittable structs that cross the FFI boundary by value.

/// C-compatible transcribed note.
///
/// Every field is four bytes wide on purpose — the struct then has no padding on any target we
/// build for, and the managed mirror can be a plain sequential struct. `is_drum` is an `i32`
/// rather than a bool for the same reason.
#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct NativeMt3Note {
    /// Onset in seconds.
    pub start_time: f32,
    /// Offset in seconds.
    pub end_time: f32,
    /// MIDI pitch.
    pub pitch: i32,
    /// MIDI velocity, 1..=127.
    pub velocity: i32,
    /// MIDI program the note was played on; zero for drums.
    pub program: i32,
    /// Non-zero when this is a percussion hit.
    pub is_drum: i32,
}

/// Options passed to `ownaudio_mt3_v1_create`.
#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct NativeMt3Options {
    /// ONNX Runtime intra-op threads; zero lets the runtime choose.
    pub threads: i32,
    /// Non-zero to drop percussion before the notes come back.
    pub skip_drums: i32,
}
