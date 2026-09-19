//! FFI exports for group track sources.
//!
//! A group lays several audio files out on one content timeline and plays them
//! through a single track, so they share its stretch stage, effect chain, gain,
//! pan and routing. Files are loaded once with [`ownaudio_v1_group_clip_open`],
//! independent of any track. [`ownaudio_v1_track_open_group`] installs an empty
//! group on a track and hands back a control handle; loaded clips are then
//! placed, moved and removed through that handle while the track plays or not.

use std::ffi::CStr;
use std::os::raw::c_char;

use ownaudio_core::{GroupClipData, GroupTrackSource};

use crate::error_code::{set_last_error, OwnAudioErrorCode};
use crate::handles::{
    group_clip_from_ptr, group_source_from_ptr, mixer_from_ptr, track_from_ptr, GroupSourceWrapper,
    OwnAudioGroupClipHandle, OwnAudioGroupSourceHandle, OwnAudioMixerHandle, OwnAudioTrackHandle,
};

/// Loads `path` for use on groups and writes the clip handle to `*out_clip`.
///
/// Files no longer than `memory_max_frames` (and files that cannot tell their own
/// length) are decoded into memory inside this call, so call it off the UI
/// thread; longer files are only probed and stream from disk once placed.
///
/// - `path` — null-terminated UTF-8 file path.
/// - `sample_rate` / `channels` — what the clip decodes to; must match the groups
///   it is placed on.
/// - `memory_max_frames` — the memory / stream threshold, in frames.
/// - `out_clip` — receives the clip handle on success.
/// - `out_length_frames` — receives the clip length in frames.
/// - `out_in_memory` — receives `1` for a memory clip, `0` for a streamed one.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success, a decoder error code when
/// the file cannot be opened.  Destroy the handle with
/// `ownaudio_v1_group_clip_destroy`; groups it was placed on keep playing it.
///
/// # Safety
/// - `path` must be a NUL-terminated UTF-8 string.
/// - The three out pointers must each point to writable storage of their type.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_group_clip_open(
    path: *const c_char,
    sample_rate: u32,
    channels: u32,
    memory_max_frames: u64,
    out_clip: *mut *mut OwnAudioGroupClipHandle,
    out_length_frames: *mut u64,
    out_in_memory: *mut u8,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if path.is_null()
            || out_clip.is_null()
            || out_length_frames.is_null()
            || out_in_memory.is_null()
        {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let path_str = match unsafe { CStr::from_ptr(path) }.to_str() {
            Ok(s) => s,
            Err(_) => {
                set_last_error("group clip path is not valid UTF-8");
                return OwnAudioErrorCode::DecoderOpenFailed as i32;
            }
        };

        match GroupClipData::open(path_str, sample_rate, channels, memory_max_frames) {
            Ok(data) => {
                unsafe {
                    *out_length_frames = data.length_frames();
                    *out_in_memory = u8::from(data.in_memory());
                    *out_clip = Box::into_raw(Box::new(data)) as *mut OwnAudioGroupClipHandle;
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

/// Destroys a clip handle.  Passing `null` is safe and has no effect.  Groups the
/// clip was placed on keep what they need to go on playing it.
///
/// # Safety
/// - `clip` must be a live handle from `ownaudio_v1_group_clip_open` that has not been destroyed.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_group_clip_destroy(clip: *mut OwnAudioGroupClipHandle) {
    let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if clip.is_null() {
            return;
        }
        unsafe {
            drop(Box::from_raw(clip as *mut GroupClipData));
        }
    }));
}

/// Installs an empty group source on `track` and writes its control handle to
/// `*out_source`.
///
/// - `mixer` — valid mixer handle that owns the track.
/// - `track` — valid track handle whose source is to be installed.
/// - `sample_rate` — rate every placed clip decodes to; pass the mixer's rate.
/// - `channels` — interleaved width every placed clip decodes to.
/// - `out_source` — receives the control handle on success.
///
/// The source is installed through the mixer's lock-free command queue, so it
/// becomes the track's source on the next render block; any previous source is
/// retired off the audio thread.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.  Destroy the returned
/// handle with `ownaudio_v1_group_source_destroy` after the track's source has
/// been cleared or the track removed.
///
/// # Safety
/// - `mixer` must be a live handle from `ownaudio_v1_mixer_create` that has not been destroyed.
/// - `track` must be a live handle from `ownaudio_v1_track_create` that has not been destroyed.
/// - `out_source` must point to a writable pointer slot; it receives the new handle.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_track_open_group(
    mixer: *mut OwnAudioMixerHandle,
    track: *mut OwnAudioTrackHandle,
    sample_rate: u32,
    channels: u32,
    out_source: *mut *mut OwnAudioGroupSourceHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if mixer.is_null() || track.is_null() || out_source.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }
        if sample_rate == 0 || channels == 0 {
            set_last_error("a group needs a sample rate and a channel count");
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

        let track_id = track_wrapper.id;
        let (source, control) = GroupTrackSource::new(sample_rate, channels);

        track_wrapper
            .shared
            .set_source_channels(channels.min(u16::MAX as u32) as u16);

        if mixer_wrapper
            .controller
            .set_track_source(track_id, Some(Box::new(source)))
            .is_err()
        {
            set_last_error("mixer command queue is full; group source not set");
            return OwnAudioErrorCode::InternalError as i32;
        }

        let boxed = Box::new(GroupSourceWrapper { control });
        unsafe {
            *out_source = Box::into_raw(boxed) as *mut OwnAudioGroupSourceHandle;
        }

        OwnAudioErrorCode::Success as i32
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Places a loaded clip on the group's timeline at `start_frame`, heard from the
/// next render block on.  A memory clip only shares its buffer; a streamed one
/// opens its file (and prefetch thread) here.
///
/// - `source` — valid handle from `ownaudio_v1_track_open_group`.
/// - `clip` — valid handle from `ownaudio_v1_group_clip_open`, decoded at the
///   group's rate and width.
/// - `start_frame` — clip start on the group's content timeline, in frames.
/// - `out_id` — receives the placement id used to move and remove it.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success, `UnsupportedConfig` when
/// the clip's format differs from the group's or the group is full, a decoder
/// error code when a streamed clip's file cannot be reopened.
///
/// # Safety
/// - `source` must be a live handle from `ownaudio_v1_track_open_group` that has not been destroyed.
/// - `clip` must be a live handle from `ownaudio_v1_group_clip_open` that has not been destroyed.
/// - `out_id` must point to a writable `u64`.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_group_source_add_clip(
    source: *mut OwnAudioGroupSourceHandle,
    clip: *mut OwnAudioGroupClipHandle,
    start_frame: u64,
    out_id: *mut u64,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_id.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let wrapper = match unsafe { group_source_from_ptr(source) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        let data = match unsafe { group_clip_from_ptr(clip) } {
            Some(d) => d,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        match wrapper.control.add_clip(data, start_frame) {
            Ok(id) => {
                unsafe {
                    *out_id = id;
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

/// Takes a clip off the group's timeline.  `*out_removed` is `0` when the group
/// holds no clip with that id.
///
/// # Safety
/// - `source` must be a live handle from `ownaudio_v1_track_open_group` that has not been destroyed.
/// - `out_removed` must point to a writable `u8`.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_group_source_remove_clip(
    source: *mut OwnAudioGroupSourceHandle,
    clip_id: u64,
    out_removed: *mut u8,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_removed.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let wrapper = match unsafe { group_source_from_ptr(source) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        let removed = wrapper.control.remove_clip(clip_id);
        unsafe {
            *out_removed = u8::from(removed);
        }

        OwnAudioErrorCode::Success as i32
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Moves a clip to `start_frame` on the group's timeline, effective from the next
/// render block.  `*out_found` is `0` when the group holds no clip with that id.
///
/// # Safety
/// - `source` must be a live handle from `ownaudio_v1_track_open_group` that has not been destroyed.
/// - `out_found` must point to a writable `u8`.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_group_source_set_clip_start(
    source: *mut OwnAudioGroupSourceHandle,
    clip_id: u64,
    start_frame: u64,
    out_found: *mut u8,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_found.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let wrapper = match unsafe { group_source_from_ptr(source) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        let found = wrapper.control.set_clip_start(clip_id, start_frame);
        unsafe {
            *out_found = u8::from(found);
        }

        OwnAudioErrorCode::Success as i32
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Requests a jump of the group's content cursor to `frame_position`.
///
/// Non-blocking: the audio thread applies it on its next read, re-aims every
/// streamed clip and clears the finished latch.
///
/// # Safety
/// - `source` must be a live handle from `ownaudio_v1_track_open_group` that has not been destroyed.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_group_source_seek(
    source: *mut OwnAudioGroupSourceHandle,
    frame_position: u64,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { group_source_from_ptr(source) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        wrapper.control.seek_frames(frame_position);

        OwnAudioErrorCode::Success as i32
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Writes `1` to `*out_finished` once the group's cursor has run past the end of
/// its last clip, `0` otherwise.  Cleared by a seek, and by a clip added or moved
/// ahead of the cursor.
///
/// # Safety
/// - `source` must be a live handle from `ownaudio_v1_track_open_group` that has not been destroyed.
/// - `out_finished` must point to a writable `u8`.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_group_source_is_finished(
    source: *mut OwnAudioGroupSourceHandle,
    out_finished: *mut u8,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_finished.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let wrapper = match unsafe { group_source_from_ptr(source) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        unsafe {
            *out_finished = u8::from(wrapper.control.is_finished());
        }

        OwnAudioErrorCode::Success as i32
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Writes the frame just past the end of the group's last clip to `*out_frames`,
/// zero for an empty group.
///
/// # Safety
/// - `source` must be a live handle from `ownaudio_v1_track_open_group` that has not been destroyed.
/// - `out_frames` must point to a writable `u64`.
/// - Null pointers are rejected with an error code rather than dereferenced.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_group_source_get_end_frame(
    source: *mut OwnAudioGroupSourceHandle,
    out_frames: *mut u64,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_frames.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let wrapper = match unsafe { group_source_from_ptr(source) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        unsafe {
            *out_frames = wrapper.control.end_frame();
        }

        OwnAudioErrorCode::Success as i32
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Destroys a group-source control handle.
///
/// Passing `null` is safe and has no effect.  Dropping this handle only releases
/// the control block; the group and its clips live on the audio thread until the
/// track's source is cleared or the track is removed, at which point they are
/// retired off the real-time path.
///
/// # Safety
/// - `source` must be a live handle from `ownaudio_v1_track_open_group` that has not been destroyed.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_group_source_destroy(source: *mut OwnAudioGroupSourceHandle) {
    let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if source.is_null() {
            return;
        }
        unsafe {
            drop(Box::from_raw(source as *mut GroupSourceWrapper));
        }
    }));
}
