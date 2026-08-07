use std::ffi::CStr;
use std::os::raw::c_char;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::Arc;

use ownaudio_core::{ring_buffer_frames, AudioDeviceInfo};

use crate::callback::{
    make_input_trampoline, make_output_trampoline, OwnAudioInputCallback, OwnAudioOutputCallback,
};
use crate::error_code::{set_last_error, OwnAudioErrorCode};
use crate::ffi_config::OwnAudioStreamConfig;
use crate::handles::{
    engine_from_ptr, input_stream_from_ptr, mixer_from_ptr, output_stream_from_ptr, CaptureBridge,
    EngineWrapper, InputStreamWrapper, OutputStreamWrapper, OwnAudioEngineHandle,
    OwnAudioInputStreamHandle, OwnAudioMixerHandle, OwnAudioOutputStreamHandle, RenderBridge,
};
use crate::host_api::{resolve_host, OwnHostApi};

/// How deep the buffered-mode capture ring is, in seconds. Generous on purpose:
/// a host that is busy starting up can be hundreds of milliseconds late with its
/// first read, and the ring is what stands between that and lost audio.
const CAPTURE_RING_SECONDS: f32 = 2.0;

/// How deep the buffered-mode render ring is, in seconds. Kept short on purpose:
/// a producer that pushes as fast as it can keeps the ring full, so the depth is
/// what the host pays in output latency.
const RENDER_RING_SECONDS: f32 = 0.1;

// Engine lifecycle

/// Creates a new `AudioEngine` instance and writes its handle to `*out_handle`.
///
/// The handle must be released with `ownaudio_v1_engine_destroy` when no
/// longer needed.  A single engine can be used to open multiple streams.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
///
/// # Safety
/// - `out_handle` must point to a writable pointer slot; it receives the new handle.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_engine_create(
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

    crate::error_code::finish_catch_unwind(result)
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
///
/// # Safety
/// - `out_handle` must point to a writable pointer slot; it receives the new handle.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_engine_create_with_host(
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

    crate::error_code::finish_catch_unwind(result)
}

/// Destroys an engine handle created by `ownaudio_v1_engine_create`.
///
/// All streams opened from this engine must be destroyed before calling this
/// function.  Passing `null` is safe and has no effect.
///
/// # Safety
/// - `handle` must be a live handle from `ownaudio_v1_engine_create` that has not been destroyed.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_engine_destroy(handle: *mut OwnAudioEngineHandle) {
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

// Output stream

/// Opens an output stream and writes its handle to `*out_stream`.
///
/// - `engine` — a valid handle returned by `ownaudio_v1_engine_create`.
/// - `device_name` — null-terminated UTF-8 name of the target device, or
///   `null` to use the system default output device.
/// - `config` — pointer to a filled `OwnAudioStreamConfig`; must not be null.
/// - `callback` — function called on the audio thread for every buffer. Pass
///   `null` to run the stream buffered instead: the host then pushes audio with
///   `ownaudio_v1_output_stream_write` and the callback drains a native ring.
///   That is the preferred mode for managed hosts, since no foreign code — and
///   therefore no garbage collector — ever runs on the render thread.
/// - `user_data` — opaque pointer passed back to `callback`; may be null.
/// - `out_stream` — receives the new stream handle on success.
///
/// The stream starts in the paused state; call
/// `ownaudio_v1_output_stream_play` to begin audio output.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
///
/// # Safety
/// - `engine` must be a live handle from `ownaudio_v1_engine_create` that has not been destroyed.
/// - `device_name` must be a NUL-terminated UTF-8 string.
/// - `config` must point to an initialised `OwnAudioStreamConfig`.
/// - `user_data` is opaque to the engine and is only handed back to the callback; it must stay alive for as long as the stream runs.
/// - `out_stream` must point to a writable pointer slot; it receives the new handle.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_open_output_stream(
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
        let engine_wrapper = match unsafe { engine_from_ptr(engine) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        let c_config = unsafe { *config };
        let core_config: ownaudio_core::StreamConfig = c_config.into();
        let channels = c_config.channels;

        let device_info = parse_device_name(device_name);

        let (stream_result, render) = match callback {
            Some(cb) => {
                let trampoline = make_output_trampoline(cb, user_data, channels);
                let opened = engine_wrapper.inner.open_output_stream(
                    device_info.as_ref(),
                    &core_config,
                    trampoline,
                );
                (opened, None)
            }
            None => {
                let ch = channels.max(1) as usize;
                let capacity = ((core_config.sample_rate.max(1) as f32)
                    * (ch as f32)
                    * RENDER_RING_SECONDS) as usize;
                let (writer, mut reader) = ring_buffer_frames(capacity.max(ch), ch);

                let underruns = Arc::new(AtomicU64::new(0));
                let clear = Arc::new(AtomicBool::new(false));
                let underruns_cb = Arc::clone(&underruns);
                let clear_cb = Arc::clone(&clear);

                // Runs on the device audio thread. Drains the ring, fills the rest with
                // silence, and never reaches into foreign memory.
                let opened = engine_wrapper.inner.open_output_stream(
                    device_info.as_ref(),
                    &core_config,
                    move |buf: &mut [f32]| {
                        if clear_cb.swap(false, Ordering::Relaxed) {
                            reader.discard_all();
                        }

                        let filled = reader.read(buf);
                        if filled < buf.len() {
                            buf[filled..].fill(0.0);
                            underruns_cb
                                .fetch_add(((buf.len() - filled) / ch) as u64, Ordering::Relaxed);
                        }
                    },
                );

                (
                    opened,
                    Some(RenderBridge {
                        writer,
                        underrun_frames: underruns,
                        clear_requested: clear,
                    }),
                )
            }
        };

        match stream_result {
            Ok(stream) => {
                let boxed = Box::new(OutputStreamWrapper {
                    inner: stream,
                    render,
                });
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

    crate::error_code::finish_catch_unwind(result)
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
///
/// # Safety
/// - `engine` must be a live handle from `ownaudio_v1_engine_create` that has not been destroyed.
/// - `mixer` must be a live handle from `ownaudio_v1_mixer_create` that has not been destroyed.
/// - `device_name` must be a NUL-terminated UTF-8 string.
/// - `config` must point to an initialised `OwnAudioStreamConfig`.
/// - `out_stream` must point to a writable pointer slot; it receives the new handle.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_mixer_open_output_stream(
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
                let boxed = Box::new(OutputStreamWrapper {
                    inner: stream,
                    render: None,
                });
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

    crate::error_code::finish_catch_unwind(result)
}

/// Starts (or resumes) audio output on the given stream.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
///
/// # Safety
/// - `stream` must be a live handle from `ownaudio_v1_open_output_stream` or
///   `ownaudio_v1_mixer_open_output_stream`, not yet destroyed.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_output_stream_play(
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

    crate::error_code::finish_catch_unwind(result)
}

/// Pauses audio output without destroying the stream.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
///
/// # Safety
/// - `stream` must be a live handle from `ownaudio_v1_open_output_stream` or
///   `ownaudio_v1_mixer_open_output_stream`, not yet destroyed.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_output_stream_pause(
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

    crate::error_code::finish_catch_unwind(result)
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
///
/// # Safety
/// - `stream` must be a live handle from `ownaudio_v1_open_output_stream` or
///   `ownaudio_v1_mixer_open_output_stream`, not yet destroyed.
/// - `out_kind` must point to a writable `u32`.
/// - `out_count` must point to a writable `u64`.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_output_stream_get_error_state(
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

    crate::error_code::finish_catch_unwind(result)
}

/// Writes the stream's hardware playback latency (in frames) to `*out_frames`.
///
/// This is how far ahead of the DAC the audio callback runs — add it to a
/// playback position to know when a sample will actually be heard. cpal folds
/// the backend latency into its timestamps and the core reads it back each
/// callback, so the value is `0` until playback has started (no callback fired
/// yet) or when the backend does not report a latency.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success, `NullPointer` (6) if
/// `out_frames` is null, or `InvalidHandle` if `stream` is null / invalid.
///
/// # Safety
/// - `stream` must be a live handle from `ownaudio_v1_open_output_stream` or
///   `ownaudio_v1_mixer_open_output_stream`, not yet destroyed.
/// - `out_frames` must point to a writable `u32`.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_output_stream_get_latency_frames(
    stream: *mut OwnAudioOutputStreamHandle,
    out_frames: *mut u32,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_frames.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }
        let wrapper = match unsafe { output_stream_from_ptr(stream) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };
        unsafe {
            *out_frames = wrapper.inner.latency_frames();
        }
        OwnAudioErrorCode::Success as i32
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Pushes interleaved samples into a buffered stream's render ring, writing the
/// sample count actually taken to `*out_written`.
///
/// Takes only whole frames and never blocks: a short write means the ring is
/// full, and the caller should back off and retry rather than drop the tail.
/// Returns `InternalError` (9) if the stream was opened with a callback and
/// therefore has no ring to write.
///
/// # Safety
/// - `stream` must be a live handle from `ownaudio_v1_open_output_stream` that has not been destroyed.
/// - `src` must point to at least `src_len` readable `f32` values.
/// - `out_written` must point to a writable `usize`.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_output_stream_write(
    stream: *mut OwnAudioOutputStreamHandle,
    src: *const f32,
    src_len: usize,
    out_written: *mut usize,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if src.is_null() || out_written.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }
        let wrapper = match unsafe { output_stream_from_ptr(stream) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };
        let bridge = match wrapper.render.as_mut() {
            Some(b) => b,
            None => {
                set_last_error(
                    "output stream was opened with a callback; there is no ring to write",
                );
                return OwnAudioErrorCode::InternalError as i32;
            }
        };

        let input = unsafe { std::slice::from_raw_parts(src, src_len) };
        unsafe {
            *out_written = bridge.writer.write(input);
        }
        OwnAudioErrorCode::Success as i32
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Writes the number of samples currently queued for playback to `*out_samples`.
///
/// This is the host's view of how far ahead of the DAC it has pushed. `0` on a
/// callback-mode stream, which has no ring.
///
/// # Safety
/// - `stream` must be a live handle from `ownaudio_v1_open_output_stream` that has not been destroyed.
/// - `out_samples` must point to a writable `usize`.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_output_stream_get_queued_samples(
    stream: *mut OwnAudioOutputStreamHandle,
    out_samples: *mut usize,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_samples.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }
        let wrapper = match unsafe { output_stream_from_ptr(stream) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };
        unsafe {
            *out_samples = wrapper.render.as_ref().map_or(0, |b| b.writer.queued());
        }
        OwnAudioErrorCode::Success as i32
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Asks a buffered stream to drop whatever is queued in its render ring.
///
/// The flush happens on the next callback, because only the reader may move the
/// read side. Meant for stop/seek, so playback never resumes with stale audio.
/// No-op on a callback-mode stream.
///
/// # Safety
/// - `stream` must be a live handle from `ownaudio_v1_open_output_stream` that has not been destroyed.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_output_stream_clear(
    stream: *mut OwnAudioOutputStreamHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { output_stream_from_ptr(stream) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };
        if let Some(bridge) = wrapper.render.as_ref() {
            bridge.clear_requested.store(true, Ordering::Relaxed);
        }
        OwnAudioErrorCode::Success as i32
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Writes the number of frames the render callback had to fill with silence to
/// `*out_frames`.
///
/// Frames go silent when the host does not keep the ring fed. Cumulative for the
/// life of the stream, and always `0` on a callback-mode stream. Note that a
/// paused-but-playing stream naturally accumulates these.
///
/// # Safety
/// - `stream` must be a live handle from `ownaudio_v1_open_output_stream` that has not been destroyed.
/// - `out_frames` must point to a writable `u64`.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_output_stream_get_underrun_frames(
    stream: *mut OwnAudioOutputStreamHandle,
    out_frames: *mut u64,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_frames.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }
        let wrapper = match unsafe { output_stream_from_ptr(stream) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };
        unsafe {
            *out_frames = wrapper
                .render
                .as_ref()
                .map_or(0, |b| b.underrun_frames.load(Ordering::Relaxed));
        }
        OwnAudioErrorCode::Success as i32
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Destroys an output stream and releases all associated resources.
///
/// Passing `null` is safe and has no effect.
///
/// # Safety
/// - `stream` must be a live handle from `ownaudio_v1_open_output_stream` or
///   `ownaudio_v1_mixer_open_output_stream`, not yet destroyed.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_output_stream_destroy(
    stream: *mut OwnAudioOutputStreamHandle,
) {
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

// Input stream

/// Opens an input stream and writes its handle to `*out_stream`.
///
/// - `device_name` — null-terminated UTF-8 name of the target device, or
///   `null` to use the system default input device.
/// - `callback` — called on the audio thread with each captured buffer. Pass
///   `null` to run the stream buffered instead: capture then lands in a native
///   ring that the host drains with `ownaudio_v1_input_stream_read`. That is the
///   preferred mode for managed hosts, since no foreign code — and therefore no
///   garbage collector — ever runs on the capture thread.
///
/// The stream starts in the paused state; call
/// `ownaudio_v1_input_stream_play` to begin capturing.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
///
/// # Safety
/// - `engine` must be a live handle from `ownaudio_v1_engine_create` that has not been destroyed.
/// - `device_name` must be a NUL-terminated UTF-8 string.
/// - `config` must point to an initialised `OwnAudioStreamConfig`.
/// - `user_data` is opaque to the engine and is only handed back to the callback; it must stay alive for as long as the stream runs.
/// - `out_stream` must point to a writable pointer slot; it receives the new handle.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_open_input_stream(
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
        let engine_wrapper = match unsafe { engine_from_ptr(engine) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        let c_config = unsafe { *config };
        let core_config: ownaudio_core::StreamConfig = c_config.into();
        let channels = c_config.channels;

        let device_info = parse_device_name(device_name);

        let (stream_result, capture) = match callback {
            Some(cb) => {
                let trampoline = make_input_trampoline(cb, user_data, channels);
                let opened = engine_wrapper.inner.open_input_stream(
                    device_info.as_ref(),
                    &core_config,
                    trampoline,
                );
                (opened, None)
            }
            None => {
                let ch = channels.max(1) as usize;
                let capacity = ((core_config.sample_rate.max(1) as f32)
                    * (ch as f32)
                    * CAPTURE_RING_SECONDS) as usize;
                let (mut writer, reader) = ring_buffer_frames(capacity.max(ch), ch);

                let dropped = Arc::new(AtomicU64::new(0));
                let dropped_cb = Arc::clone(&dropped);

                // Runs on the device audio thread. Non-blocking, allocation-free, and the
                // only place capture data moves — it never reaches into foreign memory.
                let opened = engine_wrapper.inner.open_input_stream(
                    device_info.as_ref(),
                    &core_config,
                    move |data: &[f32]| {
                        let written = writer.write(data);
                        if written < data.len() {
                            dropped_cb
                                .fetch_add(((data.len() - written) / ch) as u64, Ordering::Relaxed);
                        }
                    },
                );

                (
                    opened,
                    Some(CaptureBridge {
                        reader,
                        dropped_frames: dropped,
                    }),
                )
            }
        };

        match stream_result {
            Ok(stream) => {
                let boxed = Box::new(InputStreamWrapper {
                    inner: stream,
                    capture,
                });
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

    crate::error_code::finish_catch_unwind(result)
}

/// Starts (or resumes) audio capture on the given stream.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
///
/// # Safety
/// - `stream` must be a live handle from `ownaudio_v1_open_input_stream` that has not been destroyed.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_input_stream_play(
    stream: *mut OwnAudioInputStreamHandle,
) -> i32 {
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

    crate::error_code::finish_catch_unwind(result)
}

/// Pauses audio capture without destroying the stream.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
///
/// # Safety
/// - `stream` must be a live handle from `ownaudio_v1_open_input_stream` that has not been destroyed.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_input_stream_pause(
    stream: *mut OwnAudioInputStreamHandle,
) -> i32 {
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

    crate::error_code::finish_catch_unwind(result)
}

/// Polls the input stream's error state. See
/// `ownaudio_v1_output_stream_get_error_state` for semantics; the input path is
/// identical.
///
/// # Safety
/// - `stream` must be a live handle from `ownaudio_v1_open_input_stream` that has not been destroyed.
/// - `out_kind` must point to a writable `u32`.
/// - `out_count` must point to a writable `u64`.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_input_stream_get_error_state(
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

    crate::error_code::finish_catch_unwind(result)
}

/// Writes the stream's hardware capture latency (in frames) to `*out_frames`.
///
/// This is how long ago the samples in each buffer actually hit the ADC —
/// subtract it from the capture position to line a recording up with the real
/// timeline. cpal folds the backend latency into its timestamps and the core
/// reads it back each callback, so the value is `0` until capture has started
/// (no callback fired yet) or when the backend does not report a latency.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success, `NullPointer` (6) if
/// `out_frames` is null, or `InvalidHandle` if `stream` is null / invalid.
///
/// # Safety
/// - `stream` must be a live handle from `ownaudio_v1_open_input_stream` that has not been destroyed.
/// - `out_frames` must point to a writable `u32`.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_input_stream_get_latency_frames(
    stream: *mut OwnAudioInputStreamHandle,
    out_frames: *mut u32,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_frames.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }
        let wrapper = match unsafe { input_stream_from_ptr(stream) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };
        unsafe {
            *out_frames = wrapper.inner.latency_frames();
        }
        OwnAudioErrorCode::Success as i32
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Drains captured samples from a stream opened in buffered mode (null callback)
/// into `dst`, writing the sample count actually taken to `*out_read`.
///
/// Reads only whole frames, so the caller can always slice the result into
/// interleaved frames without the channels sliding. Returns `0` samples when the
/// ring is empty rather than blocking, and `InternalError` (9) if the stream was
/// opened with a callback and therefore has no ring to read.
///
/// # Safety
/// - `stream` must be a live handle from `ownaudio_v1_open_input_stream` that has not been destroyed.
/// - `dst` must point to at least `dst_len` writable `f32` slots.
/// - `out_read` must point to a writable `usize`.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_input_stream_read(
    stream: *mut OwnAudioInputStreamHandle,
    dst: *mut f32,
    dst_len: usize,
    out_read: *mut usize,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if dst.is_null() || out_read.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }
        let wrapper = match unsafe { input_stream_from_ptr(stream) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };
        let bridge = match wrapper.capture.as_mut() {
            Some(b) => b,
            None => {
                set_last_error("input stream was opened with a callback; there is no ring to read");
                return OwnAudioErrorCode::InternalError as i32;
            }
        };

        let out = unsafe { std::slice::from_raw_parts_mut(dst, dst_len) };
        unsafe {
            *out_read = bridge.reader.read(out);
        }
        OwnAudioErrorCode::Success as i32
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Throws away everything sitting in a buffered stream's capture ring.
///
/// Meant for stop/start, so a new take never opens with the tail of the old one.
/// Call it while capture is paused: it only moves the read side, so samples the
/// callback writes concurrently may survive. No-op on a callback-mode stream.
///
/// # Safety
/// - `stream` must be a live handle from `ownaudio_v1_open_input_stream` that has not been destroyed.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_input_stream_clear(
    stream: *mut OwnAudioInputStreamHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { input_stream_from_ptr(stream) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };
        if let Some(bridge) = wrapper.capture.as_mut() {
            bridge.reader.discard_all();
        }
        OwnAudioErrorCode::Success as i32
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Writes the number of capture frames dropped so far to `*out_frames`.
///
/// Frames get dropped when the host leaves the capture ring unread for longer
/// than it is deep. Cumulative for the life of the stream, and always `0` on a
/// callback-mode stream. Anything above zero means the recording has a hole in it.
///
/// # Safety
/// - `stream` must be a live handle from `ownaudio_v1_open_input_stream` that has not been destroyed.
/// - `out_frames` must point to a writable `u64`.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_input_stream_get_dropped_frames(
    stream: *mut OwnAudioInputStreamHandle,
    out_frames: *mut u64,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_frames.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }
        let wrapper = match unsafe { input_stream_from_ptr(stream) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };
        unsafe {
            *out_frames = wrapper
                .capture
                .as_ref()
                .map_or(0, |b| b.dropped_frames.load(Ordering::Relaxed));
        }
        OwnAudioErrorCode::Success as i32
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Destroys an input stream and releases all associated resources.
///
/// Passing `null` is safe and has no effect.
///
/// # Safety
/// - `stream` must be a live handle from `ownaudio_v1_open_input_stream` that has not been destroyed.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_input_stream_destroy(stream: *mut OwnAudioInputStreamHandle) {
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

// Internal helper

/// Converts a nullable C device name string to an `Option<AudioDeviceInfo>`.
///
/// Only the `name` field is populated; the engine uses it only for device
/// lookup and ignores the other fields when they are zero/false.
pub(crate) fn parse_device_name(device_name: *const c_char) -> Option<AudioDeviceInfo> {
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
