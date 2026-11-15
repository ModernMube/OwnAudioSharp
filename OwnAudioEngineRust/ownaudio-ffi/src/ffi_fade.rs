//! FFI exports for the fade ramps: a track's own and the master's. Both run on the audio
//! thread frame by frame, on top of the gain the user set, and say when they got there.

use ownaudio_core::fade::FadeShared;

use crate::error_code::OwnAudioErrorCode;
use crate::handles::{mixer_from_ptr, track_from_ptr, OwnAudioMixerHandle, OwnAudioTrackHandle};

/// Level and done-flag of a ramp into the caller's out pointers.
///
/// # Safety
/// Both pointers are checked for null by the callers and must be writable.
unsafe fn write_state(fade: &FadeShared, out_level: *mut f32, out_done: *mut u8) {
    unsafe {
        *out_level = fade.level();
        *out_done = u8::from(fade.is_done());
    }
}

/// Ramps the track's fade from where it stands to `target` (linear, 0 = silence, 1 = unity)
/// over `frames`, straight in dB; 0 frames jumps. The track gain is not touched.
///
/// # Safety
/// - `track` must be a live handle from `ownaudio_v1_track_create` that has not been destroyed.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_track_fade(
    track: *mut OwnAudioTrackHandle,
    target: f32,
    frames: u32,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let Some(wrapper) = (unsafe { track_from_ptr(track) }) else {
            return OwnAudioErrorCode::InvalidHandle as i32;
        };

        wrapper.shared.fade().request(target, frames);
        OwnAudioErrorCode::Success as i32
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Where the track's fade stands after the last rendered block, and whether the latest
/// request has landed (`out_done` = 1). A track that does not render does not move.
///
/// # Safety
/// - `track` must be a live handle from `ownaudio_v1_track_create` that has not been destroyed.
/// - `out_level` and `out_done` must be writable.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_track_get_fade(
    track: *mut OwnAudioTrackHandle,
    out_level: *mut f32,
    out_done: *mut u8,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_level.is_null() || out_done.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let Some(wrapper) = (unsafe { track_from_ptr(track) }) else {
            return OwnAudioErrorCode::InvalidHandle as i32;
        };

        unsafe { write_state(wrapper.shared.fade(), out_level, out_done) };
        OwnAudioErrorCode::Success as i32
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Same for the master: the fade runs inside the master scope, after the master chain and
/// before the master gain, so a direct out outside the scope keeps playing.
///
/// # Safety
/// - `mixer` must be a live handle from `ownaudio_v1_mixer_create` that has not been destroyed.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_mixer_master_fade(
    mixer: *mut OwnAudioMixerHandle,
    target: f32,
    frames: u32,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let Some(wrapper) = (unsafe { mixer_from_ptr(mixer) }) else {
            return OwnAudioErrorCode::InvalidHandle as i32;
        };

        wrapper.master_shared.master_fade().request(target, frames);
        OwnAudioErrorCode::Success as i32
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// The master fade's level and done-flag, see [`ownaudio_v1_track_get_fade`].
///
/// # Safety
/// - `mixer` must be a live handle from `ownaudio_v1_mixer_create` that has not been destroyed.
/// - `out_level` and `out_done` must be writable.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_mixer_get_master_fade(
    mixer: *mut OwnAudioMixerHandle,
    out_level: *mut f32,
    out_done: *mut u8,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_level.is_null() || out_done.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let Some(wrapper) = (unsafe { mixer_from_ptr(mixer) }) else {
            return OwnAudioErrorCode::InvalidHandle as i32;
        };

        unsafe { write_state(wrapper.master_shared.master_fade(), out_level, out_done) };
        OwnAudioErrorCode::Success as i32
    }));

    crate::error_code::finish_catch_unwind(result)
}
