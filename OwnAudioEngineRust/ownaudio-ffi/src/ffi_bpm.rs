//! FFI layer for the native BPM detector.
//!
//! Exposes [`ownaudio_soundtouch::BpmDetect`] — the port of the managed `BpmDetect` used by the
//! offline chord/tempo detection — so the C# `ChordDetect` feature can drop its managed SoundTouch
//! dependency and call the native detector instead. The detector is not real-time (it allocates in
//! `get_bpm`), which is fine: it is driven offline on the caller's thread.

use ownaudio_soundtouch::BpmDetect;

use crate::error_code::OwnAudioErrorCode;
use crate::handles::OwnAudioBpmHandle;

/// Casts a raw `*mut OwnAudioBpmHandle` back to `&mut BpmDetect`.
///
/// # Safety
/// The caller must guarantee that `ptr` was obtained from `ownaudio_v1_bpm_create` and has not been
/// destroyed yet.
unsafe fn bpm_from_ptr<'a>(ptr: *mut OwnAudioBpmHandle) -> Option<&'a mut BpmDetect> {
    if ptr.is_null() {
        None
    } else {
        Some(&mut *(ptr as *mut BpmDetect))
    }
}

/// Creates a BPM detector for the given channel count and input sample rate.
///
/// - `channels` — interleaved channel count of the samples fed to
///   [`ownaudio_v1_bpm_input_samples`] (clamped to at least 1).
/// - `sample_rate` — input sample rate in Hz.
/// - `out_detector` — receives the new handle on success.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success. The handle must be released with
/// `ownaudio_v1_bpm_destroy`.
#[no_mangle]
pub extern "C" fn ownaudio_v1_bpm_create(
    channels: u32,
    sample_rate: u32,
    out_detector: *mut *mut OwnAudioBpmHandle,
) -> i32 {
    let result = std::panic::catch_unwind(|| {
        if out_detector.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let detector = BpmDetect::new(channels.max(1) as usize, sample_rate as usize);
        let boxed = Box::new(detector);
        unsafe {
            *out_detector = Box::into_raw(boxed) as *mut OwnAudioBpmHandle;
        }
        OwnAudioErrorCode::Success as i32
    });

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Feeds `num_samples` interleaved frames from `samples` into the detector.
///
/// `samples` must point to at least `num_samples * channels` `f32` values (the channel count passed
/// to `ownaudio_v1_bpm_create`).
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_bpm_input_samples(
    handle: *mut OwnAudioBpmHandle,
    samples: *const f32,
    num_samples: usize,
    sample_count: usize,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if samples.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let detector = match unsafe { bpm_from_ptr(handle) } {
            Some(d) => d,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        if num_samples > 0 && sample_count > 0 {
            let src = unsafe { std::slice::from_raw_parts(samples, sample_count) };
            detector.input_samples(src, num_samples);
        }
        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Writes the current estimated tempo (in BPM) to `*out_bpm`, or `0.0` when there is not yet enough
/// data for a reliable estimate.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_bpm_get_bpm(
    handle: *mut OwnAudioBpmHandle,
    out_bpm: *mut f32,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_bpm.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let detector = match unsafe { bpm_from_ptr(handle) } {
            Some(d) => d,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        let bpm = detector.get_bpm();
        unsafe {
            *out_bpm = bpm;
        }
        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Destroys a BPM detector created by `ownaudio_v1_bpm_create`. Null is ignored.
#[no_mangle]
pub extern "C" fn ownaudio_v1_bpm_destroy(handle: *mut OwnAudioBpmHandle) {
    if handle.is_null() {
        return;
    }
    let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| unsafe {
        drop(Box::from_raw(handle as *mut BpmDetect));
    }));
}
