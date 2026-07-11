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
        let (controller, receiver) = command_channel(
            COMMAND_QUEUE_CAPACITY,
            mixer.sample_rate(),
            mixer.channels(),
            mixer.max_buffer_size(),
        );
        mixer.attach_command_receiver(receiver);
        let master_shared = mixer.master_shared();

        let boxed = Box::new(MixerWrapper {
            controller,
            mixer: Some(mixer),
            master_shared,
            sample_rate,
            channels,
            capture_reader: None,
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
// Master-output capture (recording)
// ---------------------------------------------------------------------------

/// Starts capturing the mixer's master output into a ring buffer so the control
/// thread can persist the rendered mix (e.g. record to a file).
///
/// The mixer copies every fully rendered master block (post master effects and
/// gain) into the ring; drain it with [`ownaudio_v1_mixer_capture_read`]. A slow
/// drain never blocks rendering — overflow is dropped.
///
/// - `mixer` — valid mixer handle.
/// - `capacity_samples` — ring capacity in interleaved samples (size for a few
///   seconds of headroom, e.g. `sample_rate * channels * seconds`); `0` is
///   treated as `1`.
///
/// Calling this while capture is already active replaces the previous ring.
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_mixer_capture_start(
    mixer: *mut OwnAudioMixerHandle,
    capacity_samples: usize,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { mixer_from_ptr(mixer) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        match wrapper.controller.start_capture(capacity_samples) {
            Ok(reader) => {
                wrapper.capture_reader = Some(reader);
                OwnAudioErrorCode::Success as i32
            }
            Err(_) => {
                set_last_error("mixer command queue is full; capture not started");
                OwnAudioErrorCode::InternalError as i32
            }
        }
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Reads up to `len` captured samples into `out`, returning the number actually
/// read through `*out_read` (fewer than `len` when the ring holds less; `0` when
/// empty or when capture is not active).
///
/// Single-consumer: call only from one thread at a time, and never concurrently
/// with [`ownaudio_v1_mixer_capture_stop`].
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_mixer_capture_read(
    mixer: *mut OwnAudioMixerHandle,
    out: *mut f32,
    len: usize,
    out_read: *mut usize,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out.is_null() || out_read.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let wrapper = match unsafe { mixer_from_ptr(mixer) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        let read = match wrapper.capture_reader.as_mut() {
            Some(reader) if len > 0 => {
                let slice = unsafe { std::slice::from_raw_parts_mut(out, len) };
                reader.read(slice)
            }
            _ => 0,
        };

        unsafe {
            *out_read = read;
        }

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Stops master-output capture and releases the ring's read side. The mixer's
/// writer is cleared on the audio thread through the command queue.
///
/// Safe to call when capture is not active (no-op). Must not run concurrently
/// with [`ownaudio_v1_mixer_capture_read`].
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_mixer_capture_stop(mixer: *mut OwnAudioMixerHandle) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { mixer_from_ptr(mixer) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        wrapper.capture_reader = None;
        match wrapper.controller.stop_capture() {
            Ok(()) => OwnAudioErrorCode::Success as i32,
            Err(_) => {
                set_last_error("mixer command queue is full; capture stop deferred");
                OwnAudioErrorCode::InternalError as i32
            }
        }
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

/// Writes the number of *content* (source-timeline) frames the track has advanced
/// through since the last position reset to `*out_frames`.
///
/// Unlike [`ownaudio_v1_track_get_rendered_frames`], which counts output frames
/// (wall-clock time, tempo-independent), this integrates the per-block tempo
/// (`Σ output_frames × tempo_ratio`), so it tracks the source content actually
/// heard through a live time-stretch. Divide by the sample rate to get the
/// content-time position in seconds — the tempo-aware playback position a file
/// source reports, matching the legacy managed chain.
///
/// - `track` — valid track handle.
/// - `out_frames` — receives the content frame count on success.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_track_get_rendered_content_frames(
    track: *mut OwnAudioTrackHandle,
    out_frames: *mut f64,
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
            *out_frames = wrapper.shared.content_frames();
        }

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Resets the track's rendered-frame position counter to zero.
///
/// Call this from the control thread after seeking the track's source (in the
/// intermediate phase the decoder seek happens on the C# side), so the rendered
/// position restarts from the seek target. Resets the tempo-aware content-frame
/// counter in lock-step so the content-time position also restarts from the seek.
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
        wrapper.shared.reset_content_frames();
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

/// Installs a per-track output-channel routing map: source channel `i` is summed
/// into physical output channel `map[i]` (for `i < len`), and every output channel
/// not named by the map receives no contribution from this track (silence). This is
/// the native counterpart of the managed mixer's selective channel routing, letting
/// a track be placed onto a chosen subset of a multi-channel output bus.
///
/// - `track` — valid track handle.
/// - `map` — pointer to `len` zero-based output-channel indices, or null when
///   `len` is 0 (which clears any routing).
/// - `len` — number of source channels the map covers. Entries beyond the mixer's
///   channel count are ignored at render time.
///
/// Passing `len == 0` clears any routing, returning the track to the straight
/// identity mix. Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_track_set_output_channel_map(
    track: *mut OwnAudioTrackHandle,
    map: *const u32,
    len: usize,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { track_from_ptr(track) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        if len == 0 {
            wrapper.shared.clear_output_channel_map();
            return OwnAudioErrorCode::Success as i32;
        }

        if map.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let slice = unsafe { std::slice::from_raw_parts(map, len) };
        wrapper.shared.set_output_channel_map(slice);

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Clears any per-track output-channel routing installed by
/// `ownaudio_v1_track_set_output_channel_map`, returning the track to the straight
/// identity mix (source channel `i` → output channel `i`).
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_track_clear_output_channel_map(
    track: *mut OwnAudioTrackHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { track_from_ptr(track) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        wrapper.shared.clear_output_channel_map();

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
pub extern "C" fn ownaudio_v1_track_set_gain(track: *mut OwnAudioTrackHandle, gain: f32) -> i32 {
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
pub extern "C" fn ownaudio_v1_track_set_tempo(track: *mut OwnAudioTrackHandle, ratio: f32) -> i32 {
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

/// Pins the track's SoundTouch time-stretch stage on for its whole lifetime (nonzero = on,
/// zero = off). A pinned track routes through the stretch stage from the first block — even at
/// unity tempo/pitch — and is never released back to the zero-latency bypass path, so the very
/// first tempo/pitch change lands on a warm FIFO with a constant, plugin-delay-compensated
/// latency instead of switching in from bypass (which clicks, comb-filters against the bypass
/// tail, and desyncs the track from the others). A tempo/pitch-capable source (a file source)
/// sets this once when it binds the track; a bypass-only source (e.g. a metronome whose tempo is
/// baked into its audio) leaves it off.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_track_set_stretch_always_on(
    track: *mut OwnAudioTrackHandle,
    enabled: i32,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { track_from_ptr(track) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        wrapper.shared.set_stretch_pinned(enabled != 0);

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Sets the track mute state (0.0 = unmuted, 1.0 = muted).
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_track_set_mute(track: *mut OwnAudioTrackHandle, muted: f32) -> i32 {
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
