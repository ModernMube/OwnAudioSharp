//! Phaser with cascaded all-pass filter stages.
//!
//! Faithful Rust port of the reference C# `OwnaudioNET.Effects.PhaserEffect`: a
//! chain of up to eight first-order all-pass filters whose shared coefficient is
//! derived from a notch frequency swept between 200 Hz and 2 kHz by one global
//! LFO scaled by the depth; the cascade output is mixed with a scaled copy of
//! the input (the feedback term) and blended with the dry signal.  A single
//! filter chain is shared across the interleaved samples exactly as the C#
//! effect walks them.  Parameter identifiers, ranges, defaults and the
//! per-sample DSP mirror the C# effect so the two are numerically equivalent
//! (the basis of the 2.2 reference comparison).
//!
//! Each all-pass stage carries a recursive `y1` state, so its output is
//! denormal-flushed (2.12): a decaying tail would otherwise park in the
//! subnormal range and stall the CPU on the audio thread.

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};
use crate::denormal;
use crate::smoothing::{RampedParam, DEFAULT_SMOOTH_MS};

/// Param ID 2 — LFO modulation rate in Hz (0.1 … 10.0).
pub const PARAM_RATE: u32 = 2;
/// Param ID 3 — modulation depth (0.0 … 1.0).
pub const PARAM_DEPTH: u32 = 3;
/// Param ID 4 — feedback amount (0.0 … 0.95).
pub const PARAM_FEEDBACK: u32 = 4;
/// Param ID 5 — number of all-pass stages (2 … 8).
pub const PARAM_STAGES: u32 = 5;

/// Maximum number of cascaded all-pass stages (mirrors the C# reference).
const MAX_STAGES: usize = 8;

/// First-order all-pass filter stage.
#[derive(Clone, Copy, Default)]
struct AllPass {
    x1: f32,
    y1: f32,
}

impl AllPass {
    #[inline]
    fn process(&mut self, input: f32, coefficient: f32) -> f32 {
        let output = -coefficient * input + self.x1 + coefficient * self.y1;
        self.x1 = input;
        self.y1 = denormal::flush(output);
        self.y1
    }
}

/// Phaser effect.
pub struct Phaser {
    enabled: bool,
    mix: f32,
    rate_hz: f32,
    depth: f32,
    feedback: f32,
    stages: usize,
    sample_rate: f32,

    filters: [AllPass; MAX_STAGES],
    lfo_phase: f32,
    mix_ramp: RampedParam,
}

impl Phaser {
    /// Creates a new [`Phaser`] sized for `sample_rate`, with the reference
    /// default parameters (rate 0.5 Hz, depth 0.7, feedback 0.5, mix 0.5,
    /// 4 stages).
    pub fn new(sample_rate: f32) -> Self {
        let sample_rate = if sample_rate > 0.0 { sample_rate } else { 44_100.0 };
        Self {
            enabled: true,
            mix: 0.5,
            rate_hz: 0.5,
            depth: 0.7,
            feedback: 0.5,
            stages: 4,
            sample_rate,
            filters: [AllPass::default(); MAX_STAGES],
            lfo_phase: 0.0,
            mix_ramp: RampedParam::new(0.5, sample_rate, DEFAULT_SMOOTH_MS),
        }
    }

    /// All-pass coefficient for a notch at `frequency`; mirrors the reference
    /// `CalculateAllPassCoefficient`.
    #[inline]
    fn all_pass_coefficient(&self, frequency: f32) -> f32 {
        let omega = 2.0 * std::f32::consts::PI * frequency / self.sample_rate;
        let tan_half_omega = (omega * 0.5).tan();
        (tan_half_omega - 1.0) / (tan_half_omega + 1.0)
    }
}

impl Effect for Phaser {
    fn effect_type(&self) -> EffectType {
        EffectType::Phaser
    }

    fn process(&mut self, buffer: &mut [f32], _channels: u16) {
        self.mix_ramp.begin_block();
        if !self.enabled {
            return;
        }

        let two_pi = std::f32::consts::PI * 2.0;
        let depth = self.depth;
        let feedback = self.feedback;
        let stages = self.stages;
        let lfo_increment = two_pi * self.rate_hz / self.sample_rate;

        const MIN_FREQ: f32 = 200.0;
        const MAX_FREQ: f32 = 2000.0;

        let mut lfo_phase = self.lfo_phase;

        for sample in buffer.iter_mut() {
            let mix = self.mix_ramp.advance();
            let input = *sample;

            let lfo_value = lfo_phase.sin();
            let frequency = MIN_FREQ + (MAX_FREQ - MIN_FREQ) * (0.5 + 0.5 * lfo_value * depth);
            let coefficient = self.all_pass_coefficient(frequency);

            let mut processed = input;
            for filter in self.filters.iter_mut().take(stages) {
                processed = filter.process(processed, coefficient);
            }

            processed += input * feedback;

            *sample = input * (1.0 - mix) + processed * mix;

            lfo_phase += lfo_increment;
            if lfo_phase >= two_pi {
                lfo_phase -= two_pi;
            }
        }

        self.lfo_phase = lfo_phase;
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
            PARAM_RATE => {
                self.rate_hz = value.clamp(0.1, 10.0);
                true
            }
            PARAM_DEPTH => {
                self.depth = value.clamp(0.0, 1.0);
                true
            }
            PARAM_FEEDBACK => {
                self.feedback = value.clamp(0.0, 0.95);
                true
            }
            PARAM_STAGES => {
                self.stages = (value.clamp(2.0, 8.0) as usize).clamp(2, MAX_STAGES);
                true
            }
            _ => false,
        }
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        match param_id {
            PARAM_ENABLED => Some(if self.enabled { 1.0 } else { 0.0 }),
            PARAM_MIX => Some(self.mix),
            PARAM_RATE => Some(self.rate_hz),
            PARAM_DEPTH => Some(self.depth),
            PARAM_FEEDBACK => Some(self.feedback),
            PARAM_STAGES => Some(self.stages as f32),
            _ => None,
        }
    }

    fn reset(&mut self) {
        self.filters = [AllPass::default(); MAX_STAGES];
        self.lfo_phase = 0.0;
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

    /// f64 ground-truth transcription of the reference C# phaser, carrying the
    /// full all-pass and LFO state, used to measure the production f32
    /// implementation's numerical fidelity.  The all-pass cascade is a smooth
    /// IIR (no integer quantization), so a straight f64 transcription tracks the
    /// f32 production to well within the -60 dB bound.
    struct Reference {
        sample_rate: f64,
        depth: f64,
        mix: f64,
        feedback: f64,
        stages: usize,
        rate: f64,
        x1: [f64; MAX_STAGES],
        y1: [f64; MAX_STAGES],
        lfo_phase: f64,
    }

    impl Reference {
        fn new(sample_rate: f64, rate: f64, depth: f64, feedback: f64, mix: f64, stages: usize) -> Self {
            Self {
                sample_rate,
                depth,
                mix,
                feedback,
                stages,
                rate,
                x1: [0.0; MAX_STAGES],
                y1: [0.0; MAX_STAGES],
                lfo_phase: 0.0,
            }
        }

        fn coefficient(&self, frequency: f64) -> f64 {
            let omega = 2.0 * std::f64::consts::PI * frequency / self.sample_rate;
            let tan_half_omega = (omega * 0.5).tan();
            (tan_half_omega - 1.0) / (tan_half_omega + 1.0)
        }

        fn process(&mut self, buffer: &[f32]) -> Vec<f32> {
            let mut out = vec![0.0f32; buffer.len()];
            let two_pi = std::f64::consts::PI * 2.0;
            let lfo_increment = two_pi * self.rate / self.sample_rate;
            let min_freq = 200.0f64;
            let max_freq = 2000.0f64;

            for (o, &x) in out.iter_mut().zip(buffer.iter()) {
                let input = x as f64;

                let lfo_value = self.lfo_phase.sin();
                let frequency = min_freq + (max_freq - min_freq) * (0.5 + 0.5 * lfo_value * self.depth);
                let coefficient = self.coefficient(frequency);

                let mut processed = input;
                for s in 0..self.stages {
                    let output = -coefficient * processed + self.x1[s] + coefficient * self.y1[s];
                    self.x1[s] = processed;
                    self.y1[s] = output;
                    processed = output;
                }

                processed += input * self.feedback;

                *o = (input * (1.0 - self.mix) + processed * self.mix) as f32;

                self.lfo_phase += lfo_increment;
                if self.lfo_phase >= two_pi {
                    self.lfo_phase -= two_pi;
                }
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
            let s = 0.6 * env * (2.0 * std::f32::consts::PI * 330.0 * t).sin();
            v[f * 2] = s;
            v[f * 2 + 1] = s * 0.8;
        }
        v
    }

    #[test]
    fn defaults_match_reference() {
        let p = Phaser::new(48_000.0);
        assert_eq!(p.get_param(PARAM_RATE), Some(0.5));
        assert_eq!(p.get_param(PARAM_DEPTH), Some(0.7));
        assert_eq!(p.get_param(PARAM_FEEDBACK), Some(0.5));
        assert_eq!(p.get_param(PARAM_MIX), Some(0.5));
        assert_eq!(p.get_param(PARAM_STAGES), Some(4.0));
        assert!(p.is_enabled());
    }

    #[test]
    fn params_clamp_to_reference_ranges() {
        let mut p = Phaser::new(48_000.0);
        p.set_param(PARAM_RATE, 100.0);
        assert_eq!(p.get_param(PARAM_RATE), Some(10.0));
        p.set_param(PARAM_RATE, 0.0);
        assert_eq!(p.get_param(PARAM_RATE), Some(0.1));
        p.set_param(PARAM_DEPTH, 5.0);
        assert_eq!(p.get_param(PARAM_DEPTH), Some(1.0));
        p.set_param(PARAM_FEEDBACK, 5.0);
        assert_eq!(p.get_param(PARAM_FEEDBACK), Some(0.95));
        p.set_param(PARAM_STAGES, 100.0);
        assert_eq!(p.get_param(PARAM_STAGES), Some(8.0));
        p.set_param(PARAM_STAGES, 0.0);
        assert_eq!(p.get_param(PARAM_STAGES), Some(2.0));
        p.set_param(PARAM_MIX, 2.0);
        assert_eq!(p.get_param(PARAM_MIX), Some(1.0));
    }

    #[test]
    fn unknown_param_is_rejected() {
        let mut p = Phaser::new(48_000.0);
        assert!(!p.set_param(999, 1.0));
        assert_eq!(p.get_param(999), None);
    }

    #[test]
    fn disabled_effect_passes_signal_through_untouched() {
        let mut p = Phaser::new(48_000.0);
        p.set_enabled(false);
        let input = stereo_pluck(256);
        let mut buf = input.clone();
        p.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn zero_mix_passes_signal_through_untouched() {
        let mut p = Phaser::new(48_000.0);
        p.set_param(PARAM_MIX, 0.0);
        let input = stereo_pluck(256);
        let mut buf = input.clone();
        p.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn output_is_finite_and_length_preserved() {
        let mut p = Phaser::new(48_000.0);
        let input = stereo_pluck(2_048);
        let mut buf = input.clone();
        p.process(&mut buf, 2);
        assert_eq!(buf.len(), input.len());
        assert!(buf.iter().all(|s| s.is_finite()));
    }

    #[test]
    fn matches_reference_dsp_within_minus_60_db() {
        // 2.2 acceptance: the production f32 phaser must reproduce the f64 ground
        // truth (transcribed from the C# algorithm, carrying the full all-pass
        // and LFO state) to better than -60 dB RMS error across the parameter
        // space.  Production flushes the recursive all-pass state out of the
        // subnormal range; the reference does not, but the two differ only at
        // subnormal magnitudes, far below the -60 dB bound.
        let input = stereo_pluck(8_192);
        for &(rate, depth, feedback, mix, stages) in &[
            (0.5f64, 0.7f64, 0.5f64, 0.5f64, 4usize),
            (0.6, 0.75, 0.62, 0.60, 4),
            (4.0, 0.58, 0.18, 0.70, 3),
            (0.3, 1.0, 0.85, 0.82, 8),
        ] {
            let mut p = Phaser::new(48_000.0);
            p.set_param(PARAM_RATE, rate as f32);
            p.set_param(PARAM_DEPTH, depth as f32);
            p.set_param(PARAM_FEEDBACK, feedback as f32);
            p.set_param(PARAM_MIX, mix as f32);
            p.set_param(PARAM_STAGES, stages as f32);

            let mut produced = input.clone();
            p.process(&mut produced, 2);

            let mut reference = Reference::new(48_000.0, rate, depth, feedback, mix, stages);
            let expected = reference.process(&input);
            let err = rms_error_db(&produced, &expected);
            assert!(
                err < -60.0,
                "rate={rate} depth={depth} feedback={feedback} mix={mix} stages={stages}: RMS error {err:.1} dB exceeds -60 dB"
            );
        }
    }

    #[test]
    fn reset_clears_state() {
        let mut p = Phaser::new(48_000.0);
        let input = stereo_pluck(512);
        let mut first = input.clone();
        p.process(&mut first, 2);
        p.reset();
        let mut second = input.clone();
        p.process(&mut second, 2);
        assert_eq!(first, second);
    }

    #[test]
    fn decaying_tail_does_not_produce_subnormals() {
        // A high-feedback phaser fed a single impulse then a long silent tail:
        // the denormal flush on each all-pass state must keep the recursive
        // state either normal or exactly zero.
        let mut p = Phaser::new(48_000.0);
        p.set_param(PARAM_FEEDBACK, 0.95);
        p.set_param(PARAM_STAGES, 8.0);
        p.set_param(PARAM_MIX, 1.0);
        let mut impulse = vec![0.0f32; 2];
        impulse[0] = 1.0;
        impulse[1] = 1.0;
        p.process(&mut impulse, 2);
        let mut silence = vec![0.0f32; 2 * 200_000];
        p.process(&mut silence, 2);
        for f in &p.filters {
            assert!(
                f.y1 == 0.0 || f.y1.abs() >= f32::MIN_POSITIVE,
                "subnormal in all-pass state: {:e}",
                f.y1
            );
        }
    }
}
