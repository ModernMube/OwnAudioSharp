//! Multi-voice chorus with LFO modulation.

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};

pub const PARAM_RATE: u32 = 2;     // LFO rate in Hz (0.01 … 10)
pub const PARAM_DEPTH: u32 = 3;    // modulation depth in ms (0 … 30)
pub const PARAM_FEEDBACK: u32 = 4; // 0.0 … 1.0
pub const PARAM_VOICES: u32 = 5;   // 1 … 8

/// Multi-voice chorus effect.
pub struct Chorus {
    enabled: bool,
    mix: f32,
    rate_hz: f32,
    depth_ms: f32,
    feedback: f32,
    voices: u32,
}

impl Chorus {
    /// Creates a new [`Chorus`] with default parameters.
    pub fn new(_sample_rate: f32) -> Self {
        Self {
            enabled: true,
            mix: 0.5,
            rate_hz: 0.5,
            depth_ms: 10.0,
            feedback: 0.2,
            voices: 2,
        }
    }
}

impl Effect for Chorus {
    fn effect_type(&self) -> EffectType { EffectType::Chorus }

    fn process(&mut self, _buffer: &mut [f32], _channels: u16) {}

    fn set_param(&mut self, param_id: u32, value: f32) -> bool {
        match param_id {
            PARAM_ENABLED  => { self.enabled = value >= 0.5; true }
            PARAM_MIX      => { self.mix = value.clamp(0.0, 1.0); true }
            PARAM_RATE     => { self.rate_hz = value.clamp(0.01, 10.0); true }
            PARAM_DEPTH    => { self.depth_ms = value.clamp(0.0, 30.0); true }
            PARAM_FEEDBACK => { self.feedback = value.clamp(0.0, 1.0); true }
            PARAM_VOICES   => { self.voices = (value.clamp(1.0, 8.0) as u32).max(1); true }
            _ => false,
        }
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        match param_id {
            PARAM_ENABLED  => Some(if self.enabled { 1.0 } else { 0.0 }),
            PARAM_MIX      => Some(self.mix),
            PARAM_RATE     => Some(self.rate_hz),
            PARAM_DEPTH    => Some(self.depth_ms),
            PARAM_FEEDBACK => Some(self.feedback),
            PARAM_VOICES   => Some(self.voices as f32),
            _ => None,
        }
    }

    fn reset(&mut self) {}
    fn is_enabled(&self) -> bool { self.enabled }
    fn set_enabled(&mut self, enabled: bool) { self.enabled = enabled; }
}
