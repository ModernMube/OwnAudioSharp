//! FFI exports for memory-backed track sources.
//!
//! Unlike [`ffi_source`](crate::ffi_source), where the control thread keeps
//! pushing decoded samples into a ring buffer, a memory source takes ownership of
//! a fully-decoded interleaved buffer once: [`ownaudio_v1_track_open_memory`]
//! copies the caller's samples, installs the serving source on the track (through
//! the mixer command queue, so the audio thread takes ownership without a data
//! race), and hands back a control handle.  The control thread then only toggles
//! looping, polls the end-of-stream latch, and requests seeks — it never touches
//! audio data on the render path.

use ownaudio_core::MemoryTrackSource;

use crate::error_code::{set_last_error, OwnAudioErrorCode};
use crate::handles::{
    memory_source_from_ptr, mixer_from_ptr, track_from_ptr, MemorySourceWrapper,
    OwnAudioMemorySourceHandle, OwnAudioMixerHandle, OwnAudioTrackHandle,
};

/// Copies `sample_count` interleaved `f32` samples, installs a memory-serving
/// source on `track`, and writes the control handle to `*out_source`.
///
/// The samples must already be at the session's sample rate and interleaved with
/// `channels` channels; they are copied once into native memory (a control-thread
/// copy, never on the audio path), after which the audio thread owns them and the
/// managed side is only a controller.  Looping and end-of-stream are handled
/// natively; observe and steer them through the returned handle.
///
/// - `mixer` — valid mixer handle that owns the track.
/// - `track` — valid track handle whose source is to be installed.
/// - `samples` — pointer to the first of `sample_count` interleaved `f32` samples.
/// - `sample_count` — number of samples at `samples` (frames × channels).
/// - `channels` — interleaved channel count of the buffer.
/// - `loop_enabled` — non-zero to loop seamlessly at end-of-buffer.
/// - `out_source` — receives the control handle on success.
///
/// The source is installed through the mixer's lock-free command queue, so it
/// becomes the track's source on the next render block; any previous source is
/// retired off the audio thread.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.  Destroy the returned
/// handle with `ownaudio_v1_memory_source_destroy` after the track's source has
/// been cleared or the track removed.
#[no_mangle]
pub extern "C" fn ownaudio_v1_track_open_memory(
    mixer: *mut OwnAudioMixerHandle,
    track: *mut OwnAudioTrackHandle,
    samples: *const f32,
    sample_count: usize,
    channels: u32,
    loop_enabled: u8,
    out_source: *mut *mut OwnAudioMemorySourceHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if mixer.is_null() || track.is_null() || out_source.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        if sample_count != 0 && samples.is_null() {
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

        let track_id = track_wrapper.id;

        // Copy the caller's samples into an owned Vec once (control thread), so the
        // audio thread never dereferences managed memory.
        let buffer: Vec<f32> = if sample_count == 0 {
            Vec::new()
        } else {
            // SAFETY: the caller guarantees `samples` points to `sample_count`
            // valid `f32` values for the duration of the call.
            unsafe { std::slice::from_raw_parts(samples, sample_count) }.to_vec()
        };

        let (source, control) = MemoryTrackSource::new(buffer, channels);
        control.set_loop(loop_enabled != 0);

        // Hand the serving source to the audio thread via the command queue; it
        // becomes the track's source on the next render block (the old one is
        // retired off the real-time path).
        if mixer_wrapper
            .controller
            .set_track_source(track_id, Some(Box::new(source)))
            .is_err()
        {
            set_last_error("mixer command queue is full; memory source not set");
            return OwnAudioErrorCode::InternalError as i32;
        }

        let boxed = Box::new(MemorySourceWrapper { control });
        unsafe {
            *out_source = Box::into_raw(boxed) as *mut OwnAudioMemorySourceHandle;
        }

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Enables or disables seamless looping for a memory source.
///
/// - `source` — valid handle from `ownaudio_v1_track_open_memory`.
/// - `enabled` — non-zero to loop, zero to stop at end-of-buffer.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_memory_source_set_loop(
    source: *mut OwnAudioMemorySourceHandle,
    enabled: u8,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { memory_source_from_ptr(source) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        wrapper.control.set_loop(enabled != 0);

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Writes whether the memory source has reached end-of-buffer (without looping) to
/// `*out_finished` (`1` = finished, `0` = still playing or looping).
///
/// - `source` — valid handle from `ownaudio_v1_track_open_memory`.
/// - `out_finished` — receives the finished flag on success.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_memory_source_is_finished(
    source: *mut OwnAudioMemorySourceHandle,
    out_finished: *mut u8,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_finished.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let wrapper = match unsafe { memory_source_from_ptr(source) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        unsafe {
            *out_finished = u8::from(wrapper.control.is_finished());
        }

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Requests a seek to `frame_position` (output frames) on a memory source.
///
/// Non-blocking: the audio thread applies the reposition on its next read and the
/// finished latch clears once audio flows from the new position.
///
/// - `source` — valid handle from `ownaudio_v1_track_open_memory`.
/// - `frame_position` — target position in output sample frames.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_memory_source_seek(
    source: *mut OwnAudioMemorySourceHandle,
    frame_position: u64,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { memory_source_from_ptr(source) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        wrapper.control.seek_frames(frame_position);

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Destroys a memory-source control handle.
///
/// Passing `null` is safe and has no effect.  Dropping this handle only releases
/// the control block; the serving source itself lives on the audio thread until
/// the track's source is cleared (`ownaudio_v1_track_clear_source`) or the track
/// is removed, at which point the source (and its buffer) is retired off the
/// real-time path.
#[no_mangle]
pub extern "C" fn ownaudio_v1_memory_source_destroy(source: *mut OwnAudioMemorySourceHandle) {
    let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if source.is_null() {
            return;
        }
        unsafe {
            drop(Box::from_raw(source as *mut MemorySourceWrapper));
        }
    }));
}
