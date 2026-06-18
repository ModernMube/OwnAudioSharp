//! Stereo delay with ping-pong and damping.

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};

pub const PARAM_TIME_MS: u32 = 2;   // 1 … 5000 ms
pub const PARAM_FEEDBACK: u32 = 3;  // 0.0 … 1.0
pub const PARAM_DAMPING: u32 = 4;   // 0.0 … 1.0
pub const PARAM_PING_PONG: u32 = 5; // 0.0 = off, 1.0 = on

/// Stereo delay effect.
pub struct Delay {
    enabled: bool,
    mix: f32,
    time_ms: f32,
    feedback: f32,
    damping: f32,
    ping_pong: bool,
}

impl Delay {
    /// Creates a new [`Delay`] with default parameters.
    pub fn new(_sample_rate: f32) -> Self {
        Self {
            enabled: true,
            mix: 0.5,
            time_ms: 300.0,
            feedback: 0.4,
            damping: 0.2,
            ping_pong: false,
        }
    }
}

impl Effect for Delay {
    fn effect_type(&self) -> EffectType { EffectType::Delay }

    fn process(&mut self, _buffer: &mut [f32], _channels: u16) {}

    fn set_param(&mut self, param_id: u32, value: f32) -> bool {
        match param_id {
            PARAM_ENABLED   => { self.enabled = value >= 0.5; true }
            PARAM_MIX       => { self.mix = value.clamp(0.0, 1.0); true }
            PARAM_TIME_MS   => { self.time_ms = value.clamp(1.0, 5000.0); true }
            PARAM_FEEDBACK  => { self.feedback = value.clamp(0.0, 1.0); true }
            PARAM_DAMPING   => { self.damping = value.clamp(0.0, 1.0); true }
            PARAM_PING_PONG => { self.ping_pong = value >= 0.5; true }
            _ => false,
        }
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        match param_id {
            PARAM_ENABLED   => Some(if self.enabled { 1.0 } else { 0.0 }),
            PARAM_MIX       => Some(self.mix),
            PARAM_TIME_MS   => Some(self.time_ms),
            PARAM_FEEDBACK  => Some(self.feedback),
            PARAM_DAMPING   => Some(self.damping),
            PARAM_PING_PONG => Some(if self.ping_pong { 1.0 } else { 0.0 }),
            _ => None,
        }
    }

    fn reset(&mut self) {}
    fn is_enabled(&self) -> bool { self.enabled }
    fn set_enabled(&mut self, enabled: bool) { self.enabled = enabled; }
}
