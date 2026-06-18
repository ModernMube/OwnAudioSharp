//! Asymmetric tube-style overdrive.

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};

pub const PARAM_DRIVE: u32 = 2; // 0.0 … 1.0
pub const PARAM_TONE: u32 = 3;  // 0.0 … 1.0
pub const PARAM_LEVEL: u32 = 4; // 0.0 … 2.0

/// Asymmetric tube overdrive effect.
pub struct Overdrive {
    enabled: bool,
    mix: f32,
    drive: f32,
    tone: f32,
    level: f32,
}

impl Overdrive {
    /// Creates a new [`Overdrive`] with default parameters.
    pub fn new(_sample_rate: f32) -> Self {
        Self {
            enabled: true,
            mix: 1.0,
            drive: 0.4,
            tone: 0.5,
            level: 1.0,
        }
    }
}

impl Effect for Overdrive {
    fn effect_type(&self) -> EffectType { EffectType::Overdrive }

    fn process(&mut self, _buffer: &mut [f32], _channels: u16) {}

    fn set_param(&mut self, param_id: u32, value: f32) -> bool {
        match param_id {
            PARAM_ENABLED => { self.enabled = value >= 0.5; true }
            PARAM_MIX     => { self.mix = value.clamp(0.0, 1.0); true }
            PARAM_DRIVE   => { self.drive = value.clamp(0.0, 1.0); true }
            PARAM_TONE    => { self.tone = value.clamp(0.0, 1.0); true }
            PARAM_LEVEL   => { self.level = value.clamp(0.0, 2.0); true }
            _ => false,
        }
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        match param_id {
            PARAM_ENABLED => Some(if self.enabled { 1.0 } else { 0.0 }),
            PARAM_MIX     => Some(self.mix),
            PARAM_DRIVE   => Some(self.drive),
            PARAM_TONE    => Some(self.tone),
            PARAM_LEVEL   => Some(self.level),
            _ => None,
        }
    }

    fn reset(&mut self) {}
    fn is_enabled(&self) -> bool { self.enabled }
    fn set_enabled(&mut self, enabled: bool) { self.enabled = enabled; }
}
