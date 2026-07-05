//! FFI exports for file-backed track sources.
//!
//! Unlike [`ffi_source`](crate::ffi_source), where the control thread pushes
//! decoded samples into a ring buffer, a file source owns its decoder and a
//! native prefetch thread: [`ownaudio_v1_track_open_file`] opens the file,
//! installs the decoding source on the track (through the mixer command queue,
//! so the audio thread takes ownership without a data race), and hands back a
//! control handle.  The control thread then only toggles looping, polls the
//! end-of-stream latch, and requests seeks — it never touches audio data.

use std::ffi::CStr;
use std::os::raw::c_char;

use ownaudio_core::FileTrackSource;

use crate::error_code::{set_last_error, OwnAudioErrorCode};
use crate::handles::{
    file_source_from_ptr, mixer_from_ptr, track_from_ptr, FileSourceWrapper,
    OwnAudioFileSourceHandle, OwnAudioMixerHandle, OwnAudioTrackHandle,
};

/// Minimum prefetch ring size in frames, used when the caller passes `0`.
const DEFAULT_PREFETCH_FRAMES: usize = 88_200;

/// Opens `path`, installs a decoding source on `track`, and writes the control
/// handle to `*out_source`.
///
/// The file is decoded (and resampled/remixed) to `target_sample_rate` /
/// `target_channels` on a dedicated native prefetch thread, then fed straight
/// into the track on the audio thread — no managed pump is involved.  Looping
/// and end-of-stream are handled natively; observe them through the returned
/// handle.
///
/// - `mixer` — valid mixer handle that owns the track.
/// - `track` — valid track handle whose source is to be installed.
/// - `path` — null-terminated UTF-8 file path.
/// - `target_sample_rate` — desired output sample rate in Hz; `0` keeps source.
/// - `target_channels` — desired output channel count; `0` keeps source.
/// - `prefetch_frames` — ring-buffer capacity in sample frames; `0` uses a
///   sensible default (about two seconds at 44.1 kHz).
/// - `out_source` — receives the control handle on success.
///
/// The source is installed through the mixer's lock-free command queue, so it
/// becomes the track's source on the next render block; any previous source is
/// retired off the audio thread.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.  Destroy the returned
/// handle with `ownaudio_v1_file_source_destroy` after the track's source has
/// been cleared or the track removed.
#[no_mangle]
pub extern "C" fn ownaudio_v1_track_open_file(
    mixer: *mut OwnAudioMixerHandle,
    track: *mut OwnAudioTrackHandle,
    path: *const c_char,
    target_sample_rate: u32,
    target_channels: u32,
    prefetch_frames: usize,
    out_source: *mut *mut OwnAudioFileSourceHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if mixer.is_null() || track.is_null() || path.is_null() || out_source.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let path_str = match unsafe { CStr::from_ptr(path) }.to_str() {
            Ok(s) => s,
            Err(_) => {
                set_last_error("file source path is not valid UTF-8");
                return OwnAudioErrorCode::DecoderOpenFailed as i32;
            }
        };

        let track_wrapper = match unsafe { track_from_ptr(track) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        let mixer_wrapper = match unsafe { mixer_from_ptr(mixer) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        let track_id = track_wrapper.id;

        // File-backed tracks keep the SoundTouch stretch stage continuously primed so a live
        // tempo/pitch change never cold-starts the FIFO (matching the legacy file chain).
        track_wrapper.shared.set_stretch_always_on(true);

        let frames = if prefetch_frames == 0 {
            DEFAULT_PREFETCH_FRAMES
        } else {
            prefetch_frames
        };

        let (source, control) =
            match FileTrackSource::open(path_str, target_sample_rate, target_channels, frames) {
                Ok(pair) => pair,
                Err(e) => {
                    set_last_error(e.to_string());
                    return OwnAudioErrorCode::from(e) as i32;
                }
            };

        // Hand the decoding source to the audio thread via the command queue; it
        // becomes the track's source on the next render block (the old one is
        // retired off the real-time path).
        if mixer_wrapper
            .controller
            .set_track_source(track_id, Some(Box::new(source)))
            .is_err()
        {
            set_last_error("mixer command queue is full; file source not set");
            return OwnAudioErrorCode::InternalError as i32;
        }

        let boxed = Box::new(FileSourceWrapper { control });
        unsafe {
            *out_source = Box::into_raw(boxed) as *mut OwnAudioFileSourceHandle;
        }

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Enables or disables seamless looping for a file source.
///
/// When enabled, reaching end-of-stream rewinds the decoder to the start and
/// keeps playing; when disabled, end-of-stream latches the finished flag.
///
/// - `source` — valid handle from `ownaudio_v1_track_open_file`.
/// - `enabled` — non-zero to loop, zero to stop at end-of-stream.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_file_source_set_loop(
    source: *mut OwnAudioFileSourceHandle,
    enabled: u8,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { file_source_from_ptr(source) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        wrapper.control.set_loop(enabled != 0);

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Writes whether the file source has reached end-of-stream (without looping) to
/// `*out_finished` (`1` = finished, `0` = still playing or looping).
///
/// The flag latches at end-of-stream and clears as soon as audio flows again
/// after a seek.  Poll it from the control thread to raise a completion event.
///
/// - `source` — valid handle from `ownaudio_v1_track_open_file`.
/// - `out_finished` — receives the finished flag on success.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_file_source_is_finished(
    source: *mut OwnAudioFileSourceHandle,
    out_finished: *mut u8,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_finished.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let wrapper = match unsafe { file_source_from_ptr(source) } {
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

/// Requests a seek to `frame_position` (output frames) on a file source.
///
/// Non-blocking: the native prefetch thread repositions the decoder on its next
/// iteration and the finished latch clears once audio flows from the new
/// position.  Divide a time in seconds by nothing — pass the target expressed in
/// output frames (`seconds * sample_rate`).
///
/// - `source` — valid handle from `ownaudio_v1_track_open_file`.
/// - `frame_position` — target position in output sample frames.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_file_source_seek(
    source: *mut OwnAudioFileSourceHandle,
    frame_position: u64,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { file_source_from_ptr(source) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        wrapper.control.seek_frames(frame_position);

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Destroys a file-source control handle.
///
/// Passing `null` is safe and has no effect.  Dropping this handle only releases
/// the control block; the decoding source itself lives on the audio thread until
/// the track's source is cleared (`ownaudio_v1_track_clear_source`) or the track
/// is removed, at which point the source and its prefetch thread are retired off
/// the real-time path.
#[no_mangle]
pub extern "C" fn ownaudio_v1_file_source_destroy(source: *mut OwnAudioFileSourceHandle) {
    let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if source.is_null() {
            return;
        }
        unsafe {
            drop(Box::from_raw(source as *mut FileSourceWrapper));
        }
    }));
}
