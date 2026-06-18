//! Automatic gain control (RMS-based AGC).

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};

pub const PARAM_TARGET_LEVEL: u32 = 2; // 0.0 … 1.0 linear RMS target
pub const PARAM_ATTACK: u32 = 3;       // ms (1 … 500)
pub const PARAM_RELEASE: u32 = 4;      // ms (10 … 2000)
pub const PARAM_MAX_GAIN: u32 = 5;     // linear (1.0 … 10.0)
pub const PARAM_MIN_GAIN: u32 = 6;     // linear (0.1 … 1.0)

/// Automatic gain control effect.
pub struct AutoGain {
    enabled: bool,
    target_level: f32,
    attack_ms: f32,
    release_ms: f32,
    max_gain: f32,
    min_gain: f32,
}

impl AutoGain {
    /// Creates a new [`AutoGain`] with default parameters.
    pub fn new(_sample_rate: f32) -> Self {
        Self {
            enabled: true,
            target_level: 0.25,
            attack_ms: 50.0,
            release_ms: 300.0,
            max_gain: 4.0,
            min_gain: 0.25,
        }
    }
}

impl Effect for AutoGain {
    fn effect_type(&self) -> EffectType { EffectType::AutoGain }

    fn process(&mut self, _buffer: &mut [f32], _channels: u16) {}

    fn set_param(&mut self, param_id: u32, value: f32) -> bool {
        match param_id {
            PARAM_ENABLED      => { self.enabled = value >= 0.5; true }
            PARAM_MIX          => true,
            PARAM_TARGET_LEVEL => { self.target_level = value.clamp(0.0, 1.0); true }
            PARAM_ATTACK       => { self.attack_ms = value.clamp(1.0, 500.0); true }
            PARAM_RELEASE      => { self.release_ms = value.clamp(10.0, 2000.0); true }
            PARAM_MAX_GAIN     => { self.max_gain = value.clamp(1.0, 10.0); true }
            PARAM_MIN_GAIN     => { self.min_gain = value.clamp(0.1, 1.0); true }
            _ => false,
        }
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        match param_id {
            PARAM_ENABLED      => Some(if self.enabled { 1.0 } else { 0.0 }),
            PARAM_MIX          => Some(1.0),
            PARAM_TARGET_LEVEL => Some(self.target_level),
            PARAM_ATTACK       => Some(self.attack_ms),
            PARAM_RELEASE      => Some(self.release_ms),
            PARAM_MAX_GAIN     => Some(self.max_gain),
            PARAM_MIN_GAIN     => Some(self.min_gain),
            _ => None,
        }
    }

    fn reset(&mut self) {}
    fn is_enabled(&self) -> bool { self.enabled }
    fn set_enabled(&mut self, enabled: bool) { self.enabled = enabled; }
}
