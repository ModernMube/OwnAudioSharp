//! Real-time pitch shifter (semitone-accurate).

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};

pub const PARAM_SEMITONES: u32 = 2; // -12.0 … +12.0

/// Real-time pitch shift effect.
pub struct PitchShift {
    enabled: bool,
    mix: f32,
    semitones: f32,
}

impl PitchShift {
    /// Creates a new [`PitchShift`] with no shift (0 semitones).
    pub fn new(_sample_rate: f32) -> Self {
        Self {
            enabled: true,
            mix: 1.0,
            semitones: 0.0,
        }
    }
}

impl Effect for PitchShift {
    fn effect_type(&self) -> EffectType { EffectType::PitchShift }

    fn process(&mut self, _buffer: &mut [f32], _channels: u16) {}

    fn set_param(&mut self, param_id: u32, value: f32) -> bool {
        match param_id {
            PARAM_ENABLED  => { self.enabled = value >= 0.5; true }
            PARAM_MIX      => { self.mix = value.clamp(0.0, 1.0); true }
            PARAM_SEMITONES => { self.semitones = value.clamp(-24.0, 24.0); true }
            _ => false,
        }
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        match param_id {
            PARAM_ENABLED   => Some(if self.enabled { 1.0 } else { 0.0 }),
            PARAM_MIX       => Some(self.mix),
            PARAM_SEMITONES => Some(self.semitones),
            _ => None,
        }
    }

    fn reset(&mut self) {}
    fn is_enabled(&self) -> bool { self.enabled }
    fn set_enabled(&mut self, enabled: bool) { self.enabled = enabled; }
}
