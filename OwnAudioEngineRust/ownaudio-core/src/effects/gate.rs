//! Noise gate / dynamic amplifier.

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};

pub const PARAM_THRESHOLD: u32 = 2; // dB (-80 … 0)
pub const PARAM_ATTACK: u32 = 3;    // ms (0.1 … 100)
pub const PARAM_RELEASE: u32 = 4;   // ms (10 … 2000)
pub const PARAM_HOLD: u32 = 5;      // ms (0 … 500)

/// Noise gate effect.
pub struct Gate {
    enabled: bool,
    mix: f32,
    threshold_db: f32,
    attack_ms: f32,
    release_ms: f32,
    hold_ms: f32,
}

impl Gate {
    /// Creates a new [`Gate`] with default parameters.
    pub fn new(_sample_rate: f32) -> Self {
        Self {
            enabled: true,
            mix: 1.0,
            threshold_db: -40.0,
            attack_ms: 1.0,
            release_ms: 100.0,
            hold_ms: 50.0,
        }
    }
}

impl Effect for Gate {
    fn effect_type(&self) -> EffectType { EffectType::Gate }

    fn process(&mut self, _buffer: &mut [f32], _channels: u16) {}

    fn set_param(&mut self, param_id: u32, value: f32) -> bool {
        match param_id {
            PARAM_ENABLED   => { self.enabled = value >= 0.5; true }
            PARAM_MIX       => { self.mix = value.clamp(0.0, 1.0); true }
            PARAM_THRESHOLD => { self.threshold_db = value.clamp(-80.0, 0.0); true }
            PARAM_ATTACK    => { self.attack_ms = value.clamp(0.1, 100.0); true }
            PARAM_RELEASE   => { self.release_ms = value.clamp(10.0, 2000.0); true }
            PARAM_HOLD      => { self.hold_ms = value.clamp(0.0, 500.0); true }
            _ => false,
        }
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        match param_id {
            PARAM_ENABLED   => Some(if self.enabled { 1.0 } else { 0.0 }),
            PARAM_MIX       => Some(self.mix),
            PARAM_THRESHOLD => Some(self.threshold_db),
            PARAM_ATTACK    => Some(self.attack_ms),
            PARAM_RELEASE   => Some(self.release_ms),
            PARAM_HOLD      => Some(self.hold_ms),
            _ => None,
        }
    }

    fn reset(&mut self) {}
    fn is_enabled(&self) -> bool { self.enabled }
    fn set_enabled(&mut self, enabled: bool) { self.enabled = enabled; }
}
