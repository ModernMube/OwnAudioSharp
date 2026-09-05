//! Mixer-free effect FFI: create, set params, process in place, destroy.
//!
//! The mixer path ([`crate::ffi_effects`]) keeps the DSP on the audio thread behind a
//! command queue. Direct `IEffectProcessor.Process` / Matchering / tests need the same
//! engine on the caller's thread, so this surface owns a boxed
//! [`ownaudio_core::effects::Effect`] and runs it immediately. A mixer twin and a
//! standalone instance are never the same handle.

use std::ffi::c_void;

use crate::error_code::{set_last_error, OwnAudioErrorCode};
use crate::ffi_effects::create_effect;
use crate::handles::{
    standalone_effect_from_ptr, OwnAudioStandaloneEffectHandle, StandaloneEffectWrapper,
};
use ownaudio_core::effects::{VstEffect, VstProcessFn};

/// Builds a standalone effect of `effect_type`, sized for `sample_rate`.
///
/// `channels` is the layout `process` will see unless the caller passes a different
/// count later. VST cannot be built from a type tag — that stays on the mixer path.
///
/// # Safety
/// - `out_effect` must point to a writable pointer slot.
/// - Null pointers come back as an error code, not a dereference.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_standalone_effect_create(
    effect_type: u32,
    sample_rate: f32,
    channels: u16,
    out_effect: *mut *mut OwnAudioStandaloneEffectHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_effect.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        if !sample_rate.is_finite() || sample_rate <= 0.0 {
            set_last_error("standalone effect sample_rate must be a positive finite Hz value");
            return OwnAudioErrorCode::UnsupportedConfig as i32;
        }

        let effect = match create_effect(effect_type, sample_rate) {
            Some(e) => e,
            None => {
                set_last_error(format!("unknown effect_type: {}", effect_type));
                return OwnAudioErrorCode::InvalidHandle as i32;
            }
        };

        let wrapper = Box::new(StandaloneEffectWrapper {
            effect,
            channels: channels.max(1),
        });

        unsafe {
            *out_effect = Box::into_raw(wrapper) as *mut OwnAudioStandaloneEffectHandle;
        }

        OwnAudioErrorCode::Success as i32
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Builds a standalone VST bridge around an already-loaded plugin.
///
/// The type-tag `create` cannot reach VST: the plugin instance is owned by the
/// caller, not by an `EffectType`. Same bridge the mixer chain uses, so a direct
/// `Process` and a mixer twin behave identically — soft bypass on param 0, dry/wet
/// on param 1, both aligned to `latency_samples`.
///
/// - `plugin_handle` — opaque instance handle; must outlive the effect.
/// - `process_fn` — the host's `VST3Plugin_ProcessAudio` pointer, not null.
/// - `channels` / `max_block_size` — the planar scratch is sized for these and a
///   larger block is skipped rather than reallocated on the caller's thread.
/// - `latency_samples` — the plugin's own latency, which the dry path is delayed by.
///
/// # Safety
/// - `out_effect` must point to a writable pointer slot.
/// - `plugin_handle` is opaque to the engine and only handed back to the callback.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_standalone_effect_create_vst(
    plugin_handle: *mut c_void,
    process_fn: VstProcessFn,
    channels: u16,
    max_block_size: u32,
    latency_samples: u32,
    out_effect: *mut *mut OwnAudioStandaloneEffectHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_effect.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let Some(process) = process_fn else {
            set_last_error("standalone vst effect needs a non-null process_fn");
            return OwnAudioErrorCode::NullPointer as i32;
        };

        if max_block_size == 0 {
            set_last_error("standalone vst effect needs a positive max_block_size");
            return OwnAudioErrorCode::UnsupportedConfig as i32;
        }

        let channels = channels.max(1);
        let effect = Box::new(VstEffect::new(
            plugin_handle,
            process,
            channels,
            max_block_size as usize,
            latency_samples,
        ));

        let wrapper = Box::new(StandaloneEffectWrapper { effect, channels });

        unsafe {
            *out_effect = Box::into_raw(wrapper) as *mut OwnAudioStandaloneEffectHandle;
        }

        OwnAudioErrorCode::Success as i32
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Applies `param_id` immediately. Unknown ids are `InvalidHandle`; out of range
/// values clamp the same way the mixer twin does.
///
/// # Safety
/// - `effect` must be a live handle from [`ownaudio_v1_standalone_effect_create`].
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_standalone_effect_set_param(
    effect: *mut OwnAudioStandaloneEffectHandle,
    param_id: u32,
    value: f32,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { standalone_effect_from_ptr(effect) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        if wrapper.effect.set_param(param_id, value) {
            OwnAudioErrorCode::Success as i32
        } else {
            set_last_error(format!("unknown param_id {} for this effect", param_id));
            OwnAudioErrorCode::InvalidHandle as i32
        }
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Reads a param back. Same unknown-id contract as set.
///
/// # Safety
/// - `out_value` must point to a writable `f32`.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_standalone_effect_get_param(
    effect: *mut OwnAudioStandaloneEffectHandle,
    param_id: u32,
    out_value: *mut f32,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_value.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let wrapper = match unsafe { standalone_effect_from_ptr(effect) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        match wrapper.effect.get_param(param_id) {
            Some(v) => {
                unsafe {
                    *out_value = v;
                }
                OwnAudioErrorCode::Success as i32
            }
            None => {
                set_last_error(format!("unknown param_id {} for this effect", param_id));
                OwnAudioErrorCode::InvalidHandle as i32
            }
        }
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// In-place process of `frame_count` interleaved frames.
///
/// `channels == 0` uses the count from create. Empty / zero-frame calls succeed
/// without touching the buffer.
///
/// # Safety
/// - `buffer` must be valid for `frame_count * channels` `f32` values when both
///   are non-zero.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_standalone_effect_process(
    effect: *mut OwnAudioStandaloneEffectHandle,
    buffer: *mut f32,
    frame_count: u32,
    channels: u16,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { standalone_effect_from_ptr(effect) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        let ch = if channels == 0 {
            wrapper.channels
        } else {
            channels
        };
        if frame_count == 0 || ch == 0 {
            return OwnAudioErrorCode::Success as i32;
        }

        if buffer.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let len = frame_count as usize * ch as usize;
        let buf = unsafe { std::slice::from_raw_parts_mut(buffer, len) };
        wrapper.effect.process(buf, ch);

        OwnAudioErrorCode::Success as i32
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Drops delay lines / envelopes / LFO phase. Parameters stay put.
///
/// # Safety
/// - `effect` must be a live standalone handle.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_standalone_effect_reset(
    effect: *mut OwnAudioStandaloneEffectHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let wrapper = match unsafe { standalone_effect_from_ptr(effect) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };
        wrapper.effect.reset();
        OwnAudioErrorCode::Success as i32
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Look-ahead / plugin delay in frames. Zero-latency effects report 0.
///
/// # Safety
/// - `out_latency` must point to a writable `u32`.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_standalone_effect_latency(
    effect: *mut OwnAudioStandaloneEffectHandle,
    out_latency: *mut u32,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_latency.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let wrapper = match unsafe { standalone_effect_from_ptr(effect) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        unsafe {
            *out_latency = wrapper.effect.latency_samples();
        }
        OwnAudioErrorCode::Success as i32
    }));

    crate::error_code::finish_catch_unwind(result)
}

/// Frees a standalone effect. Null is a no-op.
///
/// # Safety
/// - `effect` must be a live handle from [`ownaudio_v1_standalone_effect_create`],
///   not yet destroyed.
#[no_mangle]
pub unsafe extern "C" fn ownaudio_v1_standalone_effect_destroy(
    effect: *mut OwnAudioStandaloneEffectHandle,
) {
    let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if effect.is_null() {
            return;
        }
        unsafe {
            drop(Box::from_raw(effect as *mut StandaloneEffectWrapper));
        }
    }));
}
