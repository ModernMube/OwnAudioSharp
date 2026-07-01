//! Dynamic range compressor with soft-knee and peak detection.
//!
//! Rust DSP derived from the reference C# `OwnaudioNET.Effects.CompressorEffect`:
//! a peak envelope follower (separate attack / release one-pole coefficients)
//! drives a soft-knee static curve (6 dB knee, quadratic interpolation inside
//! the knee, linear `1/ratio − 1` slope above it); the resulting gain reduction
//! is applied together with a makeup gain.  Parameter identifiers, ranges and
//! defaults mirror the C# effect (threshold and makeup are dB-facing on both
//! sides).
//!
//! **Stereo-linked detection (intentional divergence from the C# reference):**
//! the C# effect walks a single envelope across the interleaved samples, so the
//! left and right channels of a frame receive gains one envelope step apart —
//! this drifts the stereo image as the signal level changes.  This port instead
//! detects per frame from the linked channel peak (`max(|L|, |R|, …)`), advances
//! one envelope per frame and applies the *same* gain to every channel of the
//! frame, so the inter-channel balance is preserved exactly.  Mono input (one
//! channel per frame) stays bit identical to the per-sample reference, and the
//! per-frame path also does half the transcendental work of the per-sample one
//! on stereo.  The `matches_reference_dsp` test therefore compares against a
//! frame-linked f64 ground truth, not the C# per-sample walk.
//!
//! **Denormal protection (2.12):** the recursive envelope is flushed every
//! frame so a decaying detector never parks in the subnormal range.

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};
use crate::denormal;

/// Param ID 2 — threshold in dB (-60 … 0).
pub const PARAM_THRESHOLD: u32 = 2;
/// Param ID 3 — compression ratio N:1 (1 … 100).
pub const PARAM_RATIO: u32 = 3;
/// Param ID 4 — attack time in ms (0.1 … 1000).
pub const PARAM_ATTACK: u32 = 4;
/// Param ID 5 — release time in ms (1 … 2000).
pub const PARAM_RELEASE: u32 = 5;
/// Param ID 6 — makeup gain in dB (-20 … +20).
pub const PARAM_MAKEUP: u32 = 6;

/// Soft-knee width in dB (reference constant).
const KNEE_WIDTH_DB: f32 = 6.0;
/// Half the soft-knee width.
const KNEE_HALF_WIDTH_DB: f32 = KNEE_WIDTH_DB / 2.0;

/// Envelope values below this are treated as silence (reference parity).
const ENV_FLOOR: f32 = 1.0e-6;

/// Dynamic range compressor with soft-knee peak detection.
pub struct Compressor {
    enabled: bool,
    sample_rate: f32,
    threshold_db: f32,
    ratio: f32,
    attack_ms: f32,
    release_ms: f32,
    makeup_db: f32,

    // Derived coefficients (recomputed on parameter change).
    attack_coeff: f32,
    release_coeff: f32,
    makeup_lin: f32,
    slope: f32,
    knee_lower_db: f32,
    knee_upper_db: f32,

    // Detector state.
    envelope: f32,
}

impl Compressor {
    /// Creates a new [`Compressor`] with the reference default parameters
    /// (threshold -6 dB, ratio 4:1, attack 100 ms, release 200 ms, makeup 0 dB).
    pub fn new(sample_rate: f32) -> Self {
        let sample_rate = sample_rate.clamp(8_000.0, 192_000.0);
        let mut c = Self {
            enabled: true,
            sample_rate,
            threshold_db: -6.0,
            ratio: 4.0,
            attack_ms: 100.0,
            release_ms: 200.0,
            makeup_db: 0.0,
            attack_coeff: 0.0,
            release_coeff: 0.0,
            makeup_lin: 1.0,
            slope: 0.0,
            knee_lower_db: 0.0,
            knee_upper_db: 0.0,
            envelope: 0.0,
        };
        c.recompute();
        c
    }

    /// Recomputes the cached coefficients from the current parameters.
    fn recompute(&mut self) {
        let attack_sec = (self.attack_ms * 0.001).max(1.0e-6);
        let release_sec = (self.release_ms * 0.001).max(1.0e-6);
        self.attack_coeff = (-1.0 / (self.sample_rate * attack_sec)).exp();
        self.release_coeff = (-1.0 / (self.sample_rate * release_sec)).exp();
        self.makeup_lin = 10.0f32.powf(self.makeup_db / 20.0);
        self.slope = 1.0 / self.ratio - 1.0;
        self.knee_lower_db = self.threshold_db - KNEE_HALF_WIDTH_DB;
        self.knee_upper_db = self.threshold_db + KNEE_HALF_WIDTH_DB;
    }
}

impl Effect for Compressor {
    fn effect_type(&self) -> EffectType {
        EffectType::Compressor
    }

    fn process(&mut self, buffer: &mut [f32], channels: u16) {
        if !self.enabled {
            return;
        }

        let ch = channels.max(1) as usize;
        let att = self.attack_coeff;
        let rel = self.release_coeff;
        let mkp = self.makeup_lin;
        let slope = self.slope;
        let t_db = self.threshold_db;
        let k_lower = self.knee_lower_db;
        let k_upper = self.knee_upper_db;
        let mut env = self.envelope;

        for frame in buffer.chunks_mut(ch) {
            // 1. Stereo-linked detection: the loudest channel of the frame
            //    drives the shared envelope, so every channel is compressed by
            //    the identical gain and the stereo image never drifts.
            let mut abs_input = 0.0f32;
            for &s in frame.iter() {
                let a = s.abs();
                if a > abs_input {
                    abs_input = a;
                }
            }

            // 2. Peak envelope detection with separate attack / release.
            if abs_input > env {
                env = att * env + (1.0 - att) * abs_input;
            } else {
                env = rel * env + (1.0 - rel) * abs_input;
            }
            env = denormal::flush(env);

            // 3. Soft-knee compression characteristic → one gain for the frame.
            //    Below the noise floor the reduction is unity (makeup only).
            let gain = if env < ENV_FLOOR {
                1.0
            } else {
                let env_db = 20.0 * env.log10();
                let gain_reduction_db = if env_db < k_lower {
                    0.0
                } else if env_db > k_upper {
                    slope * (env_db - t_db)
                } else {
                    let over = env_db - k_lower;
                    slope * (over * over) / (2.0 * KNEE_WIDTH_DB)
                };
                10.0f32.powf(gain_reduction_db * 0.05)
            };

            // 4. Apply the shared gain together with makeup to every channel.
            let applied = gain * mkp;
            for s in frame.iter_mut() {
                *s *= applied;
            }
        }

        self.envelope = env;
    }

    fn set_param(&mut self, param_id: u32, value: f32) -> bool {
        match param_id {
            PARAM_ENABLED => {
                self.enabled = value >= 0.5;
                true
            }
            // The compressor is always 100 % processed (reference parity); the
            // mix parameter is accepted but inert.
            PARAM_MIX => true,
            PARAM_THRESHOLD => {
                self.threshold_db = value.clamp(-60.0, 0.0);
                self.recompute();
                true
            }
            PARAM_RATIO => {
                self.ratio = value.clamp(1.0, 100.0);
                self.recompute();
                true
            }
            PARAM_ATTACK => {
                self.attack_ms = value.clamp(0.1, 1000.0);
                self.recompute();
                true
            }
            PARAM_RELEASE => {
                self.release_ms = value.clamp(1.0, 2000.0);
                self.recompute();
                true
            }
            PARAM_MAKEUP => {
                self.makeup_db = value.clamp(-20.0, 20.0);
                self.recompute();
                true
            }
            _ => false,
        }
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        match param_id {
            PARAM_ENABLED => Some(if self.enabled { 1.0 } else { 0.0 }),
            PARAM_MIX => Some(1.0),
            PARAM_THRESHOLD => Some(self.threshold_db),
            PARAM_RATIO => Some(self.ratio),
            PARAM_ATTACK => Some(self.attack_ms),
            PARAM_RELEASE => Some(self.release_ms),
            PARAM_MAKEUP => Some(self.makeup_db),
            _ => None,
        }
    }

    fn reset(&mut self) {
        self.envelope = 0.0;
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

    /// f64 ground-truth transcription of the reference C# DSP.
    struct Reference {
        attack_coeff: f64,
        release_coeff: f64,
        makeup_lin: f64,
        slope: f64,
        threshold_db: f64,
        knee_lower_db: f64,
        knee_upper_db: f64,
        envelope: f64,
    }

    impl Reference {
        fn new(
            sample_rate: f64,
            threshold_db: f64,
            ratio: f64,
            attack_ms: f64,
            release_ms: f64,
            makeup_db: f64,
        ) -> Self {
            let attack_sec = (attack_ms * 0.001).max(1.0e-6);
            let release_sec = (release_ms * 0.001).max(1.0e-6);
            Self {
                attack_coeff: (-1.0 / (sample_rate * attack_sec)).exp(),
                release_coeff: (-1.0 / (sample_rate * release_sec)).exp(),
                makeup_lin: 10.0f64.powf(makeup_db / 20.0),
                slope: 1.0 / ratio - 1.0,
                threshold_db,
                knee_lower_db: threshold_db - KNEE_HALF_WIDTH_DB as f64,
                knee_upper_db: threshold_db + KNEE_HALF_WIDTH_DB as f64,
                envelope: 0.0,
            }
        }

        /// Frame-linked f64 ground truth: one envelope/gain per frame, driven by
        /// the per-frame linked channel peak, applied to every channel.
        fn process(&mut self, input: &[f32], channels: usize) -> Vec<f32> {
            let ch = channels.max(1);
            let mut out = vec![0.0f32; input.len()];
            let mut env = self.envelope;
            for (out_frame, in_frame) in out.chunks_mut(ch).zip(input.chunks(ch)) {
                let mut abs_input = 0.0f64;
                for &s in in_frame.iter() {
                    let a = (s as f64).abs();
                    if a > abs_input {
                        abs_input = a;
                    }
                }
                if abs_input > env {
                    env = self.attack_coeff * env + (1.0 - self.attack_coeff) * abs_input;
                } else {
                    env = self.release_coeff * env + (1.0 - self.release_coeff) * abs_input;
                }
                let gain = if env < ENV_FLOOR as f64 {
                    1.0
                } else {
                    let env_db = 20.0 * env.log10();
                    let gr_db = if env_db < self.knee_lower_db {
                        0.0
                    } else if env_db > self.knee_upper_db {
                        self.slope * (env_db - self.threshold_db)
                    } else {
                        let over = env_db - self.knee_lower_db;
                        self.slope * (over * over) / (2.0 * KNEE_WIDTH_DB as f64)
                    };
                    10.0f64.powf(gr_db * 0.05)
                };
                let applied = gain * self.makeup_lin;
                for (o, &s) in out_frame.iter_mut().zip(in_frame.iter()) {
                    *o = (s as f64 * applied) as f32;
                }
            }
            self.envelope = env;
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

    /// Stereo tone that swells from quiet to loud, exercising attack/release.
    fn swell_stereo(frames: usize) -> Vec<f32> {
        let mut out = Vec::with_capacity(frames * 2);
        for i in 0..frames {
            let t = i as f32 / 48_000.0;
            let amp = 0.05 + 0.9 * (i as f32 / frames as f32);
            let v = amp * (2.0 * std::f32::consts::PI * 220.0 * t).sin();
            out.push(v);
            out.push(v * 0.8);
        }
        out
    }

    #[test]
    fn defaults_match_reference() {
        let c = Compressor::new(48_000.0);
        assert!(c.is_enabled());
        assert_eq!(c.get_param(PARAM_THRESHOLD), Some(-6.0));
        assert_eq!(c.get_param(PARAM_RATIO), Some(4.0));
        assert_eq!(c.get_param(PARAM_ATTACK), Some(100.0));
        assert_eq!(c.get_param(PARAM_RELEASE), Some(200.0));
        assert_eq!(c.get_param(PARAM_MAKEUP), Some(0.0));
        assert_eq!(c.get_param(PARAM_MIX), Some(1.0));
    }

    #[test]
    fn params_clamp_to_reference_ranges() {
        let mut c = Compressor::new(48_000.0);
        c.set_param(PARAM_THRESHOLD, 50.0);
        assert_eq!(c.get_param(PARAM_THRESHOLD), Some(0.0));
        c.set_param(PARAM_THRESHOLD, -200.0);
        assert_eq!(c.get_param(PARAM_THRESHOLD), Some(-60.0));
        c.set_param(PARAM_RATIO, 999.0);
        assert_eq!(c.get_param(PARAM_RATIO), Some(100.0));
        c.set_param(PARAM_ATTACK, 0.0);
        assert_eq!(c.get_param(PARAM_ATTACK), Some(0.1));
        c.set_param(PARAM_RELEASE, 9_999.0);
        assert_eq!(c.get_param(PARAM_RELEASE), Some(2000.0));
        c.set_param(PARAM_MAKEUP, 100.0);
        assert_eq!(c.get_param(PARAM_MAKEUP), Some(20.0));
    }

    #[test]
    fn unknown_param_is_rejected() {
        let mut c = Compressor::new(48_000.0);
        assert!(!c.set_param(999, 1.0));
        assert_eq!(c.get_param(999), None);
    }

    #[test]
    fn disabled_effect_passes_signal_through_untouched() {
        let mut c = Compressor::new(48_000.0);
        c.set_enabled(false);
        let input = swell_stereo(512);
        let mut buf = input.clone();
        c.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn loud_signal_is_attenuated() {
        let mut c = Compressor::new(48_000.0);
        c.set_param(PARAM_THRESHOLD, -20.0);
        c.set_param(PARAM_RATIO, 8.0);
        c.set_param(PARAM_ATTACK, 1.0);
        // A loud sustained tone well above threshold must be turned down.
        let mut buf: Vec<f32> = (0..9_600)
            .map(|i| 0.8 * (2.0 * std::f32::consts::PI * 220.0 * i as f32 / 48_000.0).sin())
            .collect();
        let input_rms: f32 = (buf.iter().map(|s| s * s).sum::<f32>() / buf.len() as f32).sqrt();
        c.process(&mut buf, 1);
        let tail = &buf[buf.len() / 2..];
        let out_rms: f32 = (tail.iter().map(|s| s * s).sum::<f32>() / tail.len() as f32).sqrt();
        assert!(out_rms < input_rms, "in_rms={input_rms} out_rms={out_rms}");
    }

    #[test]
    fn output_is_finite_and_length_preserved() {
        let mut c = Compressor::new(48_000.0);
        c.set_param(PARAM_THRESHOLD, -18.0);
        c.set_param(PARAM_MAKEUP, 6.0);
        let input = swell_stereo(512);
        let mut buf = input.clone();
        c.process(&mut buf, 2);
        assert_eq!(buf.len(), input.len());
        assert!(buf.iter().all(|s| s.is_finite()));
    }

    #[test]
    fn reset_restores_reproducibility() {
        let mut c = Compressor::new(48_000.0);
        c.set_param(PARAM_THRESHOLD, -18.0);
        let input = swell_stereo(1_024);

        let mut first = input.clone();
        c.process(&mut first, 2);
        c.reset();
        let mut second = input.clone();
        c.process(&mut second, 2);
        assert_eq!(first, second);
    }

    #[test]
    fn envelope_stays_out_of_subnormals_after_long_decay() {
        let mut c = Compressor::new(48_000.0);
        c.set_param(PARAM_THRESHOLD, -18.0);
        let mut excite = swell_stereo(512);
        c.process(&mut excite, 2);
        let mut silence = vec![0.0f32; 2_000_000];
        c.process(&mut silence, 2);
        assert!(
            c.envelope == 0.0 || c.envelope.abs() >= f32::MIN_POSITIVE,
            "envelope {} parked in the subnormal range",
            c.envelope
        );
    }

    #[test]
    fn matches_reference_dsp_within_minus_60_db() {
        let input = swell_stereo(8_192);
        let cases = [
            (-6.0f32, 4.0f32, 100.0f32, 200.0f32, 0.0f32),
            (-20.0, 8.0, 5.0, 100.0, 6.0),
            (-30.0, 20.0, 1.0, 500.0, -3.0),
        ];
        for (thr, ratio, atk, rel, makeup) in cases {
            let mut c = Compressor::new(48_000.0);
            c.set_param(PARAM_THRESHOLD, thr);
            c.set_param(PARAM_RATIO, ratio);
            c.set_param(PARAM_ATTACK, atk);
            c.set_param(PARAM_RELEASE, rel);
            c.set_param(PARAM_MAKEUP, makeup);

            let mut reference = Reference::new(
                48_000.0, thr as f64, ratio as f64, atk as f64, rel as f64, makeup as f64,
            );

            let mut produced = input.clone();
            c.process(&mut produced, 2);
            let expected = reference.process(&input, 2);

            let err = rms_error_db(&produced, &expected);
            assert!(
                err < -60.0,
                "thr={thr} ratio={ratio} atk={atk} rel={rel} makeup={makeup}: RMS error {err:.1} dB exceeds -60 dB"
            );
        }
    }
}
