//! Harmonic exciter / spectral enhancer.

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};

pub const PARAM_AMOUNT: u32 = 2;     // 0.0 … 1.0
pub const PARAM_FREQUENCY: u32 = 3;  // Hz (1000 … 16000)
pub const PARAM_HARMONICS: u32 = 4;  // drive for harmonic generation (0.0 … 1.0)

/// Harmonic enhancer effect.
pub struct Enhancer {
    enabled: bool,
    mix: f32,
    amount: f32,
    frequency_hz: f32,
    harmonics: f32,
}

impl Enhancer {
    /// Creates a new [`Enhancer`] with default parameters.
    pub fn new(_sample_rate: f32) -> Self {
        Self {
            enabled: true,
            mix: 0.5,
            amount: 0.4,
            frequency_hz: 3000.0,
            harmonics: 0.3,
        }
    }
}

impl Effect for Enhancer {
    fn effect_type(&self) -> EffectType { EffectType::Enhancer }

    fn process(&mut self, _buffer: &mut [f32], _channels: u16) {}

    fn set_param(&mut self, param_id: u32, value: f32) -> bool {
        match param_id {
            PARAM_ENABLED   => { self.enabled = value >= 0.5; true }
            PARAM_MIX       => { self.mix = value.clamp(0.0, 1.0); true }
            PARAM_AMOUNT    => { self.amount = value.clamp(0.0, 1.0); true }
            PARAM_FREQUENCY => { self.frequency_hz = value.clamp(1000.0, 16000.0); true }
            PARAM_HARMONICS => { self.harmonics = value.clamp(0.0, 1.0); true }
            _ => false,
        }
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        match param_id {
            PARAM_ENABLED   => Some(if self.enabled { 1.0 } else { 0.0 }),
            PARAM_MIX       => Some(self.mix),
            PARAM_AMOUNT    => Some(self.amount),
            PARAM_FREQUENCY => Some(self.frequency_hz),
            PARAM_HARMONICS => Some(self.harmonics),
            _ => None,
        }
    }

    fn reset(&mut self) {}
    fn is_enabled(&self) -> bool { self.enabled }
    fn set_enabled(&mut self, enabled: bool) { self.enabled = enabled; }
}
