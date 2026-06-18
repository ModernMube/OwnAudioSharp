//! Phaser with cascaded all-pass filter stages.

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};

pub const PARAM_RATE: u32 = 2;     // Hz (0.01 … 10)
pub const PARAM_DEPTH: u32 = 3;    // 0.0 … 1.0
pub const PARAM_FEEDBACK: u32 = 4; // 0.0 … 1.0
pub const PARAM_STAGES: u32 = 5;   // 2 … 12 (even numbers)

/// Phaser effect.
pub struct Phaser {
    enabled: bool,
    mix: f32,
    rate_hz: f32,
    depth: f32,
    feedback: f32,
    stages: u32,
}

impl Phaser {
    /// Creates a new [`Phaser`] with default parameters.
    pub fn new(_sample_rate: f32) -> Self {
        Self {
            enabled: true,
            mix: 0.5,
            rate_hz: 0.5,
            depth: 0.7,
            feedback: 0.5,
            stages: 4,
        }
    }
}

impl Effect for Phaser {
    fn effect_type(&self) -> EffectType { EffectType::Phaser }

    fn process(&mut self, _buffer: &mut [f32], _channels: u16) {}

    fn set_param(&mut self, param_id: u32, value: f32) -> bool {
        match param_id {
            PARAM_ENABLED  => { self.enabled = value >= 0.5; true }
            PARAM_MIX      => { self.mix = value.clamp(0.0, 1.0); true }
            PARAM_RATE     => { self.rate_hz = value.clamp(0.01, 10.0); true }
            PARAM_DEPTH    => { self.depth = value.clamp(0.0, 1.0); true }
            PARAM_FEEDBACK => { self.feedback = value.clamp(0.0, 1.0); true }
            PARAM_STAGES   => {
                let s = (value as u32).clamp(2, 12);
                self.stages = if s % 2 == 0 { s } else { s + 1 };
                true
            }
            _ => false,
        }
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        match param_id {
            PARAM_ENABLED  => Some(if self.enabled { 1.0 } else { 0.0 }),
            PARAM_MIX      => Some(self.mix),
            PARAM_RATE     => Some(self.rate_hz),
            PARAM_DEPTH    => Some(self.depth),
            PARAM_FEEDBACK => Some(self.feedback),
            PARAM_STAGES   => Some(self.stages as f32),
            _ => None,
        }
    }

    fn reset(&mut self) {}
    fn is_enabled(&self) -> bool { self.enabled }
    fn set_enabled(&mut self, enabled: bool) { self.enabled = enabled; }
}
