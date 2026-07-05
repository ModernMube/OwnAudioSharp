//! FFI exports for audio effect management on tracks.

use std::os::raw::c_void;

use ownaudio_core::effects::{
    AutoGain, Chorus, Compressor, Delay, Distortion, DynamicAmp, Effect, EffectType, Enhancer,
    Equalizer, Equalizer30, Flanger, Gate, Limiter, Overdrive, Phaser, PitchShift, Reverb, Rotary,
    VstEffect, VstProcessFn,
};
use ownaudio_core::multitrack::MASTER_EFFECT_TARGET;

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
        // A VST bridge needs a plugin handle + process pointer, so it cannot be
        // built from a type tag alone — it is created via the dedicated
        // `ownaudio_v1_track_add_vst_effect` / master entry points instead.
        EffectType::Vst => return None,
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

/// Adds a new effect of the given type to the mixer's **master** effect chain,
/// which runs once over the fully summed mix after every track is rendered.
///
/// - `mixer` — valid mixer handle.
/// - `effect_type` — numeric effect type identifier (`EffectType` enum value).
/// - `sample_rate` — sample rate in Hz; used to size internal buffers.
/// - `out_effect` — receives the new effect handle on success.
///
/// The returned handle is controlled with the same
/// `ownaudio_v1_effect_set_param` / `_get_param` / `_remove` / `_destroy` calls as
/// track effects — it simply targets the master chain instead of a track.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
/// Returns `OwnAudioErrorCode::InvalidHandle` (7) for an unknown `effect_type`.
#[no_mangle]
pub extern "C" fn ownaudio_v1_mixer_add_master_effect(
    mixer: *mut OwnAudioMixerHandle,
    effect_type: u32,
    sample_rate: f32,
    out_effect: *mut *mut OwnAudioEffectHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if mixer.is_null() || out_effect.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

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

        // The master chain is addressed by the sentinel track id; the controller
        // assigns the stable effect id and seeds the parameter shadow exactly as
        // for a track effect.
        let effect_id = match mixer_wrapper
            .controller
            .add_effect(MASTER_EFFECT_TARGET, effect)
        {
            Ok(id) => id,
            Err(_) => {
                set_last_error("mixer command queue is full; master effect not added");
                return OwnAudioErrorCode::InternalError as i32;
            }
        };

        let effect_wrapper = Box::new(EffectWrapper {
            mixer: mixer as *mut crate::handles::MixerWrapper,
            track_id: MASTER_EFFECT_TARGET,
            effect_id,
        });

        unsafe {
            *out_effect = Box::into_raw(effect_wrapper) as *mut OwnAudioEffectHandle;
        }

        OwnAudioErrorCode::Success as i32
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Removes a master effect from the mixer's master chain and destroys the handle.
///
/// The master counterpart of [`ownaudio_v1_effect_remove`]: it takes no track
/// handle because master effects are not owned by any track.
///
/// - `mixer` — valid mixer handle.
/// - `effect` — valid master effect handle from `ownaudio_v1_mixer_add_master_effect`.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_mixer_remove_master_effect(
    mixer: *mut OwnAudioMixerHandle,
    effect: *mut OwnAudioEffectHandle,
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

        let remove = mixer_wrapper
            .controller
            .remove_effect(effect_wrapper.track_id, effect_wrapper.effect_id);

        if remove.is_err() {
            set_last_error("mixer command queue is full; master effect not removed");
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
// VST3 plugin effects (hosted through the OwnAudioVst C ABI)
// ---------------------------------------------------------------------------

/// Builds a [`VstEffect`] and enqueues it on `target_track`'s chain, returning a
/// ready effect handle on success.
///
/// Shared by the track and master VST entry points, which differ only in the
/// target track id (the master chain uses [`MASTER_EFFECT_TARGET`]).
#[allow(clippy::too_many_arguments)]
fn add_vst_effect_to(
    mixer: *mut OwnAudioMixerHandle,
    target_track: u64,
    plugin_handle: *mut c_void,
    process_fn: VstProcessFn,
    max_channels: u16,
    max_block_size: u32,
    latency_samples: u32,
    out_effect: *mut *mut OwnAudioEffectHandle,
) -> i32 {
    if mixer.is_null() || out_effect.is_null() {
        return OwnAudioErrorCode::NullPointer as i32;
    }

    // A null process callback would make the plugin unusable on the audio thread.
    let process_fn = match process_fn {
        Some(f) => f,
        None => {
            set_last_error("VST process function pointer is null");
            return OwnAudioErrorCode::NullPointer as i32;
        }
    };

    let mixer_wrapper = match unsafe { mixer_from_ptr(mixer) } {
        Some(w) => w,
        None => return OwnAudioErrorCode::InvalidHandle as i32,
    };

    let effect: Box<dyn Effect> = Box::new(VstEffect::new(
        plugin_handle,
        process_fn,
        max_channels,
        max_block_size as usize,
        latency_samples,
    ));

    let effect_id = match mixer_wrapper.controller.add_effect(target_track, effect) {
        Ok(id) => id,
        Err(_) => {
            set_last_error("mixer command queue is full; VST effect not added");
            return OwnAudioErrorCode::InternalError as i32;
        }
    };

    let effect_wrapper = Box::new(EffectWrapper {
        mixer: mixer as *mut crate::handles::MixerWrapper,
        track_id: target_track,
        effect_id,
    });

    unsafe {
        *out_effect = Box::into_raw(effect_wrapper) as *mut OwnAudioEffectHandle;
    }

    OwnAudioErrorCode::Success as i32
}

/// Adds an external VST3 plugin to a track's effect chain as a native effect.
///
/// The plugin is created, loaded and parameter-controlled entirely on the C#
/// control plane (the `OwnAudioVst` host); this only receives the opaque plugin
/// handle and the host's `VST3Plugin_ProcessAudio` function pointer, and calls
/// it on the audio thread for every block — no linking against the host and no
/// managed audio processing.
///
/// - `mixer` — valid mixer handle (required to reach the track).
/// - `track` — valid track handle.
/// - `plugin_handle` — opaque plugin instance handle owned by the caller. It
///   MUST outlive this effect (remove/destroy the effect before freeing it).
/// - `process_fn` — the host's `VST3Plugin_ProcessAudio` pointer; must not be null.
/// - `max_channels` — largest channel count the chain will present (planar
///   scratch is sized for this).
/// - `max_block_size` — largest block size in samples per channel.
/// - `latency_samples` — the plugin's processing latency in frames, reported to
///   the mixer for plugin delay compensation (0 when the plugin is zero-latency).
/// - `out_effect` — receives the new effect handle on success.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
#[allow(clippy::too_many_arguments)]
pub extern "C" fn ownaudio_v1_track_add_vst_effect(
    mixer: *mut OwnAudioMixerHandle,
    track: *mut OwnAudioTrackHandle,
    plugin_handle: *mut c_void,
    process_fn: VstProcessFn,
    max_channels: u16,
    max_block_size: u32,
    latency_samples: u32,
    out_effect: *mut *mut OwnAudioEffectHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if track.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        let track_id = match unsafe { track_from_ptr(track) } {
            Some(w) => w.id,
            None => return OwnAudioErrorCode::InvalidHandle as i32,
        };

        add_vst_effect_to(
            mixer,
            track_id,
            plugin_handle,
            process_fn,
            max_channels,
            max_block_size,
            latency_samples,
            out_effect,
        )
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Adds an external VST3 plugin to the mixer's **master** effect chain, which
/// runs once over the fully summed mix after every track is rendered.
///
/// The master counterpart of [`ownaudio_v1_track_add_vst_effect`]; the returned
/// handle is controlled with the same `ownaudio_v1_effect_set_param` /
/// `_get_param` / `ownaudio_v1_mixer_remove_master_effect` calls as any master
/// effect. See that function for the argument contract.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_mixer_add_master_vst_effect(
    mixer: *mut OwnAudioMixerHandle,
    plugin_handle: *mut c_void,
    process_fn: VstProcessFn,
    max_channels: u16,
    max_block_size: u32,
    latency_samples: u32,
    out_effect: *mut *mut OwnAudioEffectHandle,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        add_vst_effect_to(
            mixer,
            MASTER_EFFECT_TARGET,
            plugin_handle,
            process_fn,
            max_channels,
            max_block_size,
            latency_samples,
            out_effect,
        )
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
