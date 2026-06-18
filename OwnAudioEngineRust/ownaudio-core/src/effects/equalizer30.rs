//! 30-band parametric equalizer (ISO 1/3-octave, biquad peaking).

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};

/// First band gain parameter ID; bands occupy `PARAM_BAND_0 ..= PARAM_BAND_0 + 29`.
///
/// ISO 1/3-octave centre frequencies (Hz):
/// 20, 25, 31.5, 40, 50, 63, 80, 100, 125, 160,
/// 200, 250, 315, 400, 500, 630, 800, 1 k, 1.25 k, 1.6 k,
/// 2 k, 2.5 k, 3.15 k, 4 k, 5 k, 6.3 k, 8 k, 10 k, 12.5 k, 16 k.
pub const PARAM_BAND_0: u32 = 2;
pub const PARAM_BAND_29: u32 = 31;

const BANDS: usize = 30;

/// 30-band 1/3-octave parametric equalizer.
pub struct Equalizer30 {
    enabled: bool,
    mix: f32,
    gains_db: [f32; BANDS],
}

impl Equalizer30 {
    /// Creates a new flat [`Equalizer30`].
    pub fn new(_sample_rate: f32) -> Self {
        Self {
            enabled: true,
            mix: 1.0,
            gains_db: [0.0; BANDS],
        }
    }
}

impl Effect for Equalizer30 {
    fn effect_type(&self) -> EffectType { EffectType::Equalizer30 }

    fn process(&mut self, _buffer: &mut [f32], _channels: u16) {}

    fn set_param(&mut self, param_id: u32, value: f32) -> bool {
        match param_id {
            PARAM_ENABLED => { self.enabled = value >= 0.5; true }
            PARAM_MIX     => { self.mix = value.clamp(0.0, 1.0); true }
            PARAM_BAND_0..=PARAM_BAND_29 => {
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
            PARAM_BAND_0..=PARAM_BAND_29 => {
                Some(self.gains_db[(param_id - PARAM_BAND_0) as usize])
            }
            _ => None,
        }
    }

    fn reset(&mut self) {}
    fn is_enabled(&self) -> bool { self.enabled }
    fn set_enabled(&mut self, enabled: bool) { self.enabled = enabled; }
}
