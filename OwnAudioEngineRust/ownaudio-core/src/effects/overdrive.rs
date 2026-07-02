//! Asymmetric tube-style overdrive.
//!
//! Faithful Rust port of the reference C# `OwnaudioNET.Effects.OverdriveEffect`
//! DSP: every sample is driven, passed through an asymmetric tanh tube
//! saturation, shaped by a stateful tone control (a one-pole low-pass minus a
//! scaled one-pole high-pass), attenuated by the output level, then blended
//! back with the dry signal.  Parameter identifiers, ranges and defaults mirror
//! the C# effect so the two implementations are numerically equivalent (the
//! basis of the 2.2 reference comparison).

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};
use crate::smoothing::{RampedParam, DEFAULT_SMOOTH_MS};

/// Param ID 2 — input gain (1.0 … 5.0). Controls the amount of overdrive.
pub const PARAM_GAIN: u32 = 2;
/// Param ID 3 — tone control (0.0 = dark … 1.0 = bright).
pub const PARAM_TONE: u32 = 3;
/// Param ID 4 — output level (0.1 … 1.0).
pub const PARAM_OUTPUT_LEVEL: u32 = 4;

/// Mix values below this threshold bypass processing entirely (mirrors C#).
const MIX_BYPASS_THRESHOLD: f32 = 0.001;

/// Asymmetric tube overdrive effect.
pub struct Overdrive {
    enabled: bool,
    mix: f32,
    gain: f32,
    tone: f32,
    output_level: f32,
    low_pass_state: f32,
    high_pass_state: f32,
    mix_ramp: RampedParam,
}

impl Overdrive {
    /// Creates a new [`Overdrive`] with the reference default parameters
    /// (gain 2.0, tone 0.5, mix 1.0, output level 0.7).
    pub fn new(sample_rate: f32) -> Self {
        Self {
            enabled: true,
            mix: 1.0,
            gain: 2.0,
            tone: 0.5,
            output_level: 0.7,
            low_pass_state: 0.0,
            high_pass_state: 0.0,
            mix_ramp: RampedParam::new(1.0, sample_rate, DEFAULT_SMOOTH_MS),
        }
    }

    /// Asymmetric tube saturation.
    ///
    /// Positive and negative half-waves are shaped differently (a softer, hotter
    /// curve for the positive side), giving the even-harmonic character of tube
    /// breakup.  The `tanh` is evaluated in f64 to match the reference C#
    /// `Math.Tanh` before the result is truncated back to f32.
    #[inline]
    fn tube_saturation(input: f32) -> f32 {
        let x = input as f64;
        if input >= 0.0 {
            ((x * 0.7).tanh() * 1.2) as f32
        } else {
            ((x * 0.9).tanh() * 0.9) as f32
        }
    }

    /// Stateful tone control: a one-pole low-pass minus a tone-scaled one-pole
    /// high-pass.  Advances the two filter states; matches the reference C#
    /// `ApplyToneControl`.
    #[inline]
    fn apply_tone_control(&mut self, input: f32) -> f32 {
        let low_pass_cutoff = 0.1 + self.tone * 0.4;
        let high_pass_cutoff = 0.05 + (1.0 - self.tone) * 0.2;

        self.low_pass_state += low_pass_cutoff * (input - self.low_pass_state);
        self.high_pass_state += high_pass_cutoff * (input - self.high_pass_state);

        self.low_pass_state - self.high_pass_state * (1.0 - self.tone)
    }
}

impl Effect for Overdrive {
    fn effect_type(&self) -> EffectType {
        EffectType::Overdrive
    }

    fn process(&mut self, buffer: &mut [f32], _channels: u16) {
        self.mix_ramp.begin_block();
        if !self.enabled
            || (self.mix < MIX_BYPASS_THRESHOLD && self.mix_ramp.current() < MIX_BYPASS_THRESHOLD)
        {
            return;
        }

        for sample in buffer.iter_mut() {
            let mix = self.mix_ramp.advance();
            let input = *sample;
            let gained = input * self.gain;
            let mut overdriven = Self::tube_saturation(gained);
            overdriven = self.apply_tone_control(overdriven);
            overdriven *= self.output_level;
            *sample = input * (1.0 - mix) + overdriven * mix;
        }
    }

    fn set_param(&mut self, param_id: u32, value: f32) -> bool {
        match param_id {
            PARAM_ENABLED => {
                self.enabled = value >= 0.5;
                true
            }
            PARAM_MIX => {
                self.mix = value.clamp(0.0, 1.0);
                self.mix_ramp.set(self.mix);
                true
            }
            PARAM_GAIN => {
                self.gain = value.clamp(1.0, 5.0);
                true
            }
            PARAM_TONE => {
                self.tone = value.clamp(0.0, 1.0);
                true
            }
            PARAM_OUTPUT_LEVEL => {
                self.output_level = value.clamp(0.1, 1.0);
                true
            }
            _ => false,
        }
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        match param_id {
            PARAM_ENABLED => Some(if self.enabled { 1.0 } else { 0.0 }),
            PARAM_MIX => Some(self.mix),
            PARAM_GAIN => Some(self.gain),
            PARAM_TONE => Some(self.tone),
            PARAM_OUTPUT_LEVEL => Some(self.output_level),
            _ => None,
        }
    }

    fn reset(&mut self) {
        self.low_pass_state = 0.0;
        self.high_pass_state = 0.0;
        self.mix_ramp.reset(self.mix);
    }

    fn is_enabled(&self) -> bool {
        self.enabled
    }

    fn set_enabled(&mut self, enabled: bool) {
        self.enabled = enabled;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// f64 ground-truth transcription of the reference C# DSP, carrying the same
    /// two filter states, used to measure the production f32 implementation's
    /// numerical fidelity.
    struct Reference {
        gain: f64,
        tone: f64,
        mix: f64,
        output_level: f64,
        low_pass_state: f64,
        high_pass_state: f64,
    }

    impl Reference {
        fn tube_saturation(input: f64) -> f64 {
            if input >= 0.0 {
                (input * 0.7).tanh() * 1.2
            } else {
                (input * 0.9).tanh() * 0.9
            }
        }

        fn apply_tone_control(&mut self, input: f64) -> f64 {
            let low_pass_cutoff = 0.1 + self.tone * 0.4;
            let high_pass_cutoff = 0.05 + (1.0 - self.tone) * 0.2;
            self.low_pass_state += low_pass_cutoff * (input - self.low_pass_state);
            self.high_pass_state += high_pass_cutoff * (input - self.high_pass_state);
            self.low_pass_state - self.high_pass_state * (1.0 - self.tone)
        }

        fn process(&mut self, input: &[f32]) -> Vec<f32> {
            input
                .iter()
                .map(|&x| {
                    let x = x as f64;
                    let gained = x * self.gain;
                    let mut overdriven = Self::tube_saturation(gained);
                    overdriven = self.apply_tone_control(overdriven);
                    overdriven *= self.output_level;
                    (x * (1.0 - self.mix) + overdriven * self.mix) as f32
                })
                .collect()
        }
    }

    /// Root-mean-square error between two signals expressed in decibels.
    /// Returns negative infinity for a perfect match.
    fn rms_error_db(a: &[f32], b: &[f32]) -> f64 {
        assert_eq!(a.len(), b.len());
        let mut sum_sq = 0.0f64;
        for (x, y) in a.iter().zip(b.iter()) {
            let d = *x as f64 - *y as f64;
            sum_sq += d * d;
        }
        let rms = (sum_sq / a.len() as f64).sqrt();
        if rms == 0.0 {
            f64::NEG_INFINITY
        } else {
            20.0 * rms.log10()
        }
    }

    fn sine_sweep(len: usize) -> Vec<f32> {
        (0..len)
            .map(|i| {
                let t = i as f32 / 48_000.0;
                // A moderately loud tone that drives both saturation half-waves.
                0.7 * (2.0 * std::f32::consts::PI * (220.0 + 4.0 * i as f32 / len as f32) * t).sin()
            })
            .collect()
    }

    #[test]
    fn defaults_match_reference() {
        let o = Overdrive::new(48_000.0);
        assert_eq!(o.get_param(PARAM_GAIN), Some(2.0));
        assert_eq!(o.get_param(PARAM_TONE), Some(0.5));
        assert_eq!(o.get_param(PARAM_OUTPUT_LEVEL), Some(0.7));
        assert_eq!(o.get_param(PARAM_MIX), Some(1.0));
        assert!(o.is_enabled());
    }

    #[test]
    fn params_clamp_to_reference_ranges() {
        let mut o = Overdrive::new(48_000.0);
        o.set_param(PARAM_GAIN, 100.0);
        assert_eq!(o.get_param(PARAM_GAIN), Some(5.0));
        o.set_param(PARAM_GAIN, 0.0);
        assert_eq!(o.get_param(PARAM_GAIN), Some(1.0));
        o.set_param(PARAM_TONE, 5.0);
        assert_eq!(o.get_param(PARAM_TONE), Some(1.0));
        o.set_param(PARAM_TONE, -1.0);
        assert_eq!(o.get_param(PARAM_TONE), Some(0.0));
        o.set_param(PARAM_OUTPUT_LEVEL, 5.0);
        assert_eq!(o.get_param(PARAM_OUTPUT_LEVEL), Some(1.0));
        o.set_param(PARAM_OUTPUT_LEVEL, -1.0);
        assert_eq!(o.get_param(PARAM_OUTPUT_LEVEL), Some(0.1));
        o.set_param(PARAM_MIX, 2.0);
        assert_eq!(o.get_param(PARAM_MIX), Some(1.0));
    }

    #[test]
    fn unknown_param_is_rejected() {
        let mut o = Overdrive::new(48_000.0);
        assert!(!o.set_param(999, 1.0));
        assert_eq!(o.get_param(999), None);
    }

    #[test]
    fn disabled_effect_passes_signal_through_untouched() {
        let mut o = Overdrive::new(48_000.0);
        o.set_enabled(false);
        let input = sine_sweep(256);
        let mut buf = input.clone();
        o.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn zero_mix_passes_signal_through_untouched() {
        let mut o = Overdrive::new(48_000.0);
        o.set_param(PARAM_MIX, 0.0);
        let input = sine_sweep(256);
        let mut buf = input.clone();
        o.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn tube_saturation_is_bounded() {
        // tanh is bounded by ±1; after the per-side scaling the saturated
        // output stays within ±1.2 regardless of how hard the input is driven.
        let mut o = Overdrive::new(48_000.0);
        o.set_param(PARAM_GAIN, 5.0);
        o.set_param(PARAM_OUTPUT_LEVEL, 1.0);
        o.set_param(PARAM_TONE, 1.0);
        let mut buf = vec![5.0f32, -5.0, 100.0, -100.0];
        o.process(&mut buf, 1);
        for &s in &buf {
            assert!(s.abs() <= 1.5, "sample {s} exceeded the saturation bound");
        }
    }

    #[test]
    fn output_is_finite_and_length_preserved() {
        let mut o = Overdrive::new(48_000.0);
        let input = sine_sweep(512);
        let mut buf = input.clone();
        o.process(&mut buf, 2);
        assert_eq!(buf.len(), input.len());
        assert!(buf.iter().all(|s| s.is_finite()));
    }

    #[test]
    fn matches_reference_dsp_within_minus_60_db() {
        // 2.2 acceptance: the production f32 DSP must reproduce the reference
        // (f64 ground truth, transcribed from the C# algorithm, carrying the
        // same filter states) to better than -60 dB RMS error across the
        // parameter space.
        let input = sine_sweep(4_096);
        for &(gain, tone, mix, level) in &[
            (2.0f32, 0.5f32, 1.0f32, 0.7f32),
            (4.2, 0.8, 1.0, 0.65),
            (1.3, 0.6, 0.7, 0.9),
            (5.0, 0.25, 0.8, 0.85),
        ] {
            let mut o = Overdrive::new(48_000.0);
            o.set_param(PARAM_GAIN, gain);
            o.set_param(PARAM_TONE, tone);
            o.set_param(PARAM_MIX, mix);
            o.set_param(PARAM_OUTPUT_LEVEL, level);

            let mut produced = input.clone();
            o.process(&mut produced, 2);

            let mut reference = Reference {
                gain: gain as f64,
                tone: tone as f64,
                mix: mix as f64,
                output_level: level as f64,
                low_pass_state: 0.0,
                high_pass_state: 0.0,
            };
            let expected = reference.process(&input);
            let err = rms_error_db(&produced, &expected);
            assert!(
                err < -60.0,
                "gain={gain} tone={tone} mix={mix} level={level}: RMS error {err:.1} dB exceeds -60 dB"
            );
        }
    }

    #[test]
    fn reset_clears_filter_state() {
        // The tone control is stateful, so reset must return the effect to its
        // initial response: processing the same input after a reset reproduces
        // the first run exactly.
        let mut o = Overdrive::new(48_000.0);
        let input = sine_sweep(256);
        let mut first = input.clone();
        o.process(&mut first, 2);
        o.reset();
        let mut second = input.clone();
        o.process(&mut second, 2);
        assert_eq!(first, second);
    }
}
