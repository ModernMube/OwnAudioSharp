//! Freeverb-based algorithmic reverb.

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};

/// Reverb-specific parameter IDs.
pub const PARAM_ROOM_SIZE: u32 = 2;
pub const PARAM_DAMPING: u32 = 3;
pub const PARAM_WIDTH: u32 = 4;
pub const PARAM_WET_LEVEL: u32 = 5;
pub const PARAM_DRY_LEVEL: u32 = 6;

/// Freeverb algorithmic reverb effect.
pub struct Reverb {
    enabled: bool,
    mix: f32,
    room_size: f32,
    damping: f32,
    width: f32,
    wet_level: f32,
    dry_level: f32,
}

impl Reverb {
    /// Creates a new [`Reverb`] with default parameters.
    pub fn new(_sample_rate: f32) -> Self {
        Self {
            enabled: true,
            mix: 0.5,
            room_size: 0.5,
            damping: 0.5,
            width: 1.0,
            wet_level: 0.33,
            dry_level: 0.67,
        }
    }
}

impl Effect for Reverb {
    fn effect_type(&self) -> EffectType { EffectType::Reverb }

    fn process(&mut self, _buffer: &mut [f32], _channels: u16) {}

    fn set_param(&mut self, param_id: u32, value: f32) -> bool {
        match param_id {
            PARAM_ENABLED  => { self.enabled = value >= 0.5; true }
            PARAM_MIX      => { self.mix = value.clamp(0.0, 1.0); true }
            PARAM_ROOM_SIZE => { self.room_size = value.clamp(0.0, 1.0); true }
            PARAM_DAMPING  => { self.damping = value.clamp(0.0, 1.0); true }
            PARAM_WIDTH    => { self.width = value.clamp(0.0, 2.0); true }
            PARAM_WET_LEVEL => { self.wet_level = value.clamp(0.0, 1.0); true }
            PARAM_DRY_LEVEL => { self.dry_level = value.clamp(0.0, 1.0); true }
            _ => false,
        }
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        match param_id {
            PARAM_ENABLED   => Some(if self.enabled { 1.0 } else { 0.0 }),
            PARAM_MIX       => Some(self.mix),
            PARAM_ROOM_SIZE => Some(self.room_size),
            PARAM_DAMPING   => Some(self.damping),
            PARAM_WIDTH     => Some(self.width),
            PARAM_WET_LEVEL => Some(self.wet_level),
            PARAM_DRY_LEVEL => Some(self.dry_level),
            _ => None,
        }
    }

    fn reset(&mut self) {}
    fn is_enabled(&self) -> bool { self.enabled }
    fn set_enabled(&mut self, enabled: bool) { self.enabled = enabled; }
}
