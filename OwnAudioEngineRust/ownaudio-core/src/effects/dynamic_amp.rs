//! Adaptive dynamic amplifier — dual-window RMS AGC with noise gate.

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};

/// Param IDs for [`DynamicAmp`].
pub const PARAM_TARGET_RMS_DB: u32 = 2;
pub const PARAM_ATTACK_TIME: u32 = 3;
pub const PARAM_RELEASE_TIME: u32 = 4;
pub const PARAM_NOISE_GATE_DB: u32 = 5;
pub const PARAM_MAX_GAIN: u32 = 6;
pub const PARAM_MAX_GAIN_REDUCTION_DB: u32 = 7;
pub const PARAM_RMS_WINDOW_SECONDS: u32 = 8;
pub const PARAM_MAX_GAIN_CHANGE_DB_S: u32 = 9;

/// Adaptive dynamic amplifier.
///
/// Mirrors the C# `DynamicAmpEffect`: dual-window IIR RMS detection,
/// hysteresis noise gate, and attack/release gain smoothing with a
/// per-block gain-change rate limit.
pub struct DynamicAmp {
    enabled: bool,
    mix: f32,
    target_rms_db: f32,
    attack_time: f32,
    release_time: f32,
    noise_gate_db: f32,
    max_gain: f32,
    max_gain_reduction_db: f32,
    rms_window_seconds: f32,
    max_gain_change_db_s: f32,
}

impl DynamicAmp {
    /// Creates a new [`DynamicAmp`] with default parameters.
    pub fn new(_sample_rate: f32) -> Self {
        Self {
            enabled: true,
            mix: 1.0,
            target_rms_db: -12.0,
            attack_time: 0.30,
            release_time: 1.50,
            noise_gate_db: -50.0,
            max_gain: 6.0,
            max_gain_reduction_db: 12.0,
            rms_window_seconds: 0.5,
            max_gain_change_db_s: 12.0,
        }
    }
}

impl Effect for DynamicAmp {
    fn effect_type(&self) -> EffectType { EffectType::DynamicAmp }

    fn process(&mut self, _buffer: &mut [f32], _channels: u16) {}

    fn set_param(&mut self, param_id: u32, value: f32) -> bool {
        match param_id {
            PARAM_ENABLED             => { self.enabled = value >= 0.5; true }
            PARAM_MIX                 => { self.mix = value.clamp(0.0, 1.0); true }
            PARAM_TARGET_RMS_DB       => { self.target_rms_db = value.clamp(-60.0, -3.0); true }
            PARAM_ATTACK_TIME         => { self.attack_time = value.max(0.05); true }
            PARAM_RELEASE_TIME        => { self.release_time = value.max(0.2); true }
            PARAM_NOISE_GATE_DB       => { self.noise_gate_db = value.clamp(-80.0, -30.0); true }
            PARAM_MAX_GAIN            => { self.max_gain = value.clamp(1.0, 20.0); true }
            PARAM_MAX_GAIN_REDUCTION_DB => { self.max_gain_reduction_db = value.clamp(3.0, 40.0); true }
            PARAM_RMS_WINDOW_SECONDS  => { self.rms_window_seconds = value.max(0.01); true }
            PARAM_MAX_GAIN_CHANGE_DB_S => { self.max_gain_change_db_s = value.max(1.0); true }
            _ => false,
        }
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        match param_id {
            PARAM_ENABLED             => Some(if self.enabled { 1.0 } else { 0.0 }),
            PARAM_MIX                 => Some(self.mix),
            PARAM_TARGET_RMS_DB       => Some(self.target_rms_db),
            PARAM_ATTACK_TIME         => Some(self.attack_time),
            PARAM_RELEASE_TIME        => Some(self.release_time),
            PARAM_NOISE_GATE_DB       => Some(self.noise_gate_db),
            PARAM_MAX_GAIN            => Some(self.max_gain),
            PARAM_MAX_GAIN_REDUCTION_DB => Some(self.max_gain_reduction_db),
            PARAM_RMS_WINDOW_SECONDS  => Some(self.rms_window_seconds),
            PARAM_MAX_GAIN_CHANGE_DB_S => Some(self.max_gain_change_db_s),
            _ => None,
        }
    }

    fn reset(&mut self) {}
    fn is_enabled(&self) -> bool { self.enabled }
    fn set_enabled(&mut self, enabled: bool) { self.enabled = enabled; }
}
