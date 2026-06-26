//! Noise gate.
//!
//! There is no C# reference effect for the gate (only a thin FFI wrapper), so
//! this is a faithful implementation of a standard noise gate using the
//! parameter model already declared on the native side: a threshold in dB, plus
//! attack / hold / release times in milliseconds.
//!
//! The detector takes the per-frame peak across all channels and compares it to
//! the linear threshold.  A one-pole gain envelope opens toward unity with the
//! attack time constant while the signal is above the threshold, stays open for
//! the hold time after it drops below, then closes toward zero with the release
//! time constant.  The same gain is applied to every channel of the frame so the
//! stereo image is preserved, and the gated (wet) signal is blended with the dry
//! signal by `mix`.
//!
//! **Denormal protection (2.12):** the recursive gain envelope is flushed every
//! frame so a closing gate never parks its envelope in the subnormal range.

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};
use crate::denormal;

/// Param ID 2 — open threshold in dB (-80 … 0).
pub const PARAM_THRESHOLD: u32 = 2;
/// Param ID 3 — attack time in ms (0.1 … 100).
pub const PARAM_ATTACK: u32 = 3;
/// Param ID 4 — release time in ms (10 … 2000).
pub const PARAM_RELEASE: u32 = 4;
/// Param ID 5 — hold time in ms (0 … 500).
pub const PARAM_HOLD: u32 = 5;

/// Mix values below this bypass processing entirely.
const MIX_BYPASS_THRESHOLD: f32 = 0.001;

/// Noise gate effect.
pub struct Gate {
    enabled: bool,
    mix: f32,
    sample_rate: f32,
    threshold_db: f32,
    attack_ms: f32,
    release_ms: f32,
    hold_ms: f32,

    // Derived from the parameters / sample rate (recomputed on change).
    threshold_lin: f32,
    attack_coeff: f32,
    release_coeff: f32,
    hold_frames: u32,

    // Envelope state.
    gain: f32,
    hold_counter: u32,
}

impl Gate {
    /// Creates a new [`Gate`] with standard default parameters
    /// (threshold -40 dB, attack 1 ms, release 100 ms, hold 50 ms).
    pub fn new(sample_rate: f32) -> Self {
        let sample_rate = if sample_rate > 0.0 {
            sample_rate
        } else {
            44_100.0
        };
        let mut g = Self {
            enabled: true,
            mix: 1.0,
            sample_rate,
            threshold_db: -40.0,
            attack_ms: 1.0,
            release_ms: 100.0,
            hold_ms: 50.0,
            threshold_lin: 0.0,
            attack_coeff: 0.0,
            release_coeff: 0.0,
            hold_frames: 0,
            gain: 0.0,
            hold_counter: 0,
        };
        g.recompute();
        g
    }

    /// One-pole smoothing coefficient for a given time constant (ms).
    fn time_coeff(&self, time_ms: f32) -> f32 {
        let t = (time_ms * 0.001).max(1.0e-6);
        1.0 - (-1.0 / (t * self.sample_rate)).exp()
    }

    /// Recomputes the cached threshold / coefficients from the parameters.
    fn recompute(&mut self) {
        self.threshold_lin = 10.0f32.powf(self.threshold_db / 20.0);
        self.attack_coeff = self.time_coeff(self.attack_ms);
        self.release_coeff = self.time_coeff(self.release_ms);
        self.hold_frames = (self.hold_ms * 0.001 * self.sample_rate).round() as u32;
    }
}

impl Effect for Gate {
    fn effect_type(&self) -> EffectType {
        EffectType::Gate
    }

    fn process(&mut self, buffer: &mut [f32], channels: u16) {
        if !self.enabled || channels == 0 || self.mix < MIX_BYPASS_THRESHOLD {
            return;
        }

        let channels = channels as usize;
        let dry = 1.0 - self.mix;
        let frame_count = buffer.len() / channels;

        for frame in 0..frame_count {
            let base = frame * channels;

            // Detector: peak magnitude across the channels of this frame.
            let mut level = 0.0f32;
            for ch in 0..channels {
                let m = buffer[base + ch].abs();
                if m > level {
                    level = m;
                }
            }

            if level >= self.threshold_lin {
                // Above threshold: open toward unity and refresh the hold window.
                self.gain += (1.0 - self.gain) * self.attack_coeff;
                self.hold_counter = self.hold_frames;
            } else if self.hold_counter > 0 {
                // Within the hold window: keep the gate where it is.
                self.hold_counter -= 1;
            } else {
                // Below threshold and hold elapsed: close toward zero.
                self.gain += (0.0 - self.gain) * self.release_coeff;
            }
            self.gain = denormal::flush(self.gain);

            let g = self.gain;
            for ch in 0..channels {
                let i = base + ch;
                let input = buffer[i];
                buffer[i] = input * dry + (input * g) * self.mix;
            }
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
            PARAM_THRESHOLD => {
                self.threshold_db = value.clamp(-80.0, 0.0);
                self.recompute();
                true
            }
            PARAM_ATTACK => {
                self.attack_ms = value.clamp(0.1, 100.0);
                self.recompute();
                true
            }
            PARAM_RELEASE => {
                self.release_ms = value.clamp(10.0, 2000.0);
                self.recompute();
                true
            }
            PARAM_HOLD => {
                self.hold_ms = value.clamp(0.0, 500.0);
                self.recompute();
                true
            }
            _ => false,
        }
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        match param_id {
            PARAM_ENABLED => Some(if self.enabled { 1.0 } else { 0.0 }),
            PARAM_MIX => Some(self.mix),
            PARAM_THRESHOLD => Some(self.threshold_db),
            PARAM_ATTACK => Some(self.attack_ms),
            PARAM_RELEASE => Some(self.release_ms),
            PARAM_HOLD => Some(self.hold_ms),
            _ => None,
        }
    }

    fn reset(&mut self) {
        self.gain = 0.0;
        self.hold_counter = 0;
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

    /// f64 ground-truth transcription of the production DSP, used to measure the
    /// f32 implementation's numerical fidelity (self-consistency: no C# source).
    struct Reference {
        mix: f64,
        threshold_lin: f64,
        attack_coeff: f64,
        release_coeff: f64,
        hold_frames: u32,
        gain: f64,
        hold_counter: u32,
        channels: usize,
    }

    impl Reference {
        fn new(
            sample_rate: f64,
            channels: usize,
            mix: f64,
            threshold_db: f64,
            attack_ms: f64,
            release_ms: f64,
            hold_ms: f64,
        ) -> Self {
            let coeff = |time_ms: f64| {
                let t = (time_ms * 0.001).max(1.0e-6);
                1.0 - (-1.0 / (t * sample_rate)).exp()
            };
            Self {
                mix,
                threshold_lin: 10.0f64.powf(threshold_db / 20.0),
                attack_coeff: coeff(attack_ms),
                release_coeff: coeff(release_ms),
                hold_frames: (hold_ms * 0.001 * sample_rate).round() as u32,
                gain: 0.0,
                hold_counter: 0,
                channels,
            }
        }

        fn process(&mut self, input: &[f32]) -> Vec<f32> {
            let frame_count = input.len() / self.channels;
            let mut out = vec![0.0f32; input.len()];
            let dry = 1.0 - self.mix;
            for frame in 0..frame_count {
                let base = frame * self.channels;
                let mut level = 0.0f64;
                for ch in 0..self.channels {
                    let m = (input[base + ch] as f64).abs();
                    if m > level {
                        level = m;
                    }
                }
                if level >= self.threshold_lin {
                    self.gain += (1.0 - self.gain) * self.attack_coeff;
                    self.hold_counter = self.hold_frames;
                } else if self.hold_counter > 0 {
                    self.hold_counter -= 1;
                } else {
                    self.gain += (0.0 - self.gain) * self.release_coeff;
                }
                let g = self.gain;
                for ch in 0..self.channels {
                    let i = base + ch;
                    let x = input[i] as f64;
                    out[i] = (x * dry + (x * g) * self.mix) as f32;
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

    /// Stereo signal of loud bursts separated by silence (interleaved), which
    /// exercises the open / hold / close transitions of the gate.
    fn burst_stereo(frames: usize) -> Vec<f32> {
        let mut out = Vec::with_capacity(frames * 2);
        for i in 0..frames {
            let t = i as f32 / 48_000.0;
            // 100 ms loud, 100 ms quiet, repeating.
            let phase = (i / 4_800) % 2;
            let amp = if phase == 0 { 0.6 } else { 0.0005 };
            let v = amp * (2.0 * std::f32::consts::PI * 220.0 * t).sin();
            out.push(v);
            out.push(v * 0.9);
        }
        out
    }

    #[test]
    fn defaults_match_reference() {
        let g = Gate::new(48_000.0);
        assert!(g.is_enabled());
        assert_eq!(g.get_param(PARAM_THRESHOLD), Some(-40.0));
        assert_eq!(g.get_param(PARAM_ATTACK), Some(1.0));
        assert_eq!(g.get_param(PARAM_RELEASE), Some(100.0));
        assert_eq!(g.get_param(PARAM_HOLD), Some(50.0));
        assert_eq!(g.get_param(PARAM_MIX), Some(1.0));
    }

    #[test]
    fn params_clamp_to_ranges() {
        let mut g = Gate::new(48_000.0);
        g.set_param(PARAM_THRESHOLD, 50.0);
        assert_eq!(g.get_param(PARAM_THRESHOLD), Some(0.0));
        g.set_param(PARAM_THRESHOLD, -200.0);
        assert_eq!(g.get_param(PARAM_THRESHOLD), Some(-80.0));
        g.set_param(PARAM_ATTACK, 999.0);
        assert_eq!(g.get_param(PARAM_ATTACK), Some(100.0));
        g.set_param(PARAM_RELEASE, 1.0);
        assert_eq!(g.get_param(PARAM_RELEASE), Some(10.0));
        g.set_param(PARAM_HOLD, 9_999.0);
        assert_eq!(g.get_param(PARAM_HOLD), Some(500.0));
        g.set_param(PARAM_MIX, 2.0);
        assert_eq!(g.get_param(PARAM_MIX), Some(1.0));
    }

    #[test]
    fn unknown_param_is_rejected() {
        let mut g = Gate::new(48_000.0);
        assert!(!g.set_param(999, 1.0));
        assert_eq!(g.get_param(999), None);
    }

    #[test]
    fn disabled_effect_passes_signal_through_untouched() {
        let mut g = Gate::new(48_000.0);
        g.set_enabled(false);
        let input = burst_stereo(512);
        let mut buf = input.clone();
        g.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn zero_mix_passes_signal_through_untouched() {
        let mut g = Gate::new(48_000.0);
        g.set_param(PARAM_MIX, 0.0);
        let input = burst_stereo(512);
        let mut buf = input.clone();
        g.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn loud_signal_opens_quiet_signal_closes() {
        let mut g = Gate::new(48_000.0);
        g.set_param(PARAM_THRESHOLD, -20.0);
        // Loud sustained tone: gate should open (gain → ~1).
        let mut loud: Vec<f32> = (0..9_600)
            .map(|i| 0.5 * (2.0 * std::f32::consts::PI * 200.0 * i as f32 / 48_000.0).sin())
            .collect();
        g.process(&mut loud, 1);
        assert!(g.gain > 0.9, "gate did not open: gain={}", g.gain);

        // Now feed silence past the hold time: gate should close (gain → ~0).
        let mut quiet = vec![0.0f32; 96_000];
        g.process(&mut quiet, 1);
        assert!(g.gain < 0.05, "gate did not close: gain={}", g.gain);
    }

    #[test]
    fn output_is_finite_and_length_preserved() {
        let mut g = Gate::new(48_000.0);
        g.set_param(PARAM_THRESHOLD, -25.0);
        let input = burst_stereo(1_024);
        let mut buf = input.clone();
        g.process(&mut buf, 2);
        assert_eq!(buf.len(), input.len());
        assert!(buf.iter().all(|s| s.is_finite()));
    }

    #[test]
    fn reset_restores_reproducibility() {
        let mut g = Gate::new(48_000.0);
        g.set_param(PARAM_THRESHOLD, -25.0);
        let input = burst_stereo(2_048);

        let mut first = input.clone();
        g.process(&mut first, 2);
        g.reset();
        let mut second = input.clone();
        g.process(&mut second, 2);
        assert_eq!(first, second);
    }

    #[test]
    fn envelope_stays_out_of_subnormals_after_long_decay() {
        let mut g = Gate::new(48_000.0);
        g.set_param(PARAM_THRESHOLD, -20.0);
        let mut loud: Vec<f32> = (0..4_800)
            .map(|i| 0.5 * (2.0 * std::f32::consts::PI * 200.0 * i as f32 / 48_000.0).sin())
            .collect();
        g.process(&mut loud, 1);
        let mut silence = vec![0.0f32; 2_000_000];
        g.process(&mut silence, 1);
        assert!(
            g.gain == 0.0 || g.gain.abs() >= f32::MIN_POSITIVE,
            "envelope {} parked in the subnormal range",
            g.gain
        );
    }

    #[test]
    fn matches_reference_dsp_within_minus_60_db() {
        let input = burst_stereo(16_384);
        let cases = [
            (-40.0f32, 1.0f32, 100.0f32, 50.0f32, 1.0f32),
            (-20.0, 5.0, 200.0, 0.0, 1.0),
            (-30.0, 0.5, 500.0, 100.0, 0.7),
        ];
        for (thr, atk, rel, hold, mix) in cases {
            let mut g = Gate::new(48_000.0);
            g.set_param(PARAM_THRESHOLD, thr);
            g.set_param(PARAM_ATTACK, atk);
            g.set_param(PARAM_RELEASE, rel);
            g.set_param(PARAM_HOLD, hold);
            g.set_param(PARAM_MIX, mix);

            let mut reference = Reference::new(
                48_000.0, 2, mix as f64, thr as f64, atk as f64, rel as f64, hold as f64,
            );

            let mut produced = input.clone();
            g.process(&mut produced, 2);
            let expected = reference.process(&input);

            let err = rms_error_db(&produced, &expected);
            assert!(
                err < -60.0,
                "thr={thr} atk={atk} rel={rel} hold={hold} mix={mix}: RMS error {err:.1} dB exceeds -60 dB"
            );
        }
    }
}
