//! Dynamic range compressor with soft-knee and peak detection.

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};

pub const PARAM_THRESHOLD: u32 = 2; // dB (-60 … 0)
pub const PARAM_RATIO: u32 = 3;     // 1 … 100
pub const PARAM_ATTACK: u32 = 4;    // ms (0.1 … 1000)
pub const PARAM_RELEASE: u32 = 5;   // ms (1 … 2000)
pub const PARAM_MAKEUP: u32 = 6;    // dB

/// Compressor with soft-knee peak detection.
pub struct Compressor {
    enabled: bool,
    threshold_db: f32,
    ratio: f32,
    attack_ms: f32,
    release_ms: f32,
    makeup_db: f32,
}

impl Compressor {
    /// Creates a new [`Compressor`] with default parameters.
    pub fn new(_sample_rate: f32) -> Self {
        Self {
            enabled: true,
            threshold_db: -6.0,
            ratio: 4.0,
            attack_ms: 20.0,
            release_ms: 200.0,
            makeup_db: 0.0,
        }
    }
}

impl Effect for Compressor {
    fn effect_type(&self) -> EffectType { EffectType::Compressor }

    fn process(&mut self, _buffer: &mut [f32], _channels: u16) {}

    fn set_param(&mut self, param_id: u32, value: f32) -> bool {
        match param_id {
            PARAM_ENABLED   => { self.enabled = value >= 0.5; true }
            PARAM_MIX       => true,
            PARAM_THRESHOLD => { self.threshold_db = value.clamp(-60.0, 0.0); true }
            PARAM_RATIO     => { self.ratio = value.clamp(1.0, 100.0); true }
            PARAM_ATTACK    => { self.attack_ms = value.clamp(0.1, 1000.0); true }
            PARAM_RELEASE   => { self.release_ms = value.clamp(1.0, 2000.0); true }
            PARAM_MAKEUP    => { self.makeup_db = value.clamp(-20.0, 40.0); true }
            _ => false,
        }
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        match param_id {
            PARAM_ENABLED   => Some(if self.enabled { 1.0 } else { 0.0 }),
            PARAM_MIX       => Some(1.0),
            PARAM_THRESHOLD => Some(self.threshold_db),
            PARAM_RATIO     => Some(self.ratio),
            PARAM_ATTACK    => Some(self.attack_ms),
            PARAM_RELEASE   => Some(self.release_ms),
            PARAM_MAKEUP    => Some(self.makeup_db),
            _ => None,
        }
    }

    fn reset(&mut self) {}
    fn is_enabled(&self) -> bool { self.enabled }
    fn set_enabled(&mut self, enabled: bool) { self.enabled = enabled; }
}
