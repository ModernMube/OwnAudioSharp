//! Numeric error codes for the SoundTouch pipeline.
//!
//! The C# implementation signals invalid arguments and pipeline misuse with
//! exceptions.  On the real-time audio thread Rust must never panic, so the
//! port replaces every exceptional path with an explicit [`ErrorCode`] that can
//! be forwarded across the FFI boundary as a plain `i32`.

/// Result of a fallible SoundTouch operation.
///
/// `#[repr(i32)]` keeps the discriminants stable for the future FFI layer, so a
/// returned code maps directly onto the managed `OwnAudioErrorCode` model.
#[repr(i32)]
#[derive(Debug, Copy, Clone, PartialEq, Eq)]
pub enum ErrorCode {
    /// Operation completed successfully.
    Ok = 0,
    /// A fixed-capacity buffer would have overflowed.
    BufferOverflow = 1,
    /// Tempo factor outside the supported range.
    InvalidTempo = 2,
    /// Pitch factor outside the supported range.
    InvalidPitch = 3,
    /// Rate factor outside the supported range.
    InvalidRate = 4,
    /// Channel count is zero or above [`crate::MAX_CHANNELS`].
    InvalidChannelCount = 5,
    /// A required parameter (e.g. sample rate) was not set before use.
    NotInitialized = 6,
    /// A null pointer was supplied across the FFI boundary.
    NullPointer = 7,
}

impl ErrorCode {
    /// Returns `true` when the code represents success.
    #[inline]
    pub fn is_ok(self) -> bool {
        matches!(self, ErrorCode::Ok)
    }
}

/// Convenience result type used throughout the crate.
pub type StResult<T> = Result<T, ErrorCode>;
