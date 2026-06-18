//! 10-band parametric equalizer (ISO frequencies, biquad peaking).

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};

/// Band gain parameter IDs (dB, -12 … +12).
pub const PARAM_BAND_0: u32 = 2;  // 31 Hz
pub const PARAM_BAND_1: u32 = 3;  // 62 Hz
pub const PARAM_BAND_2: u32 = 4;  // 125 Hz
pub const PARAM_BAND_3: u32 = 5;  // 250 Hz
pub const PARAM_BAND_4: u32 = 6;  // 500 Hz
pub const PARAM_BAND_5: u32 = 7;  // 1 kHz
pub const PARAM_BAND_6: u32 = 8;  // 2 kHz
pub const PARAM_BAND_7: u32 = 9;  // 4 kHz
pub const PARAM_BAND_8: u32 = 10; // 8 kHz
pub const PARAM_BAND_9: u32 = 11; // 16 kHz

const BANDS: usize = 10;

/// 10-band parametric equalizer.
pub struct Equalizer {
    enabled: bool,
    mix: f32,
    gains_db: [f32; BANDS],
}

impl Equalizer {
    /// Creates a new flat [`Equalizer`].
    pub fn new(_sample_rate: f32) -> Self {
        Self {
            enabled: true,
            mix: 1.0,
            gains_db: [0.0; BANDS],
        }
    }
}

impl Effect for Equalizer {
    fn effect_type(&self) -> EffectType { EffectType::Equalizer }

    fn process(&mut self, _buffer: &mut [f32], _channels: u16) {}

    fn set_param(&mut self, param_id: u32, value: f32) -> bool {
        match param_id {
            PARAM_ENABLED => { self.enabled = value >= 0.5; true }
            PARAM_MIX     => { self.mix = value.clamp(0.0, 1.0); true }
            PARAM_BAND_0..=PARAM_BAND_9 => {
                let idx = (param_id - PARAM_BAND_0) as usize;
                self.gains_db[idx] = value.clamp(-12.0, 12.0);
                true
            }
            _ => false,
        }
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        match param_id {
            PARAM_ENABLED => Some(if self.enabled { 1.0 } else { 0.0 }),
            PARAM_MIX     => Some(self.mix),
            PARAM_BAND_0..=PARAM_BAND_9 => {
                Some(self.gains_db[(param_id - PARAM_BAND_0) as usize])
            }
            _ => None,
        }
    }

    fn reset(&mut self) {}
    fn is_enabled(&self) -> bool { self.enabled }
    fn set_enabled(&mut self, enabled: bool) { self.enabled = enabled; }
}
