//! FFI exports for mixer and track lifecycle management.

use ownaudio_core::multitrack::{command_channel, MultiTrackMixer};

use crate::error_code::{set_last_error, OwnAudioErrorCode};
use crate::handles::{
    mixer_from_ptr, track_from_ptr, MixerWrapper, OwnAudioMixerHandle, OwnAudioTrackHandle,
    TrackWrapper,
};

/// Capacity of the control→audio command ring buffer (and its retirement
/// return queue).  Comfortably exceeds the mixer's [`MAX_TRACKS`] so a burst of
/// structural changes never overflows before the audio thread drains them.
///
/// [`MAX_TRACKS`]: ownaudio_core::multitrack::MAX_TRACKS
const COMMAND_QUEUE_CAPACITY: usize = 1024;

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

        let mut mixer = MultiTrackMixer::new(sample_rate, channels);
        let (controller, receiver) =
            command_channel(COMMAND_QUEUE_CAPACITY, mixer.sample_rate(), mixer.channels(), mixer.max_buffer_size());
        mixer.attach_command_receiver(receiver);
        let master_shared = mixer.master_shared();

        let boxed = Box::new(MixerWrapper {
            controller,
            mixer: Some(mixer),
            master_shared,
            sample_rate,
            channels,
        });

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

/// Starts every track in the mixer in a single call, against the shared clock.
///
/// Sets all tracks to the playing state from the control thread in one
/// operation, so they begin on the same audio callback — a sample-accurate
/// start that avoids the per-track P/Invoke round-trips and the synchronisation
/// drift they would introduce.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_mixer_play_all(mixer: *mut OwnAudioMixerHandle) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { mixer_from_ptr(mixer) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        wrapper.controller.play_all();

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Pauses every track in the mixer in a single call, against the shared clock.
///
/// Sets all tracks to the paused state from the control thread in one
/// operation, so they pause on the same audio callback — avoiding the per-track
/// P/Invoke round-trips and the synchronisation drift they would introduce.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_mixer_pause_all(mixer: *mut OwnAudioMixerHandle) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { mixer_from_ptr(mixer) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        wrapper.controller.pause_all();

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Stops every track in the mixer in a single call, against the shared clock.
///
/// Sets all tracks to the stopped state from the control thread in one
/// operation, so they stop on the same audio callback — avoiding the per-track
/// P/Invoke round-trips and the synchronisation drift they would introduce.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_mixer_stop_all(mixer: *mut OwnAudioMixerHandle) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { mixer_from_ptr(mixer) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        wrapper.controller.stop_all();

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Sets the mixer's master output gain (linear amplitude multiplier applied once
/// over the fully summed mix; 1.0 = unity, 0.0 = silence).
///
/// The gain is ramped on the audio thread, so a live change fades in over a few
/// milliseconds instead of clicking. Keeps working after the mixer has been moved
/// onto the audio thread by an output stream (the master block is shared).
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_mixer_set_master_gain(
    mixer: *mut OwnAudioMixerHandle,
    gain: f32,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { mixer_from_ptr(mixer) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        wrapper.master_shared.set_master_gain(gain);

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Writes the mixer's most recently measured master output peak levels (absolute,
/// post master gain) to `*out_left` and `*out_right`.
///
/// The peaks are updated by the audio thread every rendered block; a mono mixer
/// reports the same value on both channels. Values are `0.0` for silence and near
/// `1.0` at full scale (or above when the mix clips).
///
/// - `mixer` — valid mixer handle.
/// - `out_left` / `out_right` — receive the channel peaks on success.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_mixer_get_master_peaks(
    mixer: *mut OwnAudioMixerHandle,
    out_left: *mut f32,
    out_right: *mut f32,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_left.is_null() || out_right.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let wrapper = match unsafe { mixer_from_ptr(mixer) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        unsafe {
            *out_left = wrapper.master_shared.master_peak_l();
            *out_right = wrapper.master_shared.master_peak_r();
        }

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
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

        let (id, shared) = match wrapper.controller.add_track() {
            Ok(pair) => pair,
            Err(_) => {
                set_last_error("mixer command queue is full; track not added");
                return OwnAudioErrorCode::InternalError as i32;
            }
        };

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

        match mixer_wrapper.controller.remove_track(track_wrapper.id) {
            Ok(_) => OwnAudioErrorCode::Success as i32,
            Err(_) => {
                set_last_error("mixer command queue is full; track not removed");
                OwnAudioErrorCode::InternalError as i32
            }
        }
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
        // Drop any audio buffered in the stretch FIFO so a later restart begins cleanly
        // instead of replaying the pre-stop tail (consumed on the next rendered block).
        wrapper.shared.request_stretch_flush();

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
// Track position
// ---------------------------------------------------------------------------

/// Writes the number of output frames the track has rendered since the last
/// position reset to `*out_frames`.
///
/// This is the track's authoritative *rendered* playback position: it is
/// advanced on the audio thread by the mixer as each block is produced, and lags
/// the *fed* position by the ring-buffer depth. Divide by the sample rate to get
/// the position in seconds.
///
/// - `track` — valid track handle.
/// - `out_frames` — receives the rendered frame count on success.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_track_get_rendered_frames(
    track: *mut OwnAudioTrackHandle,
    out_frames: *mut u64,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_frames.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let wrapper = match unsafe { track_from_ptr(track) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        unsafe {
            *out_frames = wrapper.shared.rendered_frames();
        }

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Resets the track's rendered-frame position counter to zero.
///
/// Call this from the control thread after seeking the track's source (in the
/// intermediate phase the decoder seek happens on the C# side), so the rendered
/// position restarts from the seek target.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_track_reset_position(track: *mut OwnAudioTrackHandle) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { track_from_ptr(track) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        wrapper.shared.reset_rendered_frames();
        wrapper.shared.request_stretch_flush();

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Writes the track's most recently measured output peak levels (absolute, of the
/// track's own post-effect, post-gain contribution) to `*out_left` and `*out_right`.
///
/// Updated by the audio thread every block the track is rendered; a mono track
/// reports the same value on both channels. A track that is not currently playing
/// keeps its last measured value, so callers that want a decaying meter should
/// gate the read on the track's transport state.
///
/// - `track` — valid track handle.
/// - `out_left` / `out_right` — receive the channel peaks on success.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_track_get_peaks(
    track: *mut OwnAudioTrackHandle,
    out_left: *mut f32,
    out_right: *mut f32,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_left.is_null() || out_right.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let wrapper = match unsafe { track_from_ptr(track) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        unsafe {
            *out_left = wrapper.shared.peak_l();
            *out_right = wrapper.shared.peak_r();
        }

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Sets the track's start-offset silence: the number of output frames the track
/// emits as silence — without reading its source — before it begins contributing.
///
/// Realises a positive per-track start offset (the track enters later on the shared
/// clock, sample-accurately) without touching the source position. Pass `0` to clear
/// any pending delay. The control side pairs this with a source seek to place the
/// content, matching the managed `content = clock − start_offset` behaviour.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_track_set_start_delay_frames(
    track: *mut OwnAudioTrackHandle,
    frames: u64,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { track_from_ptr(track) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        wrapper.shared.request_start_silence(frames);

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
