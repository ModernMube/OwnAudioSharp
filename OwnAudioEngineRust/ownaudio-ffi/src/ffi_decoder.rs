//! FFI exports for the streaming audio file decoder.
//!
//! A decoder handle wraps a background prefetch thread that decodes a file
//! incrementally into a lock-free ring buffer.  `read` is real-time safe; all
//! other calls may be invoked from any (non-audio) thread.

use std::ffi::CStr;
use std::os::raw::c_char;

use ownaudio_core::open_streaming;

use crate::error_code::{set_last_error, OwnAudioErrorCode};
use crate::handles::{decoder_from_ptr, DecoderWrapper, OwnAudioDecoderHandle};

/// Default prefetch buffer duration in seconds when the caller passes `0`.
const DEFAULT_PREFETCH_SECONDS: f32 = 2.0;

/// Sample rate assumed for prefetch sizing when the caller keeps the source rate.
const DEFAULT_SIZING_RATE: f32 = 44_100.0;

/// C-compatible mirror of [`ownaudio_core::AudioStreamInfo`], returned by value.
///
/// Layout matches the core struct exactly (both are `#[repr(C)]`).
#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct OwnAudioStreamInfo {
    /// Number of interleaved channels in the decoded output.
    pub channels: u32,
    /// Output sample rate in Hz.
    pub sample_rate: u32,
    /// Total duration in milliseconds; `u64::MAX` if unknown.
    pub duration_ms: u64,
    /// Source bit depth, or `0` for float/compressed formats.
    pub bit_depth: u32,
}

impl From<ownaudio_core::AudioStreamInfo> for OwnAudioStreamInfo {
    fn from(i: ownaudio_core::AudioStreamInfo) -> Self {
        Self {
            channels: i.channels,
            sample_rate: i.sample_rate,
            duration_ms: i.duration_ms,
            bit_depth: i.bit_depth,
        }
    }
}

/// Opens an audio file for streaming decoding and writes the handle to
/// `*out_decoder`.
///
/// - `path` — null-terminated UTF-8 file path.
/// - `target_sample_rate` — desired output sample rate in Hz; `0` keeps source.
/// - `target_channels` — desired output channel count; `0` keeps source.
/// - `prefetch_seconds` — prefetch buffer length in seconds; `<= 0` uses 2.0.
/// - `out_decoder` — receives the new decoder handle on success.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.  The handle must be
/// released with `ownaudio_v1_decoder_destroy`.
#[no_mangle]
pub extern "C" fn ownaudio_v1_decoder_open(
    path: *const c_char,
    target_sample_rate: u32,
    target_channels: u32,
    prefetch_seconds: f32,
    out_decoder: *mut *mut OwnAudioDecoderHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if path.is_null() || out_decoder.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let path_str = match unsafe { CStr::from_ptr(path) }.to_str() {
            Ok(s) => s,
            Err(_) => {
                set_last_error("decoder path is not valid UTF-8");
                return OwnAudioErrorCode::DecoderOpenFailed as i32;
            }
        };

        let secs = if prefetch_seconds <= 0.0 {
            DEFAULT_PREFETCH_SECONDS
        } else {
            prefetch_seconds
        };
        let sizing_rate = if target_sample_rate == 0 {
            DEFAULT_SIZING_RATE
        } else {
            target_sample_rate as f32
        };
        let prefetch_frames = (sizing_rate * secs) as usize;

        match open_streaming(path_str, target_sample_rate, target_channels, prefetch_frames) {
            Ok(track) => {
                let boxed = Box::new(DecoderWrapper { inner: track });
                unsafe {
                    *out_decoder = Box::into_raw(boxed) as *mut OwnAudioDecoderHandle;
                }
                OwnAudioErrorCode::Success as i32
            }
            Err(e) => {
                set_last_error(e.to_string());
                OwnAudioErrorCode::from(e) as i32
            }
        }
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Reads up to `buffer_count` decoded interleaved `f32` samples into `buffer`
/// and writes the number actually produced to `*out_samples_written`.
///
/// Real-time safe: never blocks or allocates.  A value smaller than
/// `buffer_count` means EOF or a transient prefetch underrun.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_decoder_read(
    handle: *mut OwnAudioDecoderHandle,
    buffer: *mut f32,
    buffer_count: usize,
    out_samples_written: *mut usize,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if buffer.is_null() || out_samples_written.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let wrapper = match unsafe { decoder_from_ptr(handle) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        let written = if buffer_count == 0 {
            0
        } else {
            let dst = unsafe { std::slice::from_raw_parts_mut(buffer, buffer_count) };
            wrapper.inner.read(dst)
        };

        unsafe {
            *out_samples_written = written;
        }
        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Requests a non-blocking seek to `frame_position` (output sample frames).
///
/// The prefetch thread performs the seek asynchronously; subsequent reads may
/// briefly return pre-seek samples already buffered in the ring.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_decoder_seek(
    handle: *mut OwnAudioDecoderHandle,
    frame_position: u64,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { decoder_from_ptr(handle) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };
        wrapper.inner.seek(frame_position);
        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Writes the decoded output stream metadata to `*out_info`.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_decoder_get_stream_info(
    handle: *mut OwnAudioDecoderHandle,
    out_info: *mut OwnAudioStreamInfo,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_info.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let wrapper = match unsafe { decoder_from_ptr(handle) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        unsafe {
            *out_info = OwnAudioStreamInfo::from(wrapper.inner.stream_info());
        }
        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Writes `true` to `*out_is_eof` when the file has been fully decoded and the
/// prefetch buffer is drained.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_decoder_is_eof(
    handle: *mut OwnAudioDecoderHandle,
    out_is_eof: *mut bool,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_is_eof.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let wrapper = match unsafe { decoder_from_ptr(handle) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        unsafe {
            *out_is_eof = wrapper.inner.is_eof();
        }
        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Destroys a decoder handle, stopping and joining the prefetch thread.
///
/// Passing `null` is safe and has no effect.
#[no_mangle]
pub extern "C" fn ownaudio_v1_decoder_destroy(handle: *mut OwnAudioDecoderHandle) {
    if handle.is_null() {
        return;
    }
    unsafe {
        drop(Box::from_raw(handle as *mut DecoderWrapper));
    }
}
