use ownaudio_core::{AudioEngine, InputStream, OutputStream};

/// Opaque handle to an [`AudioEngine`] instance.
///
/// The C# side holds this as `IntPtr`.  Create with
/// `ownaudio_v1_engine_create`; release with `ownaudio_v1_engine_destroy`.
#[repr(C)]
pub struct OwnAudioEngineHandle {
    _private: [u8; 0],
}

/// Opaque handle to an output audio stream.
///
/// Create with `ownaudio_v1_open_output_stream`; release with
/// `ownaudio_v1_output_stream_destroy`.
#[repr(C)]
pub struct OwnAudioOutputStreamHandle {
    _private: [u8; 0],
}

/// Opaque handle to an input audio stream.
///
/// Create with `ownaudio_v1_open_input_stream`; release with
/// `ownaudio_v1_input_stream_destroy`.
#[repr(C)]
pub struct OwnAudioInputStreamHandle {
    _private: [u8; 0],
}

// ---------------------------------------------------------------------------
// Internal wrapper types — never exposed across the FFI boundary
// ---------------------------------------------------------------------------

pub(crate) struct EngineWrapper {
    pub inner: AudioEngine,
}

pub(crate) struct OutputStreamWrapper {
    pub inner: OutputStream,
}

pub(crate) struct InputStreamWrapper {
    pub inner: InputStream,
}

// SAFETY: cpal::Stream is not Send on all platforms (e.g. macOS AudioQueue).
// The FFI contract places thread-safety responsibility on the caller: handle
// pointers must not be used concurrently from multiple threads without
// external synchronization.
unsafe impl Send for OutputStreamWrapper {}
unsafe impl Sync for OutputStreamWrapper {}
unsafe impl Send for InputStreamWrapper {}
unsafe impl Sync for InputStreamWrapper {}

// ---------------------------------------------------------------------------
// Helper: safely dereference an opaque handle pointer
// ---------------------------------------------------------------------------

/// Casts a raw `*mut OwnAudioEngineHandle` back to `&mut EngineWrapper`.
///
/// Returns `None` if the pointer is null.
///
/// # Safety
/// The caller must guarantee that `ptr` was obtained from
/// `ownaudio_v1_engine_create` and has not been destroyed yet.
pub(crate) unsafe fn engine_from_ptr<'a>(
    ptr: *mut OwnAudioEngineHandle,
) -> Option<&'a mut EngineWrapper> {
    if ptr.is_null() {
        None
    } else {
        Some(&mut *(ptr as *mut EngineWrapper))
    }
}

/// Casts a raw `*mut OwnAudioOutputStreamHandle` back to `&mut OutputStreamWrapper`.
pub(crate) unsafe fn output_stream_from_ptr<'a>(
    ptr: *mut OwnAudioOutputStreamHandle,
) -> Option<&'a mut OutputStreamWrapper> {
    if ptr.is_null() {
        None
    } else {
        Some(&mut *(ptr as *mut OutputStreamWrapper))
    }
}

/// Casts a raw `*mut OwnAudioInputStreamHandle` back to `&mut InputStreamWrapper`.
pub(crate) unsafe fn input_stream_from_ptr<'a>(
    ptr: *mut OwnAudioInputStreamHandle,
) -> Option<&'a mut InputStreamWrapper> {
    if ptr.is_null() {
        None
    } else {
        Some(&mut *(ptr as *mut InputStreamWrapper))
    }
}
