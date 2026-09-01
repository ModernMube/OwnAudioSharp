//! FFI exports for the shared capture bridge.
//!
//! [`ownaudio_v1_capture_open`] opens the configured input device **once**, at its full
//! physical width, and every live track then attaches a tap to it
//! ([`ownaudio_v1_track_attach_capture`]) naming the channels it wants. The capture
//! callback de-interleaves each tap into its own lock-free ring, whose reader is installed
//! as the track's source — the same native-only path
//! [`ownaudio_v1_track_open_input`](crate::ffi_input_source::ownaudio_v1_track_open_input)
//! uses, minus one stream per track.
//!
//! The per-track form still exists and still works; this is the road for anything that
//! wants several live inputs at once. On ASIO it is the only road: a driver takes one
//! client, and every registered callback walks its channel buffers again.

use std::os::raw::c_char;
use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::Arc;

use ownaudio_core::{
    capture_channel, ring_buffer_frames, CaptureTap, SampleFormat, StreamConfig, MAX_ROUTE_CHANNELS,
};

use crate::error_code::{set_last_error, OwnAudioErrorCode};
use crate::ffi_stream::parse_device_name;
use crate::handles::{
    capture_from_ptr, engine_from_ptr, mixer_from_ptr, track_from_ptr, CaptureState,
    CaptureWrapper, InputPeaks, OwnAudioCaptureHandle, OwnAudioEngineHandle, OwnAudioMixerHandle,
    OwnAudioTrackHandle,
};

/// Per-track ring depth, in seconds of capture. Same sizing as the per-track input bridge:
/// enough to ride out scheduling jitter between the capture and render callbacks, and
/// near-empty in steady state.
const CAPTURE_RING_SECONDS: f32 = 0.5;

/// Depth of the attach/detach queue. Taps only change when a track is added, removed or
/// re-routed, so this is generous.
const CAPTURE_COMMAND_CAPACITY: usize = 64;

/// Opens the shared capture bridge on `device_name` and writes its handle to `*out_capture`.
///
/// The device is opened at **its own** channel count — all the physical inputs at once —
/// so each track can pick its channels out of the single stream; ask
/// [`ownaudio_v1_capture_channel_count`] what that turned out to be. Resolution goes through
/// exactly the path a normal input stream takes, which on ASIO means the same `cpal::Device`
/// as the output: the bridge never lands on a card the configuration did not name.
///
/// The stream starts paused; call `ownaudio_v1_capture_play`.
///
/// - `engine` — valid engine handle owning the input device.
/// - `device_name` — null-terminated UTF-8 device name, or null for the default.
/// - `sample_rate` — capture sample rate in Hz.
/// - `buffer_frames` — device buffer size in frames; `0` lets the engine choose.
/// - `out_capture` — receives the bridge handle on success.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
///
/// # Safety
/// - `engine` must be a live handle from `ownaudio_v1_engine_create` that has not been destroyed.
/// - `device_name` must be a NUL-terminated UTF-8 string.
/// - `out_capture` must point to a writable pointer slot; it receives the new handle.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_capture_open(
    engine: *mut OwnAudioEngineHandle,
    device_name: *const c_char,
    sample_rate: u32,
    buffer_frames: u32,
    out_capture: *mut *mut OwnAudioCaptureHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_capture.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let engine_wrapper = match unsafe { engine_from_ptr(engine) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        let device_info = parse_device_name(device_name);
        let channels = match engine_wrapper
            .inner
            .max_input_channels(device_info.as_ref())
        {
            Ok(n) => n.min(MAX_ROUTE_CHANNELS as u16),
            Err(e) => {
                set_last_error(e.to_string());
                return OwnAudioErrorCode::from(e) as i32;
            }
        };

        let config = StreamConfig {
            sample_rate,
            channels,
            sample_format: SampleFormat::F32,
            buffer_size_frames: (buffer_frames != 0).then_some(buffer_frames),
        };

        let (controller, mut hub) = capture_channel(CAPTURE_COMMAND_CAPACITY, channels);
        let peaks = Arc::new(InputPeaks {
            left: AtomicU32::new(0.0f32.to_bits()),
            right: AtomicU32::new(0.0f32.to_bits()),
        });

        // The capture callback owns the hub: it applies queued attach/detach requests and
        // fans the block out to every tap. Nothing here allocates once running, and no
        // audio data ever reaches managed code.
        let ch = channels.max(1) as usize;
        let peaks_cb = Arc::clone(&peaks);
        let stream_result = engine_wrapper.inner.open_input_stream(
            device_info.as_ref(),
            &config,
            move |data: &[f32]| {
                let mut peak_l = 0.0f32;
                let mut peak_r = 0.0f32;
                for frame in data.chunks(ch) {
                    let l = frame[0].abs();
                    if l > peak_l {
                        peak_l = l;
                    }
                    if let Some(&s) = frame.get(1) {
                        let r = s.abs();
                        if r > peak_r {
                            peak_r = r;
                        }
                    }
                }
                if ch == 1 {
                    peak_r = peak_l;
                }
                peaks_cb.left.store(peak_l.to_bits(), Ordering::Relaxed);
                peaks_cb.right.store(peak_r.to_bits(), Ordering::Relaxed);

                hub.on_capture(data);
            },
        );

        let stream = match stream_result {
            Ok(s) => s,
            Err(e) => {
                set_last_error(e.to_string());
                return OwnAudioErrorCode::from(e) as i32;
            }
        };

        let boxed = Box::new(CaptureWrapper::new(CaptureState {
            stream,
            controller,
            channels,
            peaks,
        }));
        unsafe {
            *out_capture = Box::into_raw(boxed) as *mut OwnAudioCaptureHandle;
        }

        OwnAudioErrorCode::Success as i32
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Writes the bridge's physical capture channel count to `*out_channels`.
///
/// This is the range a tap's channel map may address, and the number of input sockets a UI
/// should draw — the requested width adapted to what the device actually offers.
///
/// # Safety
/// - `capture` must be a live handle from `ownaudio_v1_capture_open` that has not been destroyed.
/// - `out_channels` must point to a writable `u16`.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_capture_channel_count(
    capture: *mut OwnAudioCaptureHandle,
    out_channels: *mut u16,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_channels.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }
        let wrapper = match unsafe { capture_from_ptr(capture) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };
        unsafe {
            *out_channels = wrapper.channels;
        }
        OwnAudioErrorCode::Success as i32
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Attaches `track` to the capture bridge, taking capture channel `map[i]` as the track's
/// channel `i`.
///
/// Installs a fresh ring as the track's source and sets the track's processing width to
/// `len`, so a mono vocal input costs a mono chain no matter how wide the bus is.
/// Re-attaching the same track replaces its map — that is how a live re-route works, with
/// no stream anywhere near it.
///
/// - `mixer` — valid mixer handle that owns the track.
/// - `track` — valid track handle whose source is installed.
/// - `capture` — valid bridge handle.
/// - `map` — `len` zero-based capture-channel indices, one per track channel.
/// - `len` — track-side channel count, at most [`MAX_ROUTE_CHANNELS`].
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
///
/// # Safety
/// - `mixer`, `track` and `capture` must be live handles that have not been destroyed.
/// - `map` must be valid for `len` `u32` values (readable).
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_track_attach_capture(
    mixer: *mut OwnAudioMixerHandle,
    track: *mut OwnAudioTrackHandle,
    capture: *mut OwnAudioCaptureHandle,
    map: *const u32,
    len: usize,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if map.is_null() || len == 0 {
            return OwnAudioErrorCode::NullPointer as i32;
        }
        if len > MAX_ROUTE_CHANNELS {
            set_last_error(format!(
                "capture map covers {len} channels, at most {MAX_ROUTE_CHANNELS} are routable"
            ));
            return OwnAudioErrorCode::UnsupportedConfig as i32;
        }

        let track_wrapper = match unsafe { track_from_ptr(track) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };
        let mut mixer_wrapper = match unsafe { mixer_from_ptr(mixer) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };
        let mut capture_wrapper = match unsafe { capture_from_ptr(capture) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        let channel_map = unsafe { std::slice::from_raw_parts(map, len) };
        let track_id = track_wrapper.id;
        let rate = mixer_wrapper.sample_rate.max(1.0);
        let capacity = (rate * len as f32 * CAPTURE_RING_SECONDS) as usize;
        let (writer, reader) = ring_buffer_frames(capacity.max(len), len);

        if capture_wrapper
            .controller
            .attach(CaptureTap::new(track_id, channel_map, writer))
            .is_err()
        {
            set_last_error("capture command queue is full; tap not attached");
            return OwnAudioErrorCode::InternalError as i32;
        }

        // The track works at the tap's width, and only takes the source once the tap is in
        // — a failed attach must not leave a silent orphan reader on the track.
        track_wrapper.shared.set_source_channels(len as u16);
        if mixer_wrapper
            .controller
            .set_track_source(track_id, Some(Box::new(reader)))
            .is_err()
        {
            let _ = capture_wrapper.controller.detach(track_id);
            set_last_error("mixer command queue is full; capture source not set");
            return OwnAudioErrorCode::InternalError as i32;
        }

        OwnAudioErrorCode::Success as i32
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Stops feeding `track_id` from the bridge. The track keeps its ring reader until its
/// source is cleared or it is removed, so it simply underruns into silence.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
///
/// # Safety
/// - `capture` must be a live handle from `ownaudio_v1_capture_open` that has not been destroyed.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_track_detach_capture(
    capture: *mut OwnAudioCaptureHandle,
    track: *mut OwnAudioTrackHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let track_wrapper = match unsafe { track_from_ptr(track) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };
        let mut capture_wrapper = match unsafe { capture_from_ptr(capture) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        match capture_wrapper.controller.detach(track_wrapper.id) {
            Ok(()) => OwnAudioErrorCode::Success as i32,
            Err(e) => {
                set_last_error(e.to_string());
                OwnAudioErrorCode::InternalError as i32
            }
        }
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Starts (or resumes) capture on the bridge, feeding every attached tap.
///
/// # Safety
/// - `capture` must be a live handle from `ownaudio_v1_capture_open` that has not been destroyed.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_capture_play(capture: *mut OwnAudioCaptureHandle) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { capture_from_ptr(capture) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        match wrapper.stream.play() {
            Ok(()) => OwnAudioErrorCode::Success as i32,
            Err(e) => {
                set_last_error(e.to_string());
                OwnAudioErrorCode::from(e) as i32
            }
        }
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Pauses capture. Whatever is already in the taps' rings still plays out.
///
/// # Safety
/// - `capture` must be a live handle from `ownaudio_v1_capture_open` that has not been destroyed.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_capture_pause(capture: *mut OwnAudioCaptureHandle) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { capture_from_ptr(capture) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        match wrapper.stream.pause() {
            Ok(()) => OwnAudioErrorCode::Success as i32,
            Err(e) => {
                set_last_error(e.to_string());
                OwnAudioErrorCode::from(e) as i32
            }
        }
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Writes the bridge's most recent capture peaks — measured over the first two physical
/// channels — to `*out_left` / `*out_right`.
///
/// Per-track levels come from the track's own peaks; this is the device-side meter.
///
/// # Safety
/// - `capture` must be a live handle from `ownaudio_v1_capture_open` that has not been destroyed.
/// - `out_left` and `out_right` must point to writable `f32`s.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_capture_get_peaks(
    capture: *mut OwnAudioCaptureHandle,
    out_left: *mut f32,
    out_right: *mut f32,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_left.is_null() || out_right.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }
        let wrapper = match unsafe { capture_from_ptr(capture) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        unsafe {
            *out_left = f32::from_bits(wrapper.peaks.left.load(Ordering::Relaxed));
            *out_right = f32::from_bits(wrapper.peaks.right.load(Ordering::Relaxed));
        }
        OwnAudioErrorCode::Success as i32
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Polls the bridge's error state, writing the most recent error kind to `*out_kind` and
/// the monotonic error count to `*out_count`.
///
/// The counterpart of `ownaudio_v1_output_stream_get_error_state` on the capture side:
/// without it a device lost mid-session just goes quiet, with every track that taps it.
/// `*out_kind` is `0` = None, `1` = DeviceNotAvailable, `2` = BackendSpecific, and either
/// out-pointer may be null to skip that field.
///
/// # Safety
/// - `capture` must be a live handle from `ownaudio_v1_capture_open` that has not been destroyed.
/// - `out_kind` must point to a writable `u32`, `out_count` to a writable `u64`.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_capture_get_error_state(
    capture: *mut OwnAudioCaptureHandle,
    out_kind: *mut u32,
    out_count: *mut u64,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { capture_from_ptr(capture) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        let state = wrapper.stream.error_state();
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

/// Closes the bridge, stopping capture and releasing the input stream.
///
/// Passing `null` is safe and has no effect. Attached tracks keep their ring readers until
/// their sources are cleared or they are removed; after this call those simply underrun.
///
/// # Safety
/// - `capture` must be a live handle from `ownaudio_v1_capture_open` that has not been destroyed.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_capture_close(capture: *mut OwnAudioCaptureHandle) {
    let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if capture.is_null() {
            return;
        }
        unsafe {
            drop(Box::from_raw(capture as *mut CaptureWrapper));
        }
    }));
}
