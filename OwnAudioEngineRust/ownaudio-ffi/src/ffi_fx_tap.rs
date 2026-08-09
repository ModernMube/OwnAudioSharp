//! FFI exports for the pre/post effect-chain tap.
//!
//! A track's chain and the master chain get their own export trio, matching how
//! `ownaudio_v1_track_add_effect` and `ownaudio_v1_mixer_add_master_effect`
//! already split.

use ownaudio_core::multitrack::MASTER_EFFECT_TARGET;

use crate::error_code::{set_last_error, OwnAudioErrorCode};
use crate::handles::{
    mixer_from_ptr, track_from_ptr, MixerWrapper, OwnAudioMixerHandle, OwnAudioTrackHandle,
};

/// Installs a tap on `track_id`'s chain and keeps its reader on the wrapper.
fn start(wrapper: &mut MixerWrapper, track_id: u64, capacity_samples: usize) -> i32 {
    match wrapper.controller.start_fx_tap(track_id, capacity_samples) {
        Ok(Some(reader)) => {
            wrapper.fx_tap_readers.retain(|(id, _)| *id != track_id);
            wrapper.fx_tap_readers.push((track_id, reader));
            OwnAudioErrorCode::Success as i32
        }
        Ok(None) => {
            set_last_error("no such track; effect tap not started");
            OwnAudioErrorCode::InvalidHandle as i32
        }
        Err(_) => {
            set_last_error("mixer command queue is full; effect tap not started");
            OwnAudioErrorCode::InternalError as i32
        }
    }
}

/// Drains one chunk of `track_id`'s tap into the two destinations.
///
/// # Safety
/// `pre_out` and `post_out` must each be valid for `len` writable `f32` values,
/// and `out_read` must point to a writable `usize`.
unsafe fn read(
    wrapper: &mut MixerWrapper,
    track_id: u64,
    pre_out: *mut f32,
    post_out: *mut f32,
    len: usize,
    out_read: *mut usize,
) -> i32 {
    if pre_out.is_null() || post_out.is_null() || out_read.is_null() {
        return OwnAudioErrorCode::NullPointer as i32;
    }

    let reader = wrapper
        .fx_tap_readers
        .iter_mut()
        .find(|(id, _)| *id == track_id);

    let read = match reader {
        Some((_, reader)) if len > 0 => {
            let pre = unsafe { std::slice::from_raw_parts_mut(pre_out, len) };
            let post = unsafe { std::slice::from_raw_parts_mut(post_out, len) };
            reader.read(pre, post)
        }
        _ => 0,
    };

    unsafe {
        *out_read = read;
    }

    OwnAudioErrorCode::Success as i32
}

/// Drops the reader and tells the audio thread to stop mirroring.
fn stop(wrapper: &mut MixerWrapper, track_id: u64) -> i32 {
    wrapper.fx_tap_readers.retain(|(id, _)| *id != track_id);
    match wrapper.controller.stop_fx_tap(track_id) {
        Ok(()) => OwnAudioErrorCode::Success as i32,
        Err(_) => {
            set_last_error("mixer command queue is full; effect tap stop deferred");
            OwnAudioErrorCode::InternalError as i32
        }
    }
}

/// Starts tapping the audio on both sides of a track's effect chain, so the
/// caller can compare what the chain was given with what it produced.
///
/// The audio thread mirrors each rendered block into a ring right before the
/// chain runs and again right after; drain both with
/// [`ownaudio_v1_track_fx_tap_read`]. A slow drain never blocks rendering —
/// overflow drops whole pre/post pairs, so the two sides stay aligned.
///
/// The tap sits ahead of the track's gain, pan and delay compensation, so a
/// fader move never shows up as something the effects did.
///
/// - `capacity_samples` — ring capacity per side, in interleaved samples. A few
///   render blocks is the right size; an analyser has no use for stale audio.
///
/// Tapping the same track twice replaces the previous tap.
///
/// # Safety
/// - `mixer` must be a live handle from `ownaudio_v1_mixer_create` that has not been destroyed.
/// - `track` must be a live handle from `ownaudio_v1_track_create` that has not been destroyed.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_track_fx_tap_start(
    mixer: *mut OwnAudioMixerHandle,
    track: *mut OwnAudioTrackHandle,
    capacity_samples: usize,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let track_id = match unsafe { track_from_ptr(track) } {
            Some(w) => w.id,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };
        let wrapper = match unsafe { mixer_from_ptr(mixer) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        start(wrapper, track_id, capacity_samples)
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Drains up to `len` tapped samples into `pre_out` and `post_out`, reporting the
/// count that landed in each through `*out_read`.
///
/// Both buffers always receive the same number of samples, so `pre_out[i]` and
/// `post_out[i]` are the same instant of audio. A short read is normal and `0`
/// means the ring is empty or nothing is tapping this track.
///
/// Single-consumer: call from one thread at a time, never next to
/// [`ownaudio_v1_track_fx_tap_stop`].
///
/// # Safety
/// - `mixer` and `track` must be live handles that have not been destroyed.
/// - `pre_out` and `post_out` must each be valid for `len` writable `f32` values.
/// - `out_read` must point to a writable `usize`.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_track_fx_tap_read(
    mixer: *mut OwnAudioMixerHandle,
    track: *mut OwnAudioTrackHandle,
    pre_out: *mut f32,
    post_out: *mut f32,
    len: usize,
    out_read: *mut usize,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let track_id = match unsafe { track_from_ptr(track) } {
            Some(w) => w.id,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };
        let wrapper = match unsafe { mixer_from_ptr(mixer) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        unsafe { read(wrapper, track_id, pre_out, post_out, len, out_read) }
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Stops tapping the track's chain and releases the read sides. Safe to call when
/// nothing is tapping (no-op).
///
/// # Safety
/// - `mixer` and `track` must be live handles that have not been destroyed.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_track_fx_tap_stop(
    mixer: *mut OwnAudioMixerHandle,
    track: *mut OwnAudioTrackHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let track_id = match unsafe { track_from_ptr(track) } {
            Some(w) => w.id,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };
        let wrapper = match unsafe { mixer_from_ptr(mixer) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        stop(wrapper, track_id)
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Same as [`ownaudio_v1_track_fx_tap_start`], but around the master chain, which
/// sees the summed mix. Sits ahead of the master gain and pan.
///
/// # Safety
/// - `mixer` must be a live handle from `ownaudio_v1_mixer_create` that has not been destroyed.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_mixer_master_fx_tap_start(
    mixer: *mut OwnAudioMixerHandle,
    capacity_samples: usize,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        match unsafe { mixer_from_ptr(mixer) } {
            Some(w) => start(w, MASTER_EFFECT_TARGET, capacity_samples),
            None => OwnAudioErrorCode::InvalidHandle as i32,
        }
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Master-chain counterpart of [`ownaudio_v1_track_fx_tap_read`].
///
/// # Safety
/// - `mixer` must be a live handle from `ownaudio_v1_mixer_create` that has not been destroyed.
/// - `pre_out` and `post_out` must each be valid for `len` writable `f32` values.
/// - `out_read` must point to a writable `usize`.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_mixer_master_fx_tap_read(
    mixer: *mut OwnAudioMixerHandle,
    pre_out: *mut f32,
    post_out: *mut f32,
    len: usize,
    out_read: *mut usize,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        match unsafe { mixer_from_ptr(mixer) } {
            Some(w) => unsafe { read(w, MASTER_EFFECT_TARGET, pre_out, post_out, len, out_read) },
            None => OwnAudioErrorCode::InvalidHandle as i32,
        }
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Master-chain counterpart of [`ownaudio_v1_track_fx_tap_stop`].
///
/// # Safety
/// - `mixer` must be a live handle from `ownaudio_v1_mixer_create` that has not been destroyed.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_mixer_master_fx_tap_stop(
    mixer: *mut OwnAudioMixerHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        match unsafe { mixer_from_ptr(mixer) } {
            Some(w) => stop(w, MASTER_EFFECT_TARGET),
            None => OwnAudioErrorCode::InvalidHandle as i32,
        }
    }));

    crate::error_code::finish_catch_unwind(result)
}
