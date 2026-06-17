use std::cell::RefCell;
use std::ffi::CString;

/// C-compatible error codes returned by every FFI function.
///
/// Zero always means success.  All other values indicate failure; call
/// `ownaudio_v1_last_error_message()` on the same thread for a human-readable
/// description of the most recent error.
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum OwnAudioErrorCode {
    /// Operation succeeded.
    Success = 0,
    /// No audio device matched the requested criteria.
    DeviceNotFound = 1,
    /// The OS audio subsystem failed to enumerate devices.
    DeviceEnumerationFailed = 2,
    /// The requested stream parameters are not supported by the device.
    UnsupportedConfig = 3,
    /// The audio stream could not be built.
    StreamBuildFailed = 4,
    /// Starting or stopping the stream failed.
    StreamControlFailed = 5,
    /// A required pointer argument was null.
    NullPointer = 6,
    /// The supplied handle does not point to a valid object.
    InvalidHandle = 7,
    /// A panic was caught inside the FFI boundary.
    InternalPanic = 8,
    /// An internal error not covered by the above codes.
    InternalError = 9,
}

impl From<ownaudio_core::AudioError> for OwnAudioErrorCode {
    fn from(err: ownaudio_core::AudioError) -> Self {
        match err {
            ownaudio_core::AudioError::DeviceNotFound => Self::DeviceNotFound,
            ownaudio_core::AudioError::DeviceEnumeration(_) => Self::DeviceEnumerationFailed,
            ownaudio_core::AudioError::UnsupportedConfig(_) => Self::UnsupportedConfig,
            ownaudio_core::AudioError::StreamBuild(_) => Self::StreamBuildFailed,
            ownaudio_core::AudioError::StreamControl(_) => Self::StreamControlFailed,
            ownaudio_core::AudioError::RingBufferOverflow { .. } => Self::InternalError,
            ownaudio_core::AudioError::RingBufferUnderrun { .. } => Self::InternalError,
            ownaudio_core::AudioError::ResamplerInit(_) => Self::InternalError,
            ownaudio_core::AudioError::ResamplerProcess(_) => Self::InternalError,
        }
    }
}

// ---------------------------------------------------------------------------
// Thread-local last error message
// ---------------------------------------------------------------------------

thread_local! {
    static LAST_ERROR: RefCell<Option<CString>> = const { RefCell::new(None) };
}

/// Stores a human-readable error message in thread-local storage.
///
/// The stored string is returned by `ownaudio_v1_last_error_message()` on the
/// same thread.  The previous message is discarded.
pub(crate) fn set_last_error(msg: impl Into<String>) {
    LAST_ERROR.with(|cell| {
        *cell.borrow_mut() = CString::new(msg.into()).ok();
    });
}

/// Returns the last error message as a null-terminated UTF-8 string, or null
/// if no error has been recorded on this thread.
///
/// # Lifetime
/// The pointer is valid until the next FFI call on this thread that sets an
/// error, or until the thread exits.  The caller must **not** free it.
#[no_mangle]
pub extern "C" fn ownaudio_v1_last_error_message() -> *const std::os::raw::c_char {
    LAST_ERROR.with(|cell| match &*cell.borrow() {
        Some(s) => s.as_ptr(),
        None => std::ptr::null(),
    })
}
