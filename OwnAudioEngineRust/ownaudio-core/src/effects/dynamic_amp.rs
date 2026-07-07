//! Adaptive dynamic amplifier — dual-window RMS AGC with noise gate.
//!
//! Faithful Rust port of the reference C# `OwnaudioNET.Effects.DynamicAmpEffect`.
//! Unlike the sample-by-sample effects this one is **block based**: each
//! `process` call measures the mean-square energy of the whole buffer, feeds it
//! through a fast and a slow one-pole RMS detector (time constants derived from
//! the block duration), applies a hysteresis noise gate, computes a target gain
//! in the dB domain (bounded by the max reduction and max gain), slews it toward
//! the previous gain with an attack / release time constant and a per-second
//! rate limit, and finally applies the single resulting gain to every sample
//! with a soft knee near ±0.95.  Because the gain is derived per block, the
//! output depends on the block size — the reference comparison drives both sides
//! with identical blocking.  Parameter identifiers, ranges, defaults and the DSP
//! mirror the C# effect so the two are numerically equivalent (the basis of the
//! 2.2 reference comparison).  The effect has no wet/dry mix (it always runs
//! fully wet); the `Mix` parameter is accepted for API symmetry but unused.
//!
//! The dual RMS states are recursive, so they are denormal-flushed (2.12): a
//! decaying tail would otherwise park in the subnormal range and stall the CPU
//! on the audio thread.

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};
use crate::denormal;

/// Param ID 2 — target RMS level in dB (-60.0 … -3.0).
pub const PARAM_TARGET_RMS_DB: u32 = 2;
/// Param ID 3 — attack time in seconds (≥ 0.05).
pub const PARAM_ATTACK_TIME: u32 = 3;
/// Param ID 4 — release time in seconds (≥ 0.2).
pub const PARAM_RELEASE_TIME: u32 = 4;
/// Param ID 5 — noise gate threshold in dB (-80.0 … -30.0).
pub const PARAM_NOISE_GATE_DB: u32 = 5;
/// Param ID 6 — maximum gain multiplier (1.0 … 20.0).
pub const PARAM_MAX_GAIN: u32 = 6;
/// Param ID 7 — maximum gain reduction in dB (3.0 … 40.0).
pub const PARAM_MAX_GAIN_REDUCTION_DB: u32 = 7;
/// Param ID 8 — RMS averaging window in seconds (≥ 0.01).
pub const PARAM_RMS_WINDOW_SECONDS: u32 = 8;
/// Param ID 9 — maximum gain change rate in dB/second (≥ 1.0).
pub const PARAM_MAX_GAIN_CHANGE_DB_S: u32 = 9;

/// Hysteresis ratio applied to the noise gate open threshold (≈ 3 dB).
const HYSTERESIS_RATIO: f32 = 1.5;

#[inline]
fn db_to_linear(db: f32) -> f32 {
    10.0f32.powf(db / 20.0)
}

#[inline]
fn linear_to_db(linear: f32) -> f32 {
    if linear <= 1e-6 {
        -120.0
    } else {
        20.0 * linear.log10()
    }
}

/// Adaptive dynamic amplifier.
pub struct DynamicAmp {
    enabled: bool,
    mix: f32,
    target_rms_db: f32,
    attack_time: f32,
    release_time: f32,
    noise_gate_db: f32,
    max_gain: f32,
    max_gain_reduction_db: f32,
    rms_window_seconds: f32,
    max_gain_change_db_s: f32,
    sample_rate: f32,

    current_gain: f32,
    rms_fast_state: f32,
    rms_slow_state: f32,
    last_gain_db: f32,
    is_above_noise_gate: bool,
}

impl DynamicAmp {
    /// Creates a new [`DynamicAmp`] for `sample_rate`, with the reference
    /// constructor defaults (target -12 dB, attack 0.5 s, release 2.0 s, gate
    /// -50 dB, max gain 6.0, max reduction 12 dB, RMS window 0.5 s, max change
    /// 12 dB/s).
    pub fn new(sample_rate: f32) -> Self {
        let sample_rate = sample_rate.max(8000.0);
        Self {
            enabled: true,
            mix: 1.0,
            target_rms_db: -12.0,
            attack_time: 0.5,
            release_time: 2.0,
            noise_gate_db: -50.0,
            max_gain: 6.0,
            max_gain_reduction_db: 12.0,
            rms_window_seconds: 0.5,
            max_gain_change_db_s: 12.0,
            sample_rate,
            current_gain: 1.0,
            rms_fast_state: 0.0,
            rms_slow_state: 0.0,
            last_gain_db: 0.0,
            is_above_noise_gate: false,
        }
    }
}

impl Effect for DynamicAmp {
    fn effect_type(&self) -> EffectType {
        EffectType::DynamicAmp
    }

    fn process(&mut self, buffer: &mut [f32], channels: u16) {
        if !self.enabled || channels == 0 || buffer.is_empty() {
            return;
        }

        let total_samples = buffer.len();
        let frame_count = total_samples / channels.max(1) as usize;
        if frame_count == 0 {
            return;
        }

        // 1. Block mean-square energy.
        let block_sum_sq: f32 = buffer.iter().map(|&s| s * s).sum();
        let block_mean_sq = block_sum_sq / total_samples as f32;

        // 2. Dual one-pole RMS detectors, time constants from the block duration.
        let block_time = frame_count as f32 / self.sample_rate;
        let fast_alpha = (-block_time / (self.rms_window_seconds * 0.1)).exp();
        let slow_alpha = (-block_time / self.rms_window_seconds).exp();

        self.rms_fast_state =
            denormal::flush(fast_alpha * self.rms_fast_state + (1.0 - fast_alpha) * block_mean_sq);
        self.rms_slow_state =
            denormal::flush(slow_alpha * self.rms_slow_state + (1.0 - slow_alpha) * block_mean_sq);

        let mut rms_slow = self.rms_slow_state.max(0.0).sqrt();
        if rms_slow.is_nan() {
            rms_slow = 0.0;
            self.rms_slow_state = 0.0;
        }

        // 3. Hysteresis noise gate.
        let noise_gate_linear = db_to_linear(self.noise_gate_db);
        if !self.is_above_noise_gate {
            if rms_slow > noise_gate_linear * HYSTERESIS_RATIO {
                self.is_above_noise_gate = true;
            }
        } else if rms_slow < noise_gate_linear {
            self.is_above_noise_gate = false;
        }

        // 4. Desired gain in dB — hold the previous value while gated.
        let mut desired_gain_db = self.last_gain_db;
        if self.is_above_noise_gate {
            let current_level_db = linear_to_db(rms_slow.max(1e-6));
            let gain_error_db = (self.target_rms_db - current_level_db)
                .clamp(-self.max_gain_reduction_db, linear_to_db(self.max_gain));
            desired_gain_db = gain_error_db;
        }

        // 5. Attack / release smoothing with a per-block change limit.
        let max_change_this_block = self.max_gain_change_db_s * block_time;
        let gain_change_db = desired_gain_db - self.last_gain_db;
        let time_const = if gain_change_db < 0.0 {
            self.attack_time
        } else {
            self.release_time
        };
        let alpha = (-block_time / time_const).exp();
        let smoothed_change_db =
            ((1.0 - alpha) * gain_change_db).clamp(-max_change_this_block, max_change_this_block);

        let new_gain_db = self.last_gain_db + smoothed_change_db;
        self.current_gain = db_to_linear(new_gain_db).clamp(0.1, self.max_gain);

        // 6. Apply the single block gain with a soft knee near ±0.95.
        let final_gain = self.current_gain;
        for sample in buffer.iter_mut() {
            let mut val = *sample * final_gain;
            let abs_val = val.abs();
            if abs_val > 0.95 {
                let excess = abs_val - 0.95;
                let limited = 0.95 + excess / (1.0 + excess * 20.0);
                val = if val > 0.0 { limited } else { -limited };
            }
            *sample = val.clamp(-1.0, 1.0);
        }

        // 7. Update state.
        self.last_gain_db = linear_to_db(self.current_gain);
    }

    fn set_param(&mut self, param_id: u32, value: f32) -> bool {
        match param_id {
            PARAM_ENABLED => {
                self.enabled = value >= 0.5;
                true
            }
            // DynamicAmp has no wet/dry mix; accept and store for API symmetry.
            PARAM_MIX => {
                self.mix = value.clamp(0.0, 1.0);
                true
            }
            PARAM_TARGET_RMS_DB => {
                self.target_rms_db = value.clamp(-60.0, -3.0);
                true
            }
            PARAM_ATTACK_TIME => {
                self.attack_time = value.max(0.05);
                true
            }
            PARAM_RELEASE_TIME => {
                self.release_time = value.max(0.2);
                true
            }
            PARAM_NOISE_GATE_DB => {
                self.noise_gate_db = value.clamp(-80.0, -30.0);
                true
            }
            PARAM_MAX_GAIN => {
                self.max_gain = value.clamp(1.0, 20.0);
                true
            }
            PARAM_MAX_GAIN_REDUCTION_DB => {
                self.max_gain_reduction_db = value.clamp(3.0, 40.0);
                true
            }
            PARAM_RMS_WINDOW_SECONDS => {
                self.rms_window_seconds = value.max(0.01);
                true
            }
            PARAM_MAX_GAIN_CHANGE_DB_S => {
                self.max_gain_change_db_s = value.max(1.0);
                true
            }
            _ => false,
        }
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        match param_id {
            PARAM_ENABLED => Some(if self.enabled { 1.0 } else { 0.0 }),
            PARAM_MIX => Some(self.mix),
            PARAM_TARGET_RMS_DB => Some(self.target_rms_db),
            PARAM_ATTACK_TIME => Some(self.attack_time),
            PARAM_RELEASE_TIME => Some(self.release_time),
            PARAM_NOISE_GATE_DB => Some(self.noise_gate_db),
            PARAM_MAX_GAIN => Some(self.max_gain),
            PARAM_MAX_GAIN_REDUCTION_DB => Some(self.max_gain_reduction_db),
            PARAM_RMS_WINDOW_SECONDS => Some(self.rms_window_seconds),
            PARAM_MAX_GAIN_CHANGE_DB_S => Some(self.max_gain_change_db_s),
            _ => None,
        }
    }

    fn reset(&mut self) {
        self.current_gain = 1.0;
        self.rms_fast_state = 0.0;
        self.rms_slow_state = 0.0;
        self.last_gain_db = 0.0;
        self.is_above_noise_gate = false;
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

    /// f64 ground-truth transcription of the reference C# adaptive amplifier,
    /// carrying the full detector, gate and gain state, used to measure the
    /// production f32 implementation's numerical fidelity.  The DSP is smooth
    /// (no integer quantization), so a straight f64 transcription tracks the f32
    /// production to well within the -60 dB bound as long as both sides are
    /// driven with identical block sizes (the gain is derived per block) and the
    /// signal stays clear of the gate so the hysteresis boolean agrees.
    struct Reference {
        target_rms_db: f64,
        attack_time: f64,
        release_time: f64,
        noise_gate_db: f64,
        max_gain: f64,
        max_gain_reduction_db: f64,
        rms_window_seconds: f64,
        max_gain_change_db_s: f64,
        sample_rate: f64,
        rms_fast_state: f64,
        rms_slow_state: f64,
        last_gain_db: f64,
        is_above_noise_gate: bool,
    }

    impl Reference {
        #[allow(clippy::too_many_arguments)]
        fn new(
            sample_rate: f64,
            target_rms_db: f64,
            attack_time: f64,
            release_time: f64,
            noise_gate_db: f64,
            max_gain: f64,
            max_gain_reduction_db: f64,
            rms_window_seconds: f64,
            max_gain_change_db_s: f64,
        ) -> Self {
            Self {
                target_rms_db,
                attack_time,
                release_time,
                noise_gate_db,
                max_gain,
                max_gain_reduction_db,
                rms_window_seconds,
                max_gain_change_db_s,
                sample_rate,
                rms_fast_state: 0.0,
                rms_slow_state: 0.0,
                last_gain_db: 0.0,
                is_above_noise_gate: false,
            }
        }

        fn db_to_linear(db: f64) -> f64 {
            10.0f64.powf(db / 20.0)
        }

        fn linear_to_db(linear: f64) -> f64 {
            if linear <= 1e-6 {
                -120.0
            } else {
                20.0 * linear.log10()
            }
        }

        fn process_block(&mut self, buffer: &mut [f32], channels: usize) {
            let total_samples = buffer.len();
            let frame_count = total_samples / channels.max(1);
            if frame_count == 0 {
                return;
            }

            let block_sum_sq: f64 = buffer.iter().map(|&s| (s as f64) * (s as f64)).sum();
            let block_mean_sq = block_sum_sq / total_samples as f64;

            let block_time = frame_count as f64 / self.sample_rate;
            let fast_alpha = (-block_time / (self.rms_window_seconds * 0.1)).exp();
            let slow_alpha = (-block_time / self.rms_window_seconds).exp();

            self.rms_fast_state =
                fast_alpha * self.rms_fast_state + (1.0 - fast_alpha) * block_mean_sq;
            self.rms_slow_state =
                slow_alpha * self.rms_slow_state + (1.0 - slow_alpha) * block_mean_sq;

            let rms_slow = self.rms_slow_state.max(0.0).sqrt();

            let noise_gate_linear = Self::db_to_linear(self.noise_gate_db);
            if !self.is_above_noise_gate {
                if rms_slow > noise_gate_linear * HYSTERESIS_RATIO as f64 {
                    self.is_above_noise_gate = true;
                }
            } else if rms_slow < noise_gate_linear {
                self.is_above_noise_gate = false;
            }

            let mut desired_gain_db = self.last_gain_db;
            if self.is_above_noise_gate {
                let current_level_db = Self::linear_to_db(rms_slow.max(1e-6));
                let gain_error_db = (self.target_rms_db - current_level_db).clamp(
                    -self.max_gain_reduction_db,
                    Self::linear_to_db(self.max_gain),
                );
                desired_gain_db = gain_error_db;
            }

            let max_change_this_block = self.max_gain_change_db_s * block_time;
            let gain_change_db = desired_gain_db - self.last_gain_db;
            let time_const = if gain_change_db < 0.0 {
                self.attack_time
            } else {
                self.release_time
            };
            let alpha = (-block_time / time_const).exp();
            let smoothed_change_db = ((1.0 - alpha) * gain_change_db)
                .clamp(-max_change_this_block, max_change_this_block);

            let new_gain_db = self.last_gain_db + smoothed_change_db;
            let current_gain = Self::db_to_linear(new_gain_db).clamp(0.1, self.max_gain);

            for sample in buffer.iter_mut() {
                let mut val = *sample as f64 * current_gain;
                let abs_val = val.abs();
                if abs_val > 0.95 {
                    let excess = abs_val - 0.95;
                    let limited = 0.95 + excess / (1.0 + excess * 20.0);
                    val = if val > 0.0 { limited } else { -limited };
                }
                *sample = val.clamp(-1.0, 1.0) as f32;
            }

            self.last_gain_db = Self::linear_to_db(current_gain);
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

    /// A steady, moderately quiet stereo tone comfortably above the gate so the
    /// AGC pulls the gain toward the target across many blocks.
    fn stereo_tone(frames: usize) -> Vec<f32> {
        let mut v = vec![0.0f32; frames * 2];
        for f in 0..frames {
            let t = f as f32 / 48_000.0;
            let s = 0.12 * (2.0 * std::f32::consts::PI * 220.0 * t).sin();
            v[f * 2] = s;
            v[f * 2 + 1] = s * 0.9;
        }
        v
    }

    #[test]
    fn defaults_match_reference() {
        let d = DynamicAmp::new(48_000.0);
        assert_eq!(d.get_param(PARAM_TARGET_RMS_DB), Some(-12.0));
        assert_eq!(d.get_param(PARAM_ATTACK_TIME), Some(0.5));
        assert_eq!(d.get_param(PARAM_RELEASE_TIME), Some(2.0));
        assert_eq!(d.get_param(PARAM_NOISE_GATE_DB), Some(-50.0));
        assert_eq!(d.get_param(PARAM_MAX_GAIN), Some(6.0));
        assert_eq!(d.get_param(PARAM_MAX_GAIN_REDUCTION_DB), Some(12.0));
        assert_eq!(d.get_param(PARAM_RMS_WINDOW_SECONDS), Some(0.5));
        assert_eq!(d.get_param(PARAM_MAX_GAIN_CHANGE_DB_S), Some(12.0));
        assert!(d.is_enabled());
    }

    #[test]
    fn params_clamp_to_reference_ranges() {
        let mut d = DynamicAmp::new(48_000.0);
        d.set_param(PARAM_TARGET_RMS_DB, 0.0);
        assert_eq!(d.get_param(PARAM_TARGET_RMS_DB), Some(-3.0));
        d.set_param(PARAM_TARGET_RMS_DB, -100.0);
        assert_eq!(d.get_param(PARAM_TARGET_RMS_DB), Some(-60.0));
        d.set_param(PARAM_ATTACK_TIME, 0.0);
        assert_eq!(d.get_param(PARAM_ATTACK_TIME), Some(0.05));
        d.set_param(PARAM_NOISE_GATE_DB, -200.0);
        assert_eq!(d.get_param(PARAM_NOISE_GATE_DB), Some(-80.0));
        d.set_param(PARAM_MAX_GAIN, 100.0);
        assert_eq!(d.get_param(PARAM_MAX_GAIN), Some(20.0));
        d.set_param(PARAM_MAX_GAIN_REDUCTION_DB, 1.0);
        assert_eq!(d.get_param(PARAM_MAX_GAIN_REDUCTION_DB), Some(3.0));
    }

    #[test]
    fn unknown_param_is_rejected() {
        let mut d = DynamicAmp::new(48_000.0);
        assert!(!d.set_param(999, 1.0));
        assert_eq!(d.get_param(999), None);
    }

    #[test]
    fn disabled_effect_passes_signal_through_untouched() {
        let mut d = DynamicAmp::new(48_000.0);
        d.set_enabled(false);
        let input = stereo_tone(1_024);
        let mut buf = input.clone();
        d.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn output_is_finite_and_length_preserved() {
        let mut d = DynamicAmp::new(48_000.0);
        let input = stereo_tone(4_096);
        let mut buf = input.clone();
        d.process(&mut buf, 2);
        assert_eq!(buf.len(), input.len());
        assert!(buf.iter().all(|s| s.is_finite()));
    }

    #[test]
    fn matches_reference_dsp_within_minus_60_db() {
        // 2.2 acceptance: the production f32 amplifier must reproduce the f64
        // ground truth to better than -60 dB RMS error across the parameter
        // space.  Both sides are driven with identical 1024-frame blocks (the
        // gain is derived per block), and the tone sits well above the gate so
        // the hysteresis boolean agrees on both sides.  Production flushes the
        // recursive RMS states out of the subnormal range; the reference does
        // not, but the two differ only at subnormal magnitudes, far below the
        // -60 dB bound.
        const BLOCK_FRAMES: usize = 1024;
        let full = stereo_tone(BLOCK_FRAMES * 24);
        for &(target, attack, release, gate, max_g, max_red, window, max_change) in &[
            (
                -12.0f64, 0.5f64, 2.0f64, -50.0f64, 6.0f64, 12.0f64, 0.5f64, 12.0f64,
            ),
            (-15.0, 0.18, 0.80, -45.0, 8.0, 15.0, 0.5, 20.0),
            (-10.0, 2.00, 5.00, -60.0, 3.0, 6.0, 0.5, 3.0),
        ] {
            let mut d = DynamicAmp::new(48_000.0);
            d.set_param(PARAM_TARGET_RMS_DB, target as f32);
            d.set_param(PARAM_ATTACK_TIME, attack as f32);
            d.set_param(PARAM_RELEASE_TIME, release as f32);
            d.set_param(PARAM_NOISE_GATE_DB, gate as f32);
            d.set_param(PARAM_MAX_GAIN, max_g as f32);
            d.set_param(PARAM_MAX_GAIN_REDUCTION_DB, max_red as f32);
            d.set_param(PARAM_RMS_WINDOW_SECONDS, window as f32);
            d.set_param(PARAM_MAX_GAIN_CHANGE_DB_S, max_change as f32);

            let mut reference = Reference::new(
                48_000.0, target, attack, release, gate, max_g, max_red, window, max_change,
            );

            let mut produced = full.clone();
            let mut expected = full.clone();
            let block_samples = BLOCK_FRAMES * 2;
            for (p_block, e_block) in produced
                .chunks_mut(block_samples)
                .zip(expected.chunks_mut(block_samples))
            {
                d.process(p_block, 2);
                reference.process_block(e_block, 2);
            }

            let err = rms_error_db(&produced, &expected);
            assert!(
                err < -60.0,
                "target={target} attack={attack} release={release} gate={gate} max={max_g}: RMS error {err:.1} dB exceeds -60 dB"
            );
        }
    }

    #[test]
    fn reset_clears_state() {
        let mut d = DynamicAmp::new(48_000.0);
        let input = stereo_tone(1_024);
        let mut first = input.clone();
        d.process(&mut first, 2);
        d.reset();
        let mut second = input.clone();
        d.process(&mut second, 2);
        assert_eq!(first, second);
    }

    #[test]
    fn decaying_level_does_not_produce_subnormals() {
        // A burst of signal then a long run of silent blocks: the denormal flush
        // on the dual RMS states must keep the recursive detectors either normal
        // or exactly zero.
        let mut d = DynamicAmp::new(48_000.0);
        let mut burst = stereo_tone(1_024);
        d.process(&mut burst, 2);
        for _ in 0..2_000 {
            let mut silence = vec![0.0f32; 1024 * 2];
            d.process(&mut silence, 2);
        }
        assert!(
            d.rms_fast_state == 0.0 || d.rms_fast_state.abs() >= f32::MIN_POSITIVE,
            "subnormal in fast RMS state: {:e}",
            d.rms_fast_state
        );
        assert!(
            d.rms_slow_state == 0.0 || d.rms_slow_state.abs() >= f32::MIN_POSITIVE,
            "subnormal in slow RMS state: {:e}",
            d.rms_slow_state
        );
    }
}
