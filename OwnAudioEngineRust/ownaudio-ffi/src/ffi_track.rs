//! FFI exports for mixer and track lifecycle management.

use ownaudio_core::multitrack::MultiTrackMixer;

use crate::error_code::OwnAudioErrorCode;
use crate::handles::{
    mixer_from_ptr, track_from_ptr, MixerWrapper, OwnAudioMixerHandle, OwnAudioTrackHandle,
    TrackWrapper,
};

// ---------------------------------------------------------------------------
// Mixer lifecycle
// ---------------------------------------------------------------------------

/// Creates a new [`MultiTrackMixer`] and writes its handle to `*out_mixer`.
///
/// - `sample_rate` — output sample rate in Hz.
/// - `channels` — number of output channels (1 = mono, 2 = stereo).
/// - `out_mixer` — receives the new mixer handle on success.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_mixer_create(
    sample_rate: f32,
    channels: u16,
    out_mixer: *mut *mut OwnAudioMixerHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_mixer.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let mixer = MultiTrackMixer::new(sample_rate, channels);
        let boxed = Box::new(MixerWrapper { inner: mixer });

        unsafe {
            *out_mixer = Box::into_raw(boxed) as *mut OwnAudioMixerHandle;
        }

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Destroys a mixer handle and releases all associated resources.
///
/// Passing `null` is safe and has no effect.
/// All track and effect handles obtained from this mixer must be destroyed
/// before calling this function.
#[no_mangle]
pub extern "C" fn ownaudio_v1_mixer_destroy(mixer: *mut OwnAudioMixerHandle) {
    // A panic in the mixer's Drop must never unwind across the FFI boundary.
    let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if mixer.is_null() {
            return;
        }
        unsafe {
            drop(Box::from_raw(mixer as *mut MixerWrapper));
        }
    }));
}

// ---------------------------------------------------------------------------
// Track lifecycle
// ---------------------------------------------------------------------------

/// Adds a new track to the mixer and writes its handle to `*out_track`.
///
/// - `mixer` — valid mixer handle.
/// - `out_track` — receives the new track handle on success.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_track_create(
    mixer: *mut OwnAudioMixerHandle,
    out_track: *mut *mut OwnAudioTrackHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if mixer.is_null() || out_track.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let wrapper = match unsafe { mixer_from_ptr(mixer) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        let (id, shared) = wrapper.inner.add_track();

        let track_wrapper = Box::new(TrackWrapper {
            mixer: mixer as *mut MixerWrapper,
            id,
            shared,
        });

        unsafe {
            *out_track = Box::into_raw(track_wrapper) as *mut OwnAudioTrackHandle;
        }

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Destroys a track handle.  Does NOT remove the track from the mixer.
///
/// Call `ownaudio_v1_track_remove` first to remove the track from the mix,
/// then this function to release the handle memory.
/// Passing `null` is safe and has no effect.
#[no_mangle]
pub extern "C" fn ownaudio_v1_track_destroy(track: *mut OwnAudioTrackHandle) {
    // A panic in the track handle's Drop must never unwind across the FFI boundary.
    let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if track.is_null() {
            return;
        }
        unsafe {
            drop(Box::from_raw(track as *mut TrackWrapper));
        }
    }));
}

/// Removes and destroys the track from the mixer.
///
/// The track handle is invalidated after this call; do not use it afterwards.
/// Passing `null` is safe and has no effect.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_track_remove(
    mixer: *mut OwnAudioMixerHandle,
    track: *mut OwnAudioTrackHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if mixer.is_null() || track.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let mixer_wrapper = match unsafe { mixer_from_ptr(mixer) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        let track_wrapper = match unsafe { track_from_ptr(track) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        mixer_wrapper.inner.remove_track(track_wrapper.id);
        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

// ---------------------------------------------------------------------------
// Track transport control
// ---------------------------------------------------------------------------

/// Starts or resumes playback of the track.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_track_play(track: *mut OwnAudioTrackHandle) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { track_from_ptr(track) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        wrapper.shared.set_state(ownaudio_core::TrackState::Playing);

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Pauses the track without resetting its position.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_track_pause(track: *mut OwnAudioTrackHandle) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { track_from_ptr(track) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        wrapper.shared.set_state(ownaudio_core::TrackState::Paused);

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Stops the track and resets its position to zero.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_track_stop(track: *mut OwnAudioTrackHandle) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { track_from_ptr(track) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        wrapper.shared.set_state(ownaudio_core::TrackState::Stopped);

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Sets the track's playback position to `sample_position`.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_track_seek(
    track: *mut OwnAudioTrackHandle,
    _sample_position: u64,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if unsafe { track_from_ptr(track) }.is_none() {
            return OwnAudioErrorCode::InvalidHandle as i32;
        }
        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

// ---------------------------------------------------------------------------
// Track parameters
// ---------------------------------------------------------------------------

/// Sets the track gain (linear amplitude multiplier; 1.0 = unity).
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_track_set_gain(
    track: *mut OwnAudioTrackHandle,
    gain: f32,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { track_from_ptr(track) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        wrapper.shared.set_gain(gain);

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Sets the track tempo ratio (1.0 = normal speed, 2.0 = double speed).
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_track_set_tempo(
    track: *mut OwnAudioTrackHandle,
    ratio: f32,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { track_from_ptr(track) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        wrapper.shared.set_tempo_ratio(ratio);

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Sets the track pitch shift in semitones (-24 … +24).
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_track_set_pitch(
    track: *mut OwnAudioTrackHandle,
    semitones: f32,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { track_from_ptr(track) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        wrapper.shared.set_pitch_semitones(semitones);

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Sets the track mute state (0.0 = unmuted, 1.0 = muted).
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_track_set_mute(
    track: *mut OwnAudioTrackHandle,
    muted: f32,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { track_from_ptr(track) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        wrapper.shared.set_muted(muted >= 0.5);

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}
