//! FFI exports for input-capture track sources.
//!
//! [`ownaudio_v1_track_open_input`] bridges a device input stream to a mixer track
//! entirely on the native side: it creates a lock-free ring buffer, installs the
//! read side as the track's source (via the mixer command queue, so the audio
//! thread takes ownership without a data race), and opens a native input stream
//! whose capture callback writes captured samples straight into the write side.
//! No managed callback is involved, so no audio data ever crosses into managed
//! code and the GC can never stall the capture or render path. The control thread
//! only starts/stops capture and reads metering peaks.

use std::os::raw::c_char;
use std::sync::atomic::Ordering;
use std::sync::Arc;

use ownaudio_core::{ring_buffer, SampleFormat, StreamConfig};

use crate::error_code::{set_last_error, OwnAudioErrorCode};
use crate::ffi_stream::parse_device_name;
use crate::handles::{
    engine_from_ptr, input_source_from_ptr, mixer_from_ptr, track_from_ptr, InputPeaks,
    InputSourceWrapper, OwnAudioEngineHandle, OwnAudioInputSourceHandle, OwnAudioMixerHandle,
    OwnAudioTrackHandle,
};

/// Ring-buffer capacity for the input bridge, in seconds. Sized to absorb
/// scheduling jitter between the capture and render callbacks; the ring stays
/// near-empty in steady state (it drains at the output rate ≈ the capture rate).
const INPUT_RING_SECONDS: f32 = 0.5;

/// Opens an input stream on `track`, wiring device capture straight into the
/// track's ring buffer, and writes the control handle to `*out_input`.
///
/// The stream is created paused; start capture with
/// `ownaudio_v1_input_source_play`. The device is opened at `sample_rate` /
/// `channels` (matching the session), so captured samples are layout-matched to
/// the track.
///
/// - `engine` — valid engine handle owning the input device.
/// - `mixer` — valid mixer handle that owns the track.
/// - `track` — valid track handle whose source is installed.
/// - `device_name` — null-terminated UTF-8 device name, or null for the default.
/// - `sample_rate` — capture sample rate in Hz.
/// - `channels` — capture (and track) channel count.
/// - `buffer_frames` — device buffer size in frames; `0` lets the engine choose.
/// - `out_input` — receives the input-source control handle on success.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success. Destroy the returned
/// handle with `ownaudio_v1_input_source_destroy` after the track's source has
/// been cleared or the track removed.
#[no_mangle]
pub extern "C" fn ownaudio_v1_track_open_input(
    engine: *mut OwnAudioEngineHandle,
    mixer: *mut OwnAudioMixerHandle,
    track: *mut OwnAudioTrackHandle,
    device_name: *const c_char,
    sample_rate: u32,
    channels: u16,
    buffer_frames: u32,
    out_input: *mut *mut OwnAudioInputSourceHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if engine.is_null() || mixer.is_null() || track.is_null() || out_input.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let track_wrapper = match unsafe { track_from_ptr(track) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };
        let mixer_wrapper = match unsafe { mixer_from_ptr(mixer) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };
        let engine_wrapper = match unsafe { engine_from_ptr(engine) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        let track_id = track_wrapper.id;
        let ch = channels.max(1) as usize;

        // Lock-free bridge: the capture callback writes into `writer`, the mixer
        // audio thread reads `reader` (installed as the track's source).
        let capacity = ((sample_rate.max(1) as f32) * (ch as f32) * INPUT_RING_SECONDS) as usize;
        let (mut writer, reader) = ring_buffer(capacity.max(1));

        let peaks = Arc::new(InputPeaks {
            left: std::sync::atomic::AtomicU32::new(0.0f32.to_bits()),
            right: std::sync::atomic::AtomicU32::new(0.0f32.to_bits()),
        });

        let config = StreamConfig {
            sample_rate,
            channels,
            sample_format: SampleFormat::F32,
            buffer_size_frames: if buffer_frames == 0 {
                None
            } else {
                Some(buffer_frames)
            },
        };

        let device_info = parse_device_name(device_name);

        // The capture callback runs on the device audio thread: it measures the
        // capture peaks and pushes the samples into the ring (dropping any overflow
        // — non-blocking and allocation-free). This is the only place audio data
        // flows, and it never touches managed memory.
        let peaks_cb = Arc::clone(&peaks);
        let stream_result = engine_wrapper.inner.open_input_stream(
            device_info.as_ref(),
            &config,
            move |data: &[f32]| {
                let mut peak_l = 0.0f32;
                let mut peak_r = 0.0f32;
                let mut i = 0;
                while i < data.len() {
                    let l = data[i].abs();
                    if l > peak_l {
                        peak_l = l;
                    }
                    if ch > 1 && i + 1 < data.len() {
                        let r = data[i + 1].abs();
                        if r > peak_r {
                            peak_r = r;
                        }
                    }
                    i += ch;
                }
                if ch == 1 {
                    peak_r = peak_l;
                }
                peaks_cb.left.store(peak_l.to_bits(), Ordering::Relaxed);
                peaks_cb.right.store(peak_r.to_bits(), Ordering::Relaxed);

                writer.write(data);
            },
        );

        let stream = match stream_result {
            Ok(s) => s,
            Err(e) => {
                set_last_error(e.to_string());
                return OwnAudioErrorCode::from(e) as i32;
            }
        };

        // Install the reader as the track's source only after capture opened, so a
        // failed open never leaves a silent orphan source on the track.
        if mixer_wrapper
            .controller
            .set_track_source(track_id, Some(Box::new(reader)))
            .is_err()
        {
            set_last_error("mixer command queue is full; input source not set");
            return OwnAudioErrorCode::InternalError as i32;
        }

        let boxed = Box::new(InputSourceWrapper { stream, peaks });
        unsafe {
            *out_input = Box::into_raw(boxed) as *mut OwnAudioInputSourceHandle;
        }

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Starts (or resumes) device capture feeding the track.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_input_source_play(input: *mut OwnAudioInputSourceHandle) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { input_source_from_ptr(input) } {
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

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Pauses device capture. Buffered samples already in the ring keep playing out.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_input_source_pause(input: *mut OwnAudioInputSourceHandle) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { input_source_from_ptr(input) } {
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

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Writes the most recent capture peak levels (0.0..) to `*out_left` / `*out_right`.
///
/// - `input` — valid handle from `ownaudio_v1_track_open_input`.
/// - `out_left` / `out_right` — receive the peaks on success.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_input_source_get_peaks(
    input: *mut OwnAudioInputSourceHandle,
    out_left: *mut f32,
    out_right: *mut f32,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_left.is_null() || out_right.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let wrapper = match unsafe { input_source_from_ptr(input) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        let l = f32::from_bits(wrapper.peaks.left.load(Ordering::Relaxed));
        let r = f32::from_bits(wrapper.peaks.right.load(Ordering::Relaxed));
        unsafe {
            *out_left = l;
            *out_right = r;
        }

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Destroys an input-source control handle, stopping capture and releasing the
/// input stream.
///
/// Passing `null` is safe and has no effect. The track's ring-buffer reader lives
/// on the audio thread until the track's source is cleared or the track is
/// removed; after this call it simply underruns (renders silence).
#[no_mangle]
pub extern "C" fn ownaudio_v1_input_source_destroy(input: *mut OwnAudioInputSourceHandle) {
    let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if input.is_null() {
            return;
        }
        unsafe {
            drop(Box::from_raw(input as *mut InputSourceWrapper));
        }
    }));
}
