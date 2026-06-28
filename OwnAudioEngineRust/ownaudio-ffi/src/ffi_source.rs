//! FFI exports for feeding decoded audio into a track via a lock-free ring
//! buffer.
//!
//! A track's audio source is the read side of a single-producer/single-consumer
//! ring buffer.  [`ownaudio_v1_track_set_ring_source`] creates the buffer,
//! installs its reader as the track's source (through the mixer command queue,
//! so the audio thread takes ownership without a data race), and hands the write
//! side back as an opaque handle.  The control thread then pushes interleaved
//! `f32` samples with [`ownaudio_v1_track_source_write`].

use ownaudio_core::ring_buffer;

use crate::error_code::{set_last_error, OwnAudioErrorCode};
use crate::handles::{
    mixer_from_ptr, track_from_ptr, track_source_from_ptr, OwnAudioMixerHandle,
    OwnAudioTrackHandle, OwnAudioTrackSourceHandle, TrackSourceWrapper,
};

/// Creates a lock-free ring buffer feeding the track and writes the write-side
/// handle to `*out_source`.
///
/// - `mixer` — valid mixer handle that owns the track.
/// - `track` — valid track handle whose source is to be (re)installed.
/// - `capacity_samples` — fixed ring-buffer capacity in interleaved `f32`
///   samples; sized for the desired buffering latency
///   (`sample_rate * channels * latency_seconds`).  Clamped up to 1.
/// - `out_source` — receives the write-side handle on success.
///
/// The reader is installed through the mixer's lock-free command queue, so it
/// becomes the track's source on the next render block; any previous source is
/// retired off the audio thread.  Samples written before the reader is installed
/// are buffered and played once it is — the ring buffer is live immediately.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.  Destroy the returned
/// handle with `ownaudio_v1_track_source_destroy`.
#[no_mangle]
pub extern "C" fn ownaudio_v1_track_set_ring_source(
    mixer: *mut OwnAudioMixerHandle,
    track: *mut OwnAudioTrackHandle,
    capacity_samples: usize,
    out_source: *mut *mut OwnAudioTrackSourceHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if mixer.is_null() || track.is_null() || out_source.is_null() {
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
        let (writer, reader) = ring_buffer(capacity_samples.max(1));

        // Hand the reader to the audio thread via the command queue; it becomes
        // the track's source on the next render block (the old one is retired
        // off the real-time path).
        if mixer_wrapper
            .controller
            .set_track_source(track_id, Some(Box::new(reader)))
            .is_err()
        {
            set_last_error("mixer command queue is full; track source not set");
            return OwnAudioErrorCode::InternalError as i32;
        }

        let boxed = Box::new(TrackSourceWrapper { writer });
        unsafe {
            *out_source = Box::into_raw(boxed) as *mut OwnAudioTrackSourceHandle;
        }

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Pushes up to `sample_count` interleaved `f32` samples into the track feed and
/// writes the number actually accepted to `*out_written`.
///
/// Lock-free and non-blocking: when the ring buffer is full, fewer samples (or
/// zero) are written and the caller must retry later (back-pressure).  Use
/// [`ownaudio_v1_track_source_free_samples`] to size writes against the free
/// space.
///
/// - `source` — valid handle from `ownaudio_v1_track_set_ring_source`.
/// - `samples` — pointer to the first of `sample_count` `f32` samples.
/// - `sample_count` — number of samples available at `samples`.
/// - `out_written` — receives the number of samples accepted.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_track_source_write(
    source: *mut OwnAudioTrackSourceHandle,
    samples: *const f32,
    sample_count: usize,
    out_written: *mut usize,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if source.is_null() || out_written.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let wrapper = match unsafe { track_source_from_ptr(source) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        let written = if sample_count == 0 || samples.is_null() {
            0
        } else {
            // SAFETY: the caller guarantees `samples` points to `sample_count`
            // valid `f32` values for the duration of the call.
            let slice = unsafe { std::slice::from_raw_parts(samples, sample_count) };
            wrapper.writer.write(slice)
        };

        unsafe {
            *out_written = written;
        }

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Writes the number of samples that can currently be written without overflow
/// to `*out_free`.
///
/// - `source` — valid handle from `ownaudio_v1_track_set_ring_source`.
/// - `out_free` — receives the free-sample count.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_track_source_free_samples(
    source: *mut OwnAudioTrackSourceHandle,
    out_free: *mut usize,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if source.is_null() || out_free.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let wrapper = match unsafe { track_source_from_ptr(source) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        unsafe {
            *out_free = wrapper.writer.free();
        }

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Clears a track's audio source, silencing it.
///
/// The current source (if any) is retired off the audio thread on the next
/// render block.  Any [`OwnAudioTrackSourceHandle`] previously returned for this
/// track keeps writing into a now-detached ring buffer until it is destroyed;
/// callers should destroy the stale source handle after clearing.
///
/// - `mixer` — valid mixer handle that owns the track.
/// - `track` — valid track handle whose source is to be cleared.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_track_clear_source(
    mixer: *mut OwnAudioMixerHandle,
    track: *mut OwnAudioTrackHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if mixer.is_null() || track.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let track_wrapper = match unsafe { track_from_ptr(track) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        let track_id = track_wrapper.id;

        let mixer_wrapper = match unsafe { mixer_from_ptr(mixer) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        if mixer_wrapper
            .controller
            .set_track_source(track_id, None)
            .is_err()
        {
            set_last_error("mixer command queue is full; track source not cleared");
            return OwnAudioErrorCode::InternalError as i32;
        }

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Destroys a track-source write handle and releases its ring-buffer producer.
///
/// Passing `null` is safe and has no effect.  Dropping the producer does not
/// disturb the track's reader on the audio thread; the track simply underruns
/// (renders silence) once the buffered samples are consumed.
#[no_mangle]
pub extern "C" fn ownaudio_v1_track_source_destroy(source: *mut OwnAudioTrackSourceHandle) {
    let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if source.is_null() {
            return;
        }
        unsafe {
            drop(Box::from_raw(source as *mut TrackSourceWrapper));
        }
    }));
}
