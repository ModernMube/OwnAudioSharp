use std::ffi::CStr;
use std::os::raw::c_char;

use ownaudio_core::AudioDeviceInfo;

use crate::callback::{make_input_trampoline, make_output_trampoline, OwnAudioInputCallback, OwnAudioOutputCallback};
use crate::error_code::{set_last_error, OwnAudioErrorCode};
use crate::ffi_config::OwnAudioStreamConfig;
use crate::handles::{
    engine_from_ptr, input_stream_from_ptr, output_stream_from_ptr, EngineWrapper,
    InputStreamWrapper, OwnAudioEngineHandle, OwnAudioInputStreamHandle,
    OwnAudioOutputStreamHandle, OutputStreamWrapper,
};

// ---------------------------------------------------------------------------
// Engine lifecycle
// ---------------------------------------------------------------------------

/// Creates a new `AudioEngine` instance and writes its handle to `*out_handle`.
///
/// The handle must be released with `ownaudio_v1_engine_destroy` when no
/// longer needed.  A single engine can be used to open multiple streams.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_engine_create(
    out_handle: *mut *mut OwnAudioEngineHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_handle.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        match ownaudio_core::AudioEngine::new() {
            Ok(engine) => {
                let boxed = Box::new(EngineWrapper { inner: engine });
                unsafe {
                    *out_handle = Box::into_raw(boxed) as *mut OwnAudioEngineHandle;
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

/// Destroys an engine handle created by `ownaudio_v1_engine_create`.
///
/// All streams opened from this engine must be destroyed before calling this
/// function.  Passing `null` is safe and has no effect.
#[no_mangle]
pub extern "C" fn ownaudio_v1_engine_destroy(handle: *mut OwnAudioEngineHandle) {
    if handle.is_null() {
        return;
    }
    // SAFETY: handle was produced by Box::into_raw in engine_create.
    unsafe {
        drop(Box::from_raw(handle as *mut EngineWrapper));
    }
}

// ---------------------------------------------------------------------------
// Output stream
// ---------------------------------------------------------------------------

/// Opens an output stream and writes its handle to `*out_stream`.
///
/// - `engine` — a valid handle returned by `ownaudio_v1_engine_create`.
/// - `device_name` — null-terminated UTF-8 name of the target device, or
///   `null` to use the system default output device.
/// - `config` — pointer to a filled `OwnAudioStreamConfig`; must not be null.
/// - `callback` — function called on the audio thread for every buffer;
///   must not be null.
/// - `user_data` — opaque pointer passed back to `callback`; may be null.
/// - `out_stream` — receives the new stream handle on success.
///
/// The stream starts in the paused state; call
/// `ownaudio_v1_output_stream_play` to begin audio output.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_open_output_stream(
    engine: *mut OwnAudioEngineHandle,
    device_name: *const c_char,
    config: *const OwnAudioStreamConfig,
    callback: OwnAudioOutputCallback,
    user_data: *mut std::os::raw::c_void,
    out_stream: *mut *mut OwnAudioOutputStreamHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if engine.is_null() || config.is_null() || out_stream.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }
        let cb = match callback {
            Some(f) => f,
            None => {
                set_last_error("output callback must not be null");
                return OwnAudioErrorCode::NullPointer as i32;
            }
        };

        let engine_wrapper = match unsafe { engine_from_ptr(engine) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        let c_config = unsafe { *config };
        let core_config: ownaudio_core::StreamConfig = c_config.into();
        let channels = c_config.channels;

        let device_info = parse_device_name(device_name);

        let trampoline = make_output_trampoline(cb, user_data, channels);

        match engine_wrapper
            .inner
            .open_output_stream(device_info.as_ref(), &core_config, trampoline)
        {
            Ok(stream) => {
                let boxed = Box::new(OutputStreamWrapper { inner: stream });
                unsafe {
                    *out_stream = Box::into_raw(boxed) as *mut OwnAudioOutputStreamHandle;
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

/// Starts (or resumes) audio output on the given stream.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_output_stream_play(
    stream: *mut OwnAudioOutputStreamHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { output_stream_from_ptr(stream) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        match wrapper.inner.play() {
            Ok(()) => OwnAudioErrorCode::Success as i32,
            Err(e) => {
                set_last_error(e.to_string());
                OwnAudioErrorCode::from(e) as i32
            }
        }
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Pauses audio output without destroying the stream.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_output_stream_pause(
    stream: *mut OwnAudioOutputStreamHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { output_stream_from_ptr(stream) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        match wrapper.inner.pause() {
            Ok(()) => OwnAudioErrorCode::Success as i32,
            Err(e) => {
                set_last_error(e.to_string());
                OwnAudioErrorCode::from(e) as i32
            }
        }
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Destroys an output stream and releases all associated resources.
///
/// Passing `null` is safe and has no effect.
#[no_mangle]
pub extern "C" fn ownaudio_v1_output_stream_destroy(stream: *mut OwnAudioOutputStreamHandle) {
    if stream.is_null() {
        return;
    }
    unsafe {
        drop(Box::from_raw(stream as *mut OutputStreamWrapper));
    }
}

// ---------------------------------------------------------------------------
// Input stream
// ---------------------------------------------------------------------------

/// Opens an input stream and writes its handle to `*out_stream`.
///
/// - `device_name` — null-terminated UTF-8 name of the target device, or
///   `null` to use the system default input device.
/// - `callback` — called on the audio thread with each captured buffer.
///
/// The stream starts in the paused state; call
/// `ownaudio_v1_input_stream_play` to begin capturing.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_open_input_stream(
    engine: *mut OwnAudioEngineHandle,
    device_name: *const c_char,
    config: *const OwnAudioStreamConfig,
    callback: OwnAudioInputCallback,
    user_data: *mut std::os::raw::c_void,
    out_stream: *mut *mut OwnAudioInputStreamHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if engine.is_null() || config.is_null() || out_stream.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }
        let cb = match callback {
            Some(f) => f,
            None => {
                set_last_error("input callback must not be null");
                return OwnAudioErrorCode::NullPointer as i32;
            }
        };

        let engine_wrapper = match unsafe { engine_from_ptr(engine) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        let c_config = unsafe { *config };
        let core_config: ownaudio_core::StreamConfig = c_config.into();
        let channels = c_config.channels;

        let device_info = parse_device_name(device_name);

        let trampoline = make_input_trampoline(cb, user_data, channels);

        match engine_wrapper
            .inner
            .open_input_stream(device_info.as_ref(), &core_config, trampoline)
        {
            Ok(stream) => {
                let boxed = Box::new(InputStreamWrapper { inner: stream });
                unsafe {
                    *out_stream = Box::into_raw(boxed) as *mut OwnAudioInputStreamHandle;
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

/// Starts (or resumes) audio capture on the given stream.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_input_stream_play(stream: *mut OwnAudioInputStreamHandle) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { input_stream_from_ptr(stream) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        match wrapper.inner.play() {
            Ok(()) => OwnAudioErrorCode::Success as i32,
            Err(e) => {
                set_last_error(e.to_string());
                OwnAudioErrorCode::from(e) as i32
            }
        }
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Pauses audio capture without destroying the stream.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_input_stream_pause(stream: *mut OwnAudioInputStreamHandle) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { input_stream_from_ptr(stream) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        match wrapper.inner.pause() {
            Ok(()) => OwnAudioErrorCode::Success as i32,
            Err(e) => {
                set_last_error(e.to_string());
                OwnAudioErrorCode::from(e) as i32
            }
        }
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Destroys an input stream and releases all associated resources.
///
/// Passing `null` is safe and has no effect.
#[no_mangle]
pub extern "C" fn ownaudio_v1_input_stream_destroy(stream: *mut OwnAudioInputStreamHandle) {
    if stream.is_null() {
        return;
    }
    unsafe {
        drop(Box::from_raw(stream as *mut InputStreamWrapper));
    }
}

// ---------------------------------------------------------------------------
// Internal helper
// ---------------------------------------------------------------------------

/// Converts a nullable C device name string to an `Option<AudioDeviceInfo>`.
///
/// Only the `name` field is populated; the engine uses it only for device
/// lookup and ignores the other fields when they are zero/false.
fn parse_device_name(device_name: *const c_char) -> Option<AudioDeviceInfo> {
    if device_name.is_null() {
        return None;
    }
    // SAFETY: caller guarantees the pointer is a valid null-terminated string.
    let name = unsafe { CStr::from_ptr(device_name) }
        .to_string_lossy()
        .into_owned();

    Some(AudioDeviceInfo {
        name,
        is_default_output: false,
        is_default_input: false,
        max_output_channels: 0,
        max_input_channels: 0,
        default_sample_rate: 0,
    })
}
