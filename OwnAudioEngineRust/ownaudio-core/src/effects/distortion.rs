//! Soft-clipping distortion.
//!
//! Faithful Rust port of the reference C# `OwnaudioNET.Effects.DistortionEffect`
//! DSP: every sample is driven, passed through a rational soft-clip waveshaper,
//! attenuated by an output-gain compensation, then blended back with the dry
//! signal.  Parameter identifiers, ranges and defaults mirror the C# effect so
//! the two implementations are numerically equivalent (the basis of the 2.2
//! reference comparison).

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};

/// Param ID 2 — drive amount (1.0 … 10.0). Higher values create more distortion.
pub const PARAM_DRIVE: u32 = 2;
/// Param ID 3 — output gain compensation (0.1 … 1.0).
pub const PARAM_OUTPUT_GAIN: u32 = 3;

/// Mix values below this threshold bypass processing entirely (mirrors C#).
const MIX_BYPASS_THRESHOLD: f32 = 0.001;

/// Soft-clipping distortion effect.
pub struct Distortion {
    enabled: bool,
    mix: f32,
    drive: f32,
    output_gain: f32,
}

impl Distortion {
    /// Creates a new [`Distortion`] with the reference default parameters
    /// (drive 2.0, mix 1.0, output gain 0.5).
    pub fn new(_sample_rate: f32) -> Self {
        Self {
            enabled: true,
            mix: 1.0,
            drive: 2.0,
            output_gain: 0.5,
        }
    }

    /// Rational soft-clip waveshaper.
    ///
    /// Linear within the unit interval; outside it the magnitude is smoothly
    /// compressed toward an asymptote of 2.0, preserving sign.  Matches the
    /// reference C# `SoftClip`.
    #[inline]
    fn soft_clip(input: f32) -> f32 {
        let mag = input.abs();
        if mag <= 1.0 {
            input
        } else {
            input.signum() * (2.0 - 2.0 / (mag + 1.0))
        }
    }
}

impl Effect for Distortion {
    fn effect_type(&self) -> EffectType {
        EffectType::Distortion
    }

    fn process(&mut self, buffer: &mut [f32], _channels: u16) {
        if !self.enabled || self.mix < MIX_BYPASS_THRESHOLD {
            return;
        }

        let dry = 1.0 - self.mix;
        for sample in buffer.iter_mut() {
            let input = *sample;
            let distorted = Self::soft_clip(input * self.drive) * self.output_gain;
            *sample = input * dry + distorted * self.mix;
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
                true
            }
            PARAM_DRIVE => {
                self.drive = value.clamp(1.0, 10.0);
                true
            }
            PARAM_OUTPUT_GAIN => {
                self.output_gain = value.clamp(0.1, 1.0);
                true
            }
            _ => false,
        }
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        match param_id {
            PARAM_ENABLED => Some(if self.enabled { 1.0 } else { 0.0 }),
            PARAM_MIX => Some(self.mix),
            PARAM_DRIVE => Some(self.drive),
            PARAM_OUTPUT_GAIN => Some(self.output_gain),
            _ => None,
        }
    }

    fn reset(&mut self) {}

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

    /// f64 ground-truth transcription of the reference C# DSP, used to measure
    /// the production f32 implementation's numerical fidelity.
    fn reference_soft_clip(input: f64) -> f64 {
        let mag = input.abs();
        if mag <= 1.0 {
            input
        } else {
            input.signum() * (2.0 - 2.0 / (mag + 1.0))
        }
    }

    fn reference_process(input: &[f32], drive: f64, mix: f64, output_gain: f64) -> Vec<f32> {
        input
            .iter()
            .map(|&x| {
                let x = x as f64;
                let distorted = reference_soft_clip(x * drive) * output_gain;
                (x * (1.0 - mix) + distorted * mix) as f32
            })
            .collect()
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
                // A loud, slowly rising tone that pushes the waveshaper past unity.
                1.8 * (2.0 * std::f32::consts::PI * (220.0 + 4.0 * i as f32 / len as f32) * t).sin()
            })
            .collect()
    }

    #[test]
    fn defaults_match_reference() {
        let d = Distortion::new(48_000.0);
        assert_eq!(d.get_param(PARAM_DRIVE), Some(2.0));
        assert_eq!(d.get_param(PARAM_OUTPUT_GAIN), Some(0.5));
        assert_eq!(d.get_param(PARAM_MIX), Some(1.0));
        assert!(d.is_enabled());
    }

    #[test]
    fn params_clamp_to_reference_ranges() {
        let mut d = Distortion::new(48_000.0);
        d.set_param(PARAM_DRIVE, 100.0);
        assert_eq!(d.get_param(PARAM_DRIVE), Some(10.0));
        d.set_param(PARAM_DRIVE, 0.0);
        assert_eq!(d.get_param(PARAM_DRIVE), Some(1.0));
        d.set_param(PARAM_OUTPUT_GAIN, 5.0);
        assert_eq!(d.get_param(PARAM_OUTPUT_GAIN), Some(1.0));
        d.set_param(PARAM_OUTPUT_GAIN, -1.0);
        assert_eq!(d.get_param(PARAM_OUTPUT_GAIN), Some(0.1));
        d.set_param(PARAM_MIX, 2.0);
        assert_eq!(d.get_param(PARAM_MIX), Some(1.0));
    }

    #[test]
    fn unknown_param_is_rejected() {
        let mut d = Distortion::new(48_000.0);
        assert!(!d.set_param(999, 1.0));
        assert_eq!(d.get_param(999), None);
    }

    #[test]
    fn disabled_effect_passes_signal_through_untouched() {
        let mut d = Distortion::new(48_000.0);
        d.set_enabled(false);
        let input = sine_sweep(256);
        let mut buf = input.clone();
        d.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn zero_mix_passes_signal_through_untouched() {
        let mut d = Distortion::new(48_000.0);
        d.set_param(PARAM_MIX, 0.0);
        let input = sine_sweep(256);
        let mut buf = input.clone();
        d.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn soft_clip_bounds_loud_input() {
        // The waveshaper asymptote is 2.0; after output gain the result stays
        // well bounded regardless of how hard the input is driven.
        let mut d = Distortion::new(48_000.0);
        d.set_param(PARAM_DRIVE, 10.0);
        d.set_param(PARAM_OUTPUT_GAIN, 1.0);
        let mut buf = vec![5.0f32, -5.0, 100.0, -100.0];
        d.process(&mut buf, 1);
        for &s in &buf {
            assert!(s.abs() <= 2.0, "sample {s} exceeded the soft-clip asymptote");
        }
    }

    #[test]
    fn output_is_finite_and_length_preserved() {
        let mut d = Distortion::new(48_000.0);
        let input = sine_sweep(512);
        let mut buf = input.clone();
        d.process(&mut buf, 2);
        assert_eq!(buf.len(), input.len());
        assert!(buf.iter().all(|s| s.is_finite()));
    }

    #[test]
    fn matches_reference_dsp_within_minus_60_db() {
        // 2.2 acceptance: the production f32 DSP must reproduce the reference
        // (f64 ground truth, transcribed from the C# algorithm) to better than
        // -60 dB RMS error across the parameter space.
        let input = sine_sweep(4_096);
        for &(drive, mix, gain) in &[
            (2.0f32, 1.0f32, 0.5f32),
            (6.5, 0.9, 0.4),
            (1.4, 0.4, 0.9),
            (10.0, 1.0, 0.1),
        ] {
            let mut d = Distortion::new(48_000.0);
            d.set_param(PARAM_DRIVE, drive);
            d.set_param(PARAM_MIX, mix);
            d.set_param(PARAM_OUTPUT_GAIN, gain);

            let mut produced = input.clone();
            d.process(&mut produced, 2);

            let reference =
                reference_process(&input, drive as f64, mix as f64, gain as f64);
            let err = rms_error_db(&produced, &reference);
            assert!(
                err < -60.0,
                "drive={drive} mix={mix} gain={gain}: RMS error {err:.1} dB exceeds -60 dB"
            );
        }
    }

    #[test]
    fn reset_is_a_no_op_for_stateless_effect() {
        let mut d = Distortion::new(48_000.0);
        let input = sine_sweep(128);
        let mut first = input.clone();
        d.process(&mut first, 2);
        d.reset();
        let mut second = input.clone();
        d.process(&mut second, 2);
        assert_eq!(first, second);
    }
}
