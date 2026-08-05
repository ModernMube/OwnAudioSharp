//! The MT3 transcription exports.
//!
//! Loading is expensive and transcription is long-running, so the surface is deliberately small:
//! create once, transcribe as often as you like, destroy. The note array handed back is owned by
//! Rust until `ownaudio_mt3_v1_free_notes` takes it back.

use std::ffi::CStr;
use std::os::raw::{c_char, c_void};
use std::panic::{catch_unwind, AssertUnwindSafe};

use ownaudio_mt3_core::{ModelPaths, Mt3Transcriber, TranscribeOptions};

use crate::error_code::{fail, finish_catch_unwind, set_last_error, Mt3ErrorCode};
use crate::handles::Mt3TranscriberHandle;
use crate::types::{NativeMt3Note, NativeMt3Options};

/// Progress reporter invoked once per audio segment with a 0..1 fraction.
pub type Mt3ProgressCallback = extern "C" fn(progress: f64, user_data: *mut c_void);

/// Reads a borrowed C string, or `None` if it is null or not UTF-8.
unsafe fn as_str<'a>(ptr: *const c_char) -> Option<&'a str> {
    if ptr.is_null() {
        return None;
    }
    CStr::from_ptr(ptr).to_str().ok()
}

/// Casts a raw handle back to the transcriber it came from.
///
/// # Safety
/// `ptr` must have come from [`ownaudio_mt3_v1_create`] and not yet been destroyed.
unsafe fn transcriber_from_ptr<'a>(
    ptr: *mut Mt3TranscriberHandle,
) -> Option<&'a mut Mt3Transcriber> {
    if ptr.is_null() {
        None
    } else {
        Some(&mut *(ptr as *mut Mt3Transcriber))
    }
}

/// Loads an exported MT3 model.
///
/// All four paths must come from the same export run. On success `out_transcriber` receives a
/// handle that must be released with [`ownaudio_mt3_v1_destroy`].
///
/// # Safety
/// The path arguments must be NUL-terminated UTF-8 or null; `out_transcriber` must point to a
/// writable pointer slot.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_mt3_v1_create(
    encoder_path: *const c_char,
    decoder_init_path: *const c_char,
    decoder_step_path: *const c_char,
    vocab_path: *const c_char,
    options: *const NativeMt3Options,
    out_transcriber: *mut *mut Mt3TranscriberHandle,
) -> i32 {
    let result = catch_unwind(AssertUnwindSafe(|| {
        if out_transcriber.is_null() {
            return Mt3ErrorCode::NullPointer as i32;
        }
        *out_transcriber = std::ptr::null_mut();

        let (Some(encoder), Some(decoder_init), Some(decoder_step), Some(vocab)) = (
            as_str(encoder_path),
            as_str(decoder_init_path),
            as_str(decoder_step_path),
            as_str(vocab_path),
        ) else {
            set_last_error("model paths must be non-null UTF-8".to_string());
            return Mt3ErrorCode::InvalidUtf8 as i32;
        };

        let paths = ModelPaths {
            encoder: encoder.to_string(),
            decoder_init: decoder_init.to_string(),
            decoder_step: decoder_step.to_string(),
            vocab: vocab.to_string(),
        };

        let options = match options.is_null() {
            true => TranscribeOptions::default(),
            false => TranscribeOptions {
                threads: (*options).threads.clamp(0, u16::MAX as i32) as u16,
                skip_drums: (*options).skip_drums != 0,
            },
        };

        match Mt3Transcriber::load(&paths, options) {
            Ok(transcriber) => {
                *out_transcriber =
                    Box::into_raw(Box::new(transcriber)) as *mut Mt3TranscriberHandle;
                Mt3ErrorCode::Success as i32
            }
            Err(err) => fail(err),
        }
    }));

    finish_catch_unwind(result)
}

/// Releases a transcriber and the ONNX sessions behind it. Null is a no-op.
///
/// # Safety
/// `transcriber` must have come from [`ownaudio_mt3_v1_create`] and must not be used afterwards.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_mt3_v1_destroy(transcriber: *mut Mt3TranscriberHandle) {
    if transcriber.is_null() {
        return;
    }

    let _ = catch_unwind(AssertUnwindSafe(|| {
        drop(Box::from_raw(transcriber as *mut Mt3Transcriber));
    }));
}

/// The sample rate the model runs at, so the caller can decode straight into it and skip a
/// resampling pass. Returns 0 for an invalid handle.
///
/// # Safety
/// `transcriber` must be a live handle.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_mt3_v1_sample_rate(
    transcriber: *mut Mt3TranscriberHandle,
) -> u32 {
    transcriber_from_ptr(transcriber).map_or(0, |t| t.sample_rate())
}

/// Transcribes a whole track.
///
/// `samples` is interleaved float PCM at `sample_rate`. On success `out_notes` receives an array
/// of `out_count` notes that stays valid until [`ownaudio_mt3_v1_free_notes`] is called with the
/// same pair. `progress` may be null; when it is not, it is called once per segment on the
/// calling thread.
///
/// This runs the full autoregressive decode and takes minutes on a long track — never call it
/// from a UI or audio thread.
///
/// # Safety
/// `samples` must point to `sample_count` readable floats; `out_notes` and `out_count` must point
/// to writable slots.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_mt3_v1_transcribe(
    transcriber: *mut Mt3TranscriberHandle,
    samples: *const f32,
    sample_count: usize,
    sample_rate: u32,
    channels: u16,
    progress: Option<Mt3ProgressCallback>,
    user_data: *mut c_void,
    out_notes: *mut *mut NativeMt3Note,
    out_count: *mut usize,
) -> i32 {
    let result = catch_unwind(AssertUnwindSafe(|| {
        if out_notes.is_null() || out_count.is_null() {
            return Mt3ErrorCode::NullPointer as i32;
        }
        *out_notes = std::ptr::null_mut();
        *out_count = 0;

        let Some(transcriber) = transcriber_from_ptr(transcriber) else {
            set_last_error("transcriber handle is null".to_string());
            return Mt3ErrorCode::InvalidHandle as i32;
        };
        if samples.is_null() && sample_count > 0 {
            return Mt3ErrorCode::NullPointer as i32;
        }
        if sample_rate == 0 {
            set_last_error("sample_rate must be positive".to_string());
            return Mt3ErrorCode::NullPointer as i32;
        }

        let audio = match sample_count {
            0 => &[][..],
            _ => std::slice::from_raw_parts(samples, sample_count),
        };

        // A managed callback that throws would unwind through Rust, so it is caught here and its
        // failure is simply treated as "no more progress reports".
        let report = |fraction: f64| {
            if let Some(callback) = progress {
                let _ = catch_unwind(AssertUnwindSafe(|| callback(fraction, user_data)));
            }
        };

        let notes = match transcriber.transcribe(audio, sample_rate, channels.max(1), report) {
            Ok(notes) => notes,
            Err(err) => return fail(err),
        };

        let mut native: Vec<NativeMt3Note> = notes
            .iter()
            .map(|n| NativeMt3Note {
                start_time: n.start as f32,
                end_time: n.end as f32,
                pitch: n.pitch as i32,
                velocity: n.velocity as i32,
                program: n.program as i32,
                is_drum: n.is_drum as i32,
            })
            .collect();

        native.shrink_to_fit();
        *out_count = native.len();
        *out_notes = native.as_mut_ptr();
        std::mem::forget(native);

        Mt3ErrorCode::Success as i32
    }));

    finish_catch_unwind(result)
}

/// Frees a note array handed out by [`ownaudio_mt3_v1_transcribe`].
///
/// # Safety
/// `notes` and `count` must be exactly the pair a single transcribe call produced, and this must
/// be called at most once for that pair.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_mt3_v1_free_notes(notes: *mut NativeMt3Note, count: usize) {
    if notes.is_null() || count == 0 {
        return;
    }

    let _ = catch_unwind(AssertUnwindSafe(|| {
        drop(Vec::from_raw_parts(notes, count, count));
    }));
}
