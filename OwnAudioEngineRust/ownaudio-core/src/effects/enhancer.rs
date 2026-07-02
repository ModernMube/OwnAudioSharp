//! Harmonic exciter / spectral enhancer.
//!
//! Faithful Rust port of the reference C# `OwnaudioNET.Effects.EnhancerEffect`: a
//! one-pole high-pass (`y = alpha·(y_prev + x − x_prev)`, `alpha = RC/(RC+dt)`)
//! isolates the upper band, which is amplified, softly saturated through a
//! `tanh` curve and added back to the dry signal scaled by the mix.  A single
//! filter state is shared across the interleaved samples exactly as the C#
//! effect walks them.  Parameter identifiers, ranges, defaults and the
//! per-sample DSP mirror the C# effect so the two are numerically equivalent
//! (the basis of the 2.2 reference comparison).
//!
//! The high-pass output `y_prev` is recursive, so it is denormal-flushed (2.12):
//! a decaying tail would otherwise park in the subnormal range and stall the CPU
//! on the audio thread.

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};
use crate::denormal;
use crate::smoothing::{RampedParam, DEFAULT_SMOOTH_MS};

/// Param ID 2 — pre-saturation gain (0.1 … 10.0).
pub const PARAM_GAIN: u32 = 2;
/// Param ID 3 — high-pass cutoff frequency in Hz (100 … 20000).
pub const PARAM_CUTOFF: u32 = 3;

/// Mix values below this threshold bypass processing entirely (mirrors C#).
const MIX_BYPASS_THRESHOLD: f32 = 0.001;

/// Harmonic enhancer effect.
pub struct Enhancer {
    enabled: bool,
    mix: f32,
    gain: f32,
    cut_freq: f32,
    sample_rate: f32,
    alpha: f32,
    x_prev: f32,
    y_prev: f32,
    mix_ramp: RampedParam,
}

impl Enhancer {
    /// Creates a new [`Enhancer`] for `sample_rate`, with the reference
    /// constructor defaults (mix 0.2, cutoff 4000 Hz, gain 2.5).
    pub fn new(sample_rate: f32) -> Self {
        let sample_rate = sample_rate.clamp(8000.0, 192_000.0);
        let mut enhancer = Self {
            enabled: true,
            mix: 0.2,
            gain: 2.5,
            cut_freq: 4000.0,
            sample_rate,
            alpha: 0.0,
            x_prev: 0.0,
            y_prev: 0.0,
            mix_ramp: RampedParam::new(0.2, sample_rate, DEFAULT_SMOOTH_MS),
        };
        enhancer.update_filter_coefficient();
        enhancer
    }

    /// Recomputes the one-pole high-pass coefficient from the cutoff and sample
    /// rate; mirrors the reference `UpdateFilterCoefficient`.
    fn update_filter_coefficient(&mut self) {
        if self.cut_freq > 0.0 && self.sample_rate > 0.0 {
            let rc = 1.0 / (2.0 * std::f32::consts::PI * self.cut_freq);
            let dt = 1.0 / self.sample_rate;
            self.alpha = rc / (rc + dt);
        }
    }
}

impl Effect for Enhancer {
    fn effect_type(&self) -> EffectType {
        EffectType::Enhancer
    }

    fn process(&mut self, buffer: &mut [f32], _channels: u16) {
        self.mix_ramp.begin_block();
        if !self.enabled
            || (self.mix < MIX_BYPASS_THRESHOLD && self.mix_ramp.current() < MIX_BYPASS_THRESHOLD)
        {
            return;
        }

        let alpha = self.alpha;
        let gain = self.gain;

        let mut x_prev = self.x_prev;
        let mut y_prev = self.y_prev;

        for sample in buffer.iter_mut() {
            let mix = self.mix_ramp.advance();
            let original = *sample;

            let high_freq = denormal::flush(alpha * (y_prev + original - x_prev));
            x_prev = original;
            y_prev = high_freq;

            let processed = (high_freq * gain * 0.5).tanh() * 2.0;

            *sample = original + processed * mix;
        }

        self.x_prev = x_prev;
        self.y_prev = y_prev;
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
                self.gain = value.clamp(0.1, 10.0);
                true
            }
            PARAM_CUTOFF => {
                self.cut_freq = value.clamp(100.0, 20000.0);
                self.update_filter_coefficient();
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
            PARAM_CUTOFF => Some(self.cut_freq),
            _ => None,
        }
    }

    fn reset(&mut self) {
        self.x_prev = 0.0;
        self.y_prev = 0.0;
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

    /// f64 ground-truth transcription of the reference C# enhancer, carrying the
    /// full high-pass state, used to measure the production f32 implementation's
    /// numerical fidelity.  The DSP is smooth (one-pole high-pass + `tanh`), so a
    /// straight f64 transcription tracks the f32 production to well within the
    /// -60 dB bound.
    struct Reference {
        alpha: f64,
        gain: f64,
        mix: f64,
        x_prev: f64,
        y_prev: f64,
    }

    impl Reference {
        fn new(sample_rate: f64, cut_freq: f64, gain: f64, mix: f64) -> Self {
            let rc = 1.0 / (2.0 * std::f64::consts::PI * cut_freq);
            let dt = 1.0 / sample_rate;
            Self {
                alpha: rc / (rc + dt),
                gain,
                mix,
                x_prev: 0.0,
                y_prev: 0.0,
            }
        }

        fn process(&mut self, buffer: &[f32]) -> Vec<f32> {
            let mut out = vec![0.0f32; buffer.len()];
            for (o, &x) in out.iter_mut().zip(buffer.iter()) {
                let original = x as f64;
                let high_freq = self.alpha * (self.y_prev + original - self.x_prev);
                self.x_prev = original;
                self.y_prev = high_freq;
                let processed = (high_freq * self.gain * 0.5).tanh() * 2.0;
                *o = (original + processed * self.mix) as f32;
            }
            out
        }
    }

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

    fn stereo_pluck(frames: usize) -> Vec<f32> {
        let mut v = vec![0.0f32; frames * 2];
        for f in 0..frames {
            let t = f as f32 / 48_000.0;
            let env = (-2.0 * t).exp();
            let s = 0.6 * env * (2.0 * std::f32::consts::PI * 1000.0 * t).sin();
            v[f * 2] = s;
            v[f * 2 + 1] = s * 0.8;
        }
        v
    }

    #[test]
    fn defaults_match_reference() {
        let e = Enhancer::new(48_000.0);
        assert_eq!(e.get_param(PARAM_MIX), Some(0.2));
        assert_eq!(e.get_param(PARAM_GAIN), Some(2.5));
        assert_eq!(e.get_param(PARAM_CUTOFF), Some(4000.0));
        assert!(e.is_enabled());
    }

    #[test]
    fn params_clamp_to_reference_ranges() {
        let mut e = Enhancer::new(48_000.0);
        e.set_param(PARAM_GAIN, 100.0);
        assert_eq!(e.get_param(PARAM_GAIN), Some(10.0));
        e.set_param(PARAM_GAIN, 0.0);
        assert_eq!(e.get_param(PARAM_GAIN), Some(0.1));
        e.set_param(PARAM_CUTOFF, 100_000.0);
        assert_eq!(e.get_param(PARAM_CUTOFF), Some(20000.0));
        e.set_param(PARAM_CUTOFF, 0.0);
        assert_eq!(e.get_param(PARAM_CUTOFF), Some(100.0));
        e.set_param(PARAM_MIX, 2.0);
        assert_eq!(e.get_param(PARAM_MIX), Some(1.0));
    }

    #[test]
    fn unknown_param_is_rejected() {
        let mut e = Enhancer::new(48_000.0);
        assert!(!e.set_param(999, 1.0));
        assert_eq!(e.get_param(999), None);
    }

    #[test]
    fn disabled_effect_passes_signal_through_untouched() {
        let mut e = Enhancer::new(48_000.0);
        e.set_enabled(false);
        let input = stereo_pluck(256);
        let mut buf = input.clone();
        e.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn zero_mix_passes_signal_through_untouched() {
        let mut e = Enhancer::new(48_000.0);
        e.set_param(PARAM_MIX, 0.0);
        let input = stereo_pluck(256);
        let mut buf = input.clone();
        e.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn output_is_finite_and_length_preserved() {
        let mut e = Enhancer::new(48_000.0);
        let input = stereo_pluck(2_048);
        let mut buf = input.clone();
        e.process(&mut buf, 2);
        assert_eq!(buf.len(), input.len());
        assert!(buf.iter().all(|s| s.is_finite()));
    }

    #[test]
    fn matches_reference_dsp_within_minus_60_db() {
        // 2.2 acceptance: the production f32 enhancer must reproduce the f64
        // ground truth (transcribed from the C# algorithm, carrying the full
        // high-pass state) to better than -60 dB RMS error across the parameter
        // space.  Production flushes the recursive high-pass state out of the
        // subnormal range; the reference does not, but the two differ only at
        // subnormal magnitudes, far below the -60 dB bound.
        let input = stereo_pluck(8_192);
        for &(mix, cutoff, gain) in &[
            (0.2f64, 4000.0f64, 2.5f64),
            (0.12, 4500.0, 1.8),
            (0.25, 3200.0, 2.8),
            (0.08, 5500.0, 1.5),
        ] {
            let mut e = Enhancer::new(48_000.0);
            e.set_param(PARAM_MIX, mix as f32);
            e.set_param(PARAM_CUTOFF, cutoff as f32);
            e.set_param(PARAM_GAIN, gain as f32);

            let mut produced = input.clone();
            e.process(&mut produced, 2);

            let mut reference = Reference::new(48_000.0, cutoff, gain, mix);
            let expected = reference.process(&input);
            let err = rms_error_db(&produced, &expected);
            assert!(
                err < -60.0,
                "mix={mix} cutoff={cutoff} gain={gain}: RMS error {err:.1} dB exceeds -60 dB"
            );
        }
    }

    #[test]
    fn reset_clears_state() {
        let mut e = Enhancer::new(48_000.0);
        let input = stereo_pluck(512);
        let mut first = input.clone();
        e.process(&mut first, 2);
        e.reset();
        let mut second = input.clone();
        e.process(&mut second, 2);
        assert_eq!(first, second);
    }

    #[test]
    fn decaying_tail_does_not_produce_subnormals() {
        // A single impulse then a long silent tail: the denormal flush on the
        // high-pass state must keep the recursive state either normal or exactly
        // zero.
        let mut e = Enhancer::new(48_000.0);
        e.set_param(PARAM_MIX, 1.0);
        e.set_param(PARAM_GAIN, 10.0);
        let mut impulse = vec![0.0f32; 2];
        impulse[0] = 1.0;
        impulse[1] = 1.0;
        e.process(&mut impulse, 2);
        let mut silence = vec![0.0f32; 2 * 200_000];
        e.process(&mut silence, 2);
        assert!(
            e.y_prev == 0.0 || e.y_prev.abs() >= f32::MIN_POSITIVE,
            "subnormal in high-pass state: {:e}",
            e.y_prev
        );
    }
}
