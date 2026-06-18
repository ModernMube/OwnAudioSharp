//! Look-ahead brick-wall limiter.

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};

pub const PARAM_THRESHOLD: u32 = 2; // dB (-20 … 0)
pub const PARAM_CEILING: u32 = 3;   // dB (-2 … 0)
pub const PARAM_RELEASE: u32 = 4;   // ms
pub const PARAM_LOOKAHEAD: u32 = 5; // ms

/// Look-ahead brick-wall limiter.
pub struct Limiter {
    enabled: bool,
    threshold_db: f32,
    ceiling_db: f32,
    release_ms: f32,
    lookahead_ms: f32,
}

impl Limiter {
    /// Creates a new [`Limiter`] with default parameters.
    pub fn new(_sample_rate: f32) -> Self {
        Self {
            enabled: true,
            threshold_db: -3.0,
            ceiling_db: -0.1,
            release_ms: 50.0,
            lookahead_ms: 5.0,
        }
    }
}

impl Effect for Limiter {
    fn effect_type(&self) -> EffectType { EffectType::Limiter }

    fn process(&mut self, _buffer: &mut [f32], _channels: u16) {}

    fn set_param(&mut self, param_id: u32, value: f32) -> bool {
        match param_id {
            PARAM_ENABLED   => { self.enabled = value >= 0.5; true }
            PARAM_MIX       => true,
            PARAM_THRESHOLD => { self.threshold_db = value.clamp(-20.0, 0.0); true }
            PARAM_CEILING   => { self.ceiling_db = value.clamp(-2.0, 0.0); true }
            PARAM_RELEASE   => { self.release_ms = value.clamp(1.0, 500.0); true }
            PARAM_LOOKAHEAD => { self.lookahead_ms = value.clamp(0.0, 20.0); true }
            _ => false,
        }
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        match param_id {
            PARAM_ENABLED   => Some(if self.enabled { 1.0 } else { 0.0 }),
            PARAM_MIX       => Some(1.0),
            PARAM_THRESHOLD => Some(self.threshold_db),
            PARAM_CEILING   => Some(self.ceiling_db),
            PARAM_RELEASE   => Some(self.release_ms),
            PARAM_LOOKAHEAD => Some(self.lookahead_ms),
            _ => None,
        }
    }

    fn reset(&mut self) {}
    fn is_enabled(&self) -> bool { self.enabled }
    fn set_enabled(&mut self, enabled: bool) { self.enabled = enabled; }
}
