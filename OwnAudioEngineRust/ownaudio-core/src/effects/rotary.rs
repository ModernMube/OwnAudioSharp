//! Rotary / Leslie-cabinet speaker simulator.

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};

pub const PARAM_RATE: u32 = 2;  // rotation rate in Hz (0.1 … 10)
pub const PARAM_DEPTH: u32 = 3; // modulation depth (0.0 … 1.0)

/// Rotary/Leslie cabinet simulator.
pub struct Rotary {
    enabled: bool,
    mix: f32,
    rate_hz: f32,
    depth: f32,
}

impl Rotary {
    /// Creates a new [`Rotary`] with default parameters.
    pub fn new(_sample_rate: f32) -> Self {
        Self {
            enabled: true,
            mix: 1.0,
            rate_hz: 1.0,
            depth: 0.7,
        }
    }
}

impl Effect for Rotary {
    fn effect_type(&self) -> EffectType { EffectType::Rotary }

    fn process(&mut self, _buffer: &mut [f32], _channels: u16) {}

    fn set_param(&mut self, param_id: u32, value: f32) -> bool {
        match param_id {
            PARAM_ENABLED => { self.enabled = value >= 0.5; true }
            PARAM_MIX     => { self.mix = value.clamp(0.0, 1.0); true }
            PARAM_RATE    => { self.rate_hz = value.clamp(0.1, 10.0); true }
            PARAM_DEPTH   => { self.depth = value.clamp(0.0, 1.0); true }
            _ => false,
        }
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        match param_id {
            PARAM_ENABLED => Some(if self.enabled { 1.0 } else { 0.0 }),
            PARAM_MIX     => Some(self.mix),
            PARAM_RATE    => Some(self.rate_hz),
            PARAM_DEPTH   => Some(self.depth),
            _ => None,
        }
    }

    fn reset(&mut self) {}
    fn is_enabled(&self) -> bool { self.enabled }
    fn set_enabled(&mut self, enabled: bool) { self.enabled = enabled; }
}
