use std::ffi::CStr;
use std::os::raw::c_char;

use ownaudio_core::AudioDeviceInfo;

use crate::callback::{make_input_trampoline, make_output_trampoline, OwnAudioInputCallback, OwnAudioOutputCallback};
use crate::error_code::{set_last_error, OwnAudioErrorCode};
use crate::ffi_config::OwnAudioStreamConfig;
use crate::handles::{
    engine_from_ptr, input_stream_from_ptr, mixer_from_ptr, output_stream_from_ptr, EngineWrapper,
    InputStreamWrapper, OwnAudioEngineHandle, OwnAudioInputStreamHandle, OwnAudioMixerHandle,
    OwnAudioOutputStreamHandle, OutputStreamWrapper,
};
use crate::host_api::{resolve_host, OwnHostApi};

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

/// Creates a new `AudioEngine` instance using an explicitly chosen host API, and writes
/// its handle to `*out_handle`.
///
/// - `host_api` — the audio host API to use (e.g. `OwnHostApi::Asio`).
///   Pass `OwnHostApi::Wasapi` / `OwnHostApi::CoreAudio` / `OwnHostApi::Alsa`
///   to request the standard platform backend without relying on the compile-time default.
/// - `out_handle` — receives the new engine handle on success.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
/// Returns `OwnAudioErrorCode::HostApiNotAvailable` (10) when the requested
/// host API is not compiled into this binary.
/// Returns `OwnAudioErrorCode::AsioDriverNotFound` (11) when ASIO is compiled
/// in but no ASIO driver is installed on this machine.
///
/// If `out_handle` is null returns `OwnAudioErrorCode::NullPointer` (6).
#[no_mangle]
pub extern "C" fn ownaudio_v1_engine_create_with_host(
    host_api: OwnHostApi,
    out_handle: *mut *mut OwnAudioEngineHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_handle.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let host = match resolve_host(host_api) {
            Ok(h) => h,
            Err(code) => return code,
        };

        match ownaudio_core::AudioEngine::new_with_host(host) {
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
    // A panic in the engine's Drop must never unwind across the FFI boundary.
    let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if handle.is_null() {
            return;
        }
        // SAFETY: handle was produced by Box::into_raw in engine_create.
        unsafe {
            drop(Box::from_raw(handle as *mut EngineWrapper));
        }
    }));
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

/// Opens an output stream **driven by a multi-track mixer** and writes its
/// handle to `*out_stream`.
///
/// Unlike `ownaudio_v1_open_output_stream` (which calls back into C# for every
/// buffer), this moves the mixer onto the cpal audio thread: on every callback
/// the stream calls [`MultiTrackMixer::mix`], which drains the lock-free command
/// queue and renders all active tracks — no per-buffer P/Invoke, no GC, no
/// cross-thread data race.
///
/// - `engine` — a valid handle returned by `ownaudio_v1_engine_create`.
/// - `mixer` — a valid handle returned by `ownaudio_v1_mixer_create`; its
///   sample rate and channel count should match `config`.
/// - `device_name` — null-terminated UTF-8 device name, or `null` for the
///   system default output device.
/// - `config` — pointer to a filled `OwnAudioStreamConfig`; must not be null.
/// - `out_stream` — receives the new stream handle on success.
///
/// The mixer is consumed: after this call the mixer handle keeps working for
/// structural changes and parameter access (via its command queue), but the
/// mixer can no longer be attached to another stream.  Calling this twice on the
/// same mixer returns `OwnAudioErrorCode::InvalidHandle`.
///
/// The stream starts paused; call `ownaudio_v1_output_stream_play` to begin
/// output.  Destroy the stream before destroying the mixer.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
///
/// [`MultiTrackMixer::mix`]: ownaudio_core::MultiTrackMixer::mix
#[no_mangle]
pub extern "C" fn ownaudio_v1_mixer_open_output_stream(
    engine: *mut OwnAudioEngineHandle,
    mixer: *mut OwnAudioMixerHandle,
    device_name: *const c_char,
    config: *const OwnAudioStreamConfig,
    out_stream: *mut *mut OwnAudioOutputStreamHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if engine.is_null() || mixer.is_null() || config.is_null() || out_stream.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let mixer_wrapper = match unsafe { mixer_from_ptr(mixer) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        // The mixer renders into the device buffer assuming its own channel
        // count and rate; a mismatch with the stream config would silently
        // misinterpret the interleaved frames, so reject it up front.
        let c_config = unsafe { *config };
        if c_config.channels != mixer_wrapper.channels
            || c_config.sample_rate as f32 != mixer_wrapper.sample_rate
        {
            set_last_error(format!(
                "stream config ({} Hz, {} ch) does not match mixer ({} Hz, {} ch)",
                c_config.sample_rate,
                c_config.channels,
                mixer_wrapper.sample_rate,
                mixer_wrapper.channels,
            ));
            return OwnAudioErrorCode::UnsupportedConfig as i32;
        }

        // Take exclusive ownership of the mixer for the audio thread.  If it was
        // already moved onto a stream, refuse rather than aliasing it.
        let mut multitrack = match mixer_wrapper.mixer.take() {
            Some(m) => m,
            None => {
                set_last_error("mixer is already attached to an output stream");
                return OwnAudioErrorCode::InvalidHandle as i32;
            }
        };

        let engine_wrapper = match unsafe { engine_from_ptr(engine) } {
            Some(w) => w,
            None => {
                // Restore the mixer so the caller can retry / destroy cleanly.
                mixer_wrapper.mixer = Some(multitrack);
                return OwnAudioErrorCode::InvalidHandle as i32;
            }
        };

        let core_config: ownaudio_core::StreamConfig = c_config.into();

        let device_info = parse_device_name(device_name);

        // The mixer renders directly into the device buffer; its `mix` drains the
        // command queue allocation-free at the top of every block.
        let open = engine_wrapper.inner.open_output_stream(
            device_info.as_ref(),
            &core_config,
            move |buf: &mut [f32]| {
                multitrack.mix(buf);
            },
        );

        match open {
            Ok(stream) => {
                let boxed = Box::new(OutputStreamWrapper { inner: stream });
                unsafe {
                    *out_stream = Box::into_raw(boxed) as *mut OwnAudioOutputStreamHandle;
                }
                OwnAudioErrorCode::Success as i32
            }
            Err(e) => {
                // The closure (and the mixer it captured) is dropped on error;
                // the mixer cannot be recovered, but the handle stays valid for
                // destruction.  Report the failure to the caller.
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

/// Polls the output stream's error state, writing the most recent error kind to
/// `*out_kind` and the total error count to `*out_count`.
///
/// The audio backend delivers device-lost / backend errors on an internal
/// callback that the core records into a lock-free shared state; this call reads
/// it without disturbing the audio thread. The control side polls it (e.g. on its
/// periodic tick) and, when `*out_count` increases, raises a device-lost / fault
/// event.
///
/// `*out_kind` maps to the `OwnAudioStreamErrorKind` enum:
/// `0` = None, `1` = DeviceNotAvailable, `2` = BackendSpecific.
///
/// Either out-pointer may be null to skip that field. Returns
/// `OwnAudioErrorCode::Success` (0) on success, or `InvalidHandle` if `stream`
/// is null / invalid.
#[no_mangle]
pub extern "C" fn ownaudio_v1_output_stream_get_error_state(
    stream: *mut OwnAudioOutputStreamHandle,
    out_kind: *mut u32,
    out_count: *mut u64,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { output_stream_from_ptr(stream) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };
        let state = wrapper.inner.error_state();
        if !out_kind.is_null() {
            unsafe {
                *out_kind = state.kind() as u32;
            }
        }
        if !out_count.is_null() {
            unsafe {
                *out_count = state.count();
            }
        }
        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Destroys an output stream and releases all associated resources.
///
/// Passing `null` is safe and has no effect.
#[no_mangle]
pub extern "C" fn ownaudio_v1_output_stream_destroy(stream: *mut OwnAudioOutputStreamHandle) {
    // A panic while stopping/dropping the stream must not cross the FFI boundary.
    let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if stream.is_null() {
            return;
        }
        unsafe {
            drop(Box::from_raw(stream as *mut OutputStreamWrapper));
        }
    }));
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

/// Polls the input stream's error state. See
/// `ownaudio_v1_output_stream_get_error_state` for semantics; the input path is
/// identical.
#[no_mangle]
pub extern "C" fn ownaudio_v1_input_stream_get_error_state(
    stream: *mut OwnAudioInputStreamHandle,
    out_kind: *mut u32,
    out_count: *mut u64,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { input_stream_from_ptr(stream) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };
        let state = wrapper.inner.error_state();
        if !out_kind.is_null() {
            unsafe {
                *out_kind = state.kind() as u32;
            }
        }
        if !out_count.is_null() {
            unsafe {
                *out_count = state.count();
            }
        }
        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Destroys an input stream and releases all associated resources.
///
/// Passing `null` is safe and has no effect.
#[no_mangle]
pub extern "C" fn ownaudio_v1_input_stream_destroy(stream: *mut OwnAudioInputStreamHandle) {
    // A panic while stopping/dropping the stream must not cross the FFI boundary.
    let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if stream.is_null() {
            return;
        }
        unsafe {
            drop(Box::from_raw(stream as *mut InputStreamWrapper));
        }
    }));
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
