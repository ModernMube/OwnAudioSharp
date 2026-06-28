//! FFI exports for audio effect management on tracks.

use ownaudio_core::effects::{
    AutoGain, Chorus, Compressor, Delay, Distortion, DynamicAmp, Effect, EffectType, Enhancer,
    Equalizer, Equalizer30, Flanger, Gate, Limiter, Overdrive, Phaser, PitchShift, Reverb, Rotary,
};

use crate::error_code::{set_last_error, OwnAudioErrorCode};
use crate::handles::{
    effect_from_ptr, mixer_from_ptr, track_from_ptr, EffectWrapper, OwnAudioEffectHandle,
    OwnAudioMixerHandle, OwnAudioTrackHandle,
};

// ---------------------------------------------------------------------------
// Helper: construct an effect from its numeric type tag
// ---------------------------------------------------------------------------

fn create_effect(effect_type_raw: u32, sample_rate: f32) -> Option<Box<dyn Effect>> {
    let effect_type = EffectType::try_from(effect_type_raw).ok()?;

    let effect: Box<dyn Effect> = match effect_type {
        EffectType::Reverb     => Box::new(Reverb::new(sample_rate)),
        EffectType::Equalizer  => Box::new(Equalizer::new(sample_rate)),
        EffectType::Compressor => Box::new(Compressor::new(sample_rate)),
        EffectType::Limiter    => Box::new(Limiter::new(sample_rate)),
        EffectType::Delay      => Box::new(Delay::new(sample_rate)),
        EffectType::Chorus     => Box::new(Chorus::new(sample_rate)),
        EffectType::Distortion => Box::new(Distortion::new(sample_rate)),
        EffectType::Overdrive  => Box::new(Overdrive::new(sample_rate)),
        EffectType::Flanger    => Box::new(Flanger::new(sample_rate)),
        EffectType::Phaser     => Box::new(Phaser::new(sample_rate)),
        EffectType::Rotary     => Box::new(Rotary::new(sample_rate)),
        EffectType::AutoGain   => Box::new(AutoGain::new(sample_rate)),
        EffectType::Enhancer   => Box::new(Enhancer::new(sample_rate)),
        EffectType::Gate       => Box::new(Gate::new(sample_rate)),
        EffectType::PitchShift => Box::new(PitchShift::new(sample_rate)),
        EffectType::DynamicAmp  => Box::new(DynamicAmp::new(sample_rate)),
        EffectType::Equalizer30 => Box::new(Equalizer30::new(sample_rate)),
    };

    Some(effect)
}

// ---------------------------------------------------------------------------
// Effect lifecycle
// ---------------------------------------------------------------------------

/// Adds a new effect of the given type to the track's effect chain.
///
/// - `mixer` — valid mixer handle (required to reach the track).
/// - `track` — valid track handle.
/// - `effect_type` — numeric effect type identifier (`EffectType` enum value).
/// - `sample_rate` — sample rate in Hz; used to size internal buffers.
/// - `out_effect` — receives the new effect handle on success.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
/// Returns `OwnAudioErrorCode::InvalidHandle` (7) for an unknown `effect_type`.
#[no_mangle]
pub extern "C" fn ownaudio_v1_track_add_effect(
    mixer: *mut OwnAudioMixerHandle,
    track: *mut OwnAudioTrackHandle,
    effect_type: u32,
    sample_rate: f32,
    out_effect: *mut *mut OwnAudioEffectHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if mixer.is_null() || track.is_null() || out_effect.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let track_wrapper = match unsafe { track_from_ptr(track) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        let effect = match create_effect(effect_type, sample_rate) {
            Some(e) => e,
            None => {
                set_last_error(format!("unknown effect_type: {}", effect_type));
                return OwnAudioErrorCode::InvalidHandle as i32;
            }
        };

        let mixer_wrapper = match unsafe { mixer_from_ptr(mixer) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        let track_id = track_wrapper.id;

        // Enqueue the insertion on the lock-free command queue; the controller
        // assigns the stable effect id (surviving sibling removals) and seeds
        // the parameter shadow from the effect's defaults before handing it to
        // the audio thread.
        let effect_id = match mixer_wrapper.controller.add_effect(track_id, effect) {
            Ok(id) => id,
            Err(_) => {
                set_last_error("mixer command queue is full; effect not added");
                return OwnAudioErrorCode::InternalError as i32;
            }
        };

        let effect_wrapper = Box::new(EffectWrapper {
            mixer: mixer as *mut crate::handles::MixerWrapper,
            track_id,
            effect_id,
        });

        unsafe {
            *out_effect = Box::into_raw(effect_wrapper) as *mut OwnAudioEffectHandle;
        }

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Destroys an effect handle.
///
/// Passing `null` is safe and has no effect.
/// The effect is NOT removed from the track chain; call `ownaudio_v1_effect_remove` first.
#[no_mangle]
pub extern "C" fn ownaudio_v1_effect_destroy(effect: *mut OwnAudioEffectHandle) {
    // A panic in the effect handle's Drop must never unwind across the FFI boundary.
    let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if effect.is_null() {
            return;
        }
        unsafe {
            drop(Box::from_raw(effect as *mut EffectWrapper));
        }
    }));
}

/// Removes the effect from its track's effect chain and destroys the handle.
///
/// - `mixer` — valid mixer handle.
/// - `track` — valid track handle.
/// - `effect` — valid effect handle to remove and destroy.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_effect_remove(
    mixer: *mut OwnAudioMixerHandle,
    track: *mut OwnAudioTrackHandle,
    effect: *mut OwnAudioEffectHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if mixer.is_null() || track.is_null() || effect.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        // The track handle is validated for API symmetry; the effect's own
        // track id drives the removal.
        if unsafe { track_from_ptr(track) }.is_none() {
            return OwnAudioErrorCode::InvalidHandle as i32;
        }

        let effect_wrapper = match unsafe { effect_from_ptr(effect) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        let mixer_wrapper = match unsafe { mixer_from_ptr(mixer) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        let remove = mixer_wrapper
            .controller
            .remove_effect(effect_wrapper.track_id, effect_wrapper.effect_id);

        if remove.is_err() {
            set_last_error("mixer command queue is full; effect not removed");
            return OwnAudioErrorCode::InternalError as i32;
        }

        unsafe {
            drop(Box::from_raw(effect as *mut EffectWrapper));
        }

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

// ---------------------------------------------------------------------------
// Parameter access
// ---------------------------------------------------------------------------

/// Sets a parameter on an effect by numeric identifier.
///
/// - `effect` — valid effect handle.
/// - `param_id` — numeric parameter identifier (effect-specific).
/// - `value` — new parameter value; clamped to the valid range silently.
///
/// Returns `OwnAudioErrorCode::Success` (0) when the parameter is recognised.
/// Returns `OwnAudioErrorCode::InvalidHandle` (7) when `param_id` is unknown.
#[no_mangle]
pub extern "C" fn ownaudio_v1_effect_set_param(
    mixer: *mut OwnAudioMixerHandle,
    effect: *mut OwnAudioEffectHandle,
    param_id: u32,
    value: f32,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if mixer.is_null() || effect.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let effect_wrapper = match unsafe { effect_from_ptr(effect) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        let mixer_wrapper = match unsafe { mixer_from_ptr(mixer) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        // The change is enqueued on the command queue (applied by the audio
        // thread next block) and mirrored in the control-side shadow.  An
        // unknown parameter is reported via the shadow's known-parameter set,
        // matching the effect's own `set_param` contract.
        match mixer_wrapper.controller.set_effect_param(
            effect_wrapper.track_id,
            effect_wrapper.effect_id,
            param_id,
            value,
        ) {
            Ok(true) => OwnAudioErrorCode::Success as i32,
            Ok(false) => {
                set_last_error(format!("unknown param_id {} for this effect", param_id));
                OwnAudioErrorCode::InvalidHandle as i32
            }
            Err(_) => {
                set_last_error("mixer command queue is full; parameter not set");
                OwnAudioErrorCode::InternalError as i32
            }
        }
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Reads the current value of an effect parameter.
///
/// - `effect` — valid effect handle.
/// - `param_id` — numeric parameter identifier.
/// - `out_value` — receives the current value on success.
///
/// Returns `OwnAudioErrorCode::Success` (0) when the parameter is recognised.
/// Returns `OwnAudioErrorCode::InvalidHandle` (7) when `param_id` is unknown.
#[no_mangle]
pub extern "C" fn ownaudio_v1_effect_get_param(
    mixer: *mut OwnAudioMixerHandle,
    effect: *mut OwnAudioEffectHandle,
    param_id: u32,
    out_value: *mut f32,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if mixer.is_null() || effect.is_null() || out_value.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let effect_wrapper = match unsafe { effect_from_ptr(effect) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        let mixer_wrapper = match unsafe { mixer_from_ptr(mixer) } {
            Some(w) => w,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        // Read from the control-side shadow: the effect itself lives on the
        // audio thread, so it cannot be dereferenced here.  The shadow is seeded
        // with the effect's defaults at add time and kept current on every set.
        match mixer_wrapper
            .controller
            .get_effect_param(effect_wrapper.effect_id, param_id)
        {
            Some(v) => {
                unsafe { *out_value = v; }
                OwnAudioErrorCode::Success as i32
            }
            None => {
                set_last_error(format!("unknown param_id {} for this effect", param_id));
                OwnAudioErrorCode::InvalidHandle as i32
            }
        }
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}
