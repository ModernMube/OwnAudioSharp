//! C ABI error codes returned by every MT3 FFI function, plus the thread-local last-error text.

use std::cell::RefCell;
use std::os::raw::c_char;

use ownaudio_mt3_core::Mt3Error;

/// C-compatible error codes. Zero is success; everything else tells the managed layer which
/// exception to raise.
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Mt3ErrorCode {
    /// Operation succeeded.
    Success = 0,
    /// A model or vocabulary file was not at the given path.
    ModelNotFound = 1,
    /// ONNX Runtime refused to load the graph.
    ModelLoadFailed = 2,
    /// vocab.json is malformed or describes a codec we cannot decode.
    VocabInvalid = 3,
    /// A session ran but returned something unusable.
    InferenceFailed = 4,
    /// Resampling the input to the model's rate failed.
    ResampleFailed = 5,
    /// A required pointer argument was null.
    NullPointer = 6,
    /// The supplied handle does not point to a live transcriber.
    InvalidHandle = 7,
    /// A string argument was not valid UTF-8.
    InvalidUtf8 = 8,
    /// A panic was caught at the FFI boundary.
    InternalPanic = 9,
    /// An I/O error occurred.
    IoError = 10,
}

impl From<&Mt3Error> for Mt3ErrorCode {
    fn from(err: &Mt3Error) -> Self {
        match err {
            Mt3Error::ModelNotFound(_) => Self::ModelNotFound,
            Mt3Error::ModelLoad { .. } => Self::ModelLoadFailed,
            Mt3Error::Vocab(_) => Self::VocabInvalid,
            Mt3Error::Inference(_) => Self::InferenceFailed,
            Mt3Error::Resample(_) => Self::ResampleFailed,
            Mt3Error::Io(_) => Self::IoError,
        }
    }
}

thread_local! {
    static LAST_ERROR: RefCell<String> = const { RefCell::new(String::new()) };
}

/// Records a message the C# side can pull out with `ownaudio_mt3_v1_last_error`.
pub(crate) fn set_last_error(message: String) {
    LAST_ERROR.with(|slot| *slot.borrow_mut() = message);
}

/// Stores the error and returns its code, so call sites stay one-liners.
pub(crate) fn fail(err: Mt3Error) -> i32 {
    let code = Mt3ErrorCode::from(&err);
    set_last_error(err.to_string());
    code as i32
}

/// Turns a caught panic into an error code, mirroring the audio engine's FFI layer.
pub(crate) fn finish_catch_unwind(
    result: std::result::Result<i32, Box<dyn std::any::Any + Send>>,
) -> i32 {
    match result {
        Ok(code) => code,
        Err(payload) => {
            let msg = payload
                .downcast_ref::<&str>()
                .map(|s| (*s).to_owned())
                .or_else(|| payload.downcast_ref::<String>().cloned())
                .unwrap_or_else(|| "non-string panic payload".to_owned());

            set_last_error(format!("panic in native MT3 transcriber: {msg}"));
            Mt3ErrorCode::InternalPanic as i32
        }
    }
}

/// Copies the last error message into `buffer` as NUL-terminated UTF-8.
///
/// Returns the number of bytes that would be needed including the terminator, so a caller that
/// passed too small a buffer can retry. A null buffer only queries that length.
///
/// # Safety
/// `buffer` must either be null or point to at least `capacity` writable bytes.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_mt3_v1_last_error(buffer: *mut c_char, capacity: usize) -> usize {
    LAST_ERROR.with(|slot| {
        let message = slot.borrow();
        let bytes = message.as_bytes();
        let needed = bytes.len() + 1;

        if buffer.is_null() || capacity == 0 {
            return needed;
        }

        let copy = bytes.len().min(capacity - 1);
        std::ptr::copy_nonoverlapping(bytes.as_ptr(), buffer as *mut u8, copy);
        *buffer.add(copy) = 0;

        needed
    })
}
