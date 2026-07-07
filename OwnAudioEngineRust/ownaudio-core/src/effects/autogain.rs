//! Automatic gain control (RMS-based AGC).
//!
//! Rust DSP derived from the reference C# `OwnaudioNET.Effects.AutoGainEffect`: a
//! block RMS detector is smoothed with separate attack / release coefficients;
//! the smoothed level drives a target gain (`target / level`, clamped between the
//! min and max gain) that is applied to a 5 ms look-ahead delayed copy of the
//! signal through a slew-rate limiter, followed by a soft knee near ±0.95 and a
//! hard clamp at ±0.99.  A noise gate freezes the gain while the level sits below
//! the threshold.  Parameter identifiers, ranges and defaults mirror the C#
//! effect.  The effect has no wet/dry mix (it always runs fully wet).
//!
//! **Channel-aware, frame-linked processing (intentional divergence from the C#
//! reference):** the C# effect walks a single detector, gain and look-ahead line
//! across the interleaved samples, so on stereo material the effective look-ahead
//! halves, the 64-sample RMS window is not frame aligned and the two channels
//! receive gains one detector step apart.  This port measures the RMS over a
//! 64-*frame* window from the linked channel energy, drives one gain that is
//! applied identically to every channel of a frame, and delays each channel
//! through its own 5 ms (frame-counted) look-ahead line — so the look-ahead is a
//! full 5 ms per channel, the window is frame aligned and the stereo image never
//! drifts.  Mono input stays bit identical to the per-sample reference; the
//! reported latency is now channel-independent (fixing the plugin delay
//! compensation for mono chains).
//!
//! The smoothed RMS level is recursive, so it is denormal-flushed (2.12): a
//! decaying tail would otherwise park in the subnormal range and stall the CPU
//! on the audio thread.  The per-channel look-ahead lines are pre-allocated
//! during construction and never reallocated on the audio thread.

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};
use crate::denormal;

/// Param ID 2 — target RMS level (0.01 … 1.0).
pub const PARAM_TARGET_LEVEL: u32 = 2;
/// Param ID 3 — attack coefficient (0.9 … 0.999). Higher = slower attack.
pub const PARAM_ATTACK: u32 = 3;
/// Param ID 4 — release coefficient (0.9 … 0.9999). Higher = slower release.
pub const PARAM_RELEASE: u32 = 4;
/// Param ID 5 — maximum gain multiplier (1.0 … 10.0).
pub const PARAM_MAX_GAIN: u32 = 5;
/// Param ID 6 — minimum gain multiplier (0.1 … 1.0).
pub const PARAM_MIN_GAIN: u32 = 6;
/// Param ID 7 — noise gate threshold (0.0001 … 0.01).
pub const PARAM_GATE_THRESHOLD: u32 = 7;

/// Block size (frames) over which the instantaneous RMS is measured.
const RMS_WINDOW_SIZE: usize = 64;
/// Maximum per-frame change in the applied gain (slew limiter).
const GAIN_SLEW_RATE: f32 = 0.001;

/// Highest interleaved channel count with a dedicated look-ahead line (the engine
/// is stereo; any extra channels pass through unprocessed).
const MAX_CHANNELS: usize = 2;

/// Automatic gain control effect.
pub struct AutoGain {
    enabled: bool,
    target_level: f32,
    attack_coeff: f32,
    release_coeff: f32,
    gate_threshold: f32,
    max_gain: f32,
    min_gain: f32,

    current_gain: f32,
    rms_level: f32,
    rms_accumulator: f32,
    rms_frame_count: usize,
    lookahead_buffers: [Vec<f32>; MAX_CHANNELS],
    lookahead_index: usize,
}

impl AutoGain {
    /// Creates a new [`AutoGain`] sized for `sample_rate`, with the reference
    /// default parameters (target 0.25, attack 0.99, release 0.999, gate 0.001,
    /// max gain 4.0, min gain 0.25).
    pub fn new(sample_rate: f32) -> Self {
        let sample_rate = if sample_rate > 0.0 {
            sample_rate
        } else {
            44_100.0
        };
        // 5 ms look-ahead ring buffer per channel (length counted in frames).
        let lookahead_length = (0.005 * sample_rate) as usize;
        Self {
            enabled: true,
            target_level: 0.25,
            attack_coeff: 0.99,
            release_coeff: 0.999,
            gate_threshold: 0.001,
            max_gain: 4.0,
            min_gain: 0.25,
            current_gain: 1.0,
            rms_level: 0.0,
            rms_accumulator: 0.0,
            rms_frame_count: 0,
            lookahead_buffers: [
                vec![0.0; lookahead_length.max(1)],
                vec![0.0; lookahead_length.max(1)],
            ],
            lookahead_index: 0,
        }
    }
}

impl Effect for AutoGain {
    fn effect_type(&self) -> EffectType {
        EffectType::AutoGain
    }

    #[allow(clippy::needless_range_loop)]
    fn process(&mut self, buffer: &mut [f32], channels: u16) {
        if !self.enabled {
            return;
        }

        let att = self.attack_coeff;
        let rel = self.release_coeff;
        let gate = self.gate_threshold;
        let target = self.target_level;
        let max_g = self.max_gain;
        let min_g = self.min_gain;
        let inv_att = 1.0 - att;
        let inv_rel = 1.0 - rel;

        let stride = (channels.max(1)) as usize;
        let ch = stride.min(MAX_CHANNELS);
        let lookahead_length = self.lookahead_buffers[0].len();
        // Mean square is taken over every sample in the window, so mono stays bit
        // identical (window = 64 samples) while stereo averages the linked energy
        // of both channels across 64 frames.
        let window_samples = (RMS_WINDOW_SIZE * ch) as f32;

        let mut rms_level = self.rms_level;
        let mut gain = self.current_gain;
        let mut rms_acc = self.rms_accumulator;
        let mut rms_count = self.rms_frame_count;
        let mut index = self.lookahead_index;

        for frame in buffer.chunks_mut(stride) {
            // 1. Accumulate the linked energy of the whole frame; the window is
            //    counted in frames so it is frame aligned.
            for &s in frame.iter().take(ch) {
                rms_acc += s * s;
            }
            rms_count += 1;

            if rms_count >= RMS_WINDOW_SIZE {
                let current_rms = (rms_acc / window_samples).sqrt();
                if current_rms > rms_level {
                    rms_level = att * rms_level + inv_att * current_rms;
                } else {
                    rms_level = rel * rms_level + inv_rel * current_rms;
                }
                rms_level = denormal::flush(rms_level);
                rms_acc = 0.0;
                rms_count = 0;
            }

            // 2. One gain step per frame, applied identically to every channel so
            //    the stereo balance is preserved.
            if rms_level >= gate {
                let effective_level = rms_level.max(0.0001);
                let target_gain = (target / effective_level).clamp(min_g, max_g);
                let gain_diff = target_gain - gain;
                gain += gain_diff.clamp(-GAIN_SLEW_RATE, GAIN_SLEW_RATE);
            }

            // 3. Per-channel look-ahead delay, then the shared gain, soft knee and
            //    safety clamp.
            for c in 0..ch {
                let delayed_sample = self.lookahead_buffers[c][index];
                self.lookahead_buffers[c][index] = frame[c];

                let mut output = delayed_sample * gain;
                if output > 0.95 {
                    output = 0.95 + (output - 0.95) * 0.1;
                } else if output < -0.95 {
                    output = -0.95 + (output + 0.95) * 0.1;
                }
                frame[c] = output.clamp(-0.99, 0.99);
            }

            index += 1;
            if index >= lookahead_length {
                index = 0;
            }
        }

        self.rms_level = rms_level;
        self.current_gain = gain;
        self.rms_accumulator = rms_acc;
        self.rms_frame_count = rms_count;
        self.lookahead_index = index;
    }

    fn set_param(&mut self, param_id: u32, value: f32) -> bool {
        match param_id {
            PARAM_ENABLED => {
                self.enabled = value >= 0.5;
                true
            }
            // AutoGain has no wet/dry mix; accept and ignore for API symmetry.
            PARAM_MIX => true,
            PARAM_TARGET_LEVEL => {
                self.target_level = value.clamp(0.01, 1.0);
                true
            }
            PARAM_ATTACK => {
                self.attack_coeff = value.clamp(0.9, 0.999);
                true
            }
            PARAM_RELEASE => {
                self.release_coeff = value.clamp(0.9, 0.9999);
                true
            }
            PARAM_MAX_GAIN => {
                self.max_gain = value.clamp(1.0, 10.0);
                true
            }
            PARAM_MIN_GAIN => {
                self.min_gain = value.clamp(0.1, 1.0);
                true
            }
            PARAM_GATE_THRESHOLD => {
                self.gate_threshold = value.clamp(0.0001, 0.01);
                true
            }
            _ => false,
        }
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        match param_id {
            PARAM_ENABLED => Some(if self.enabled { 1.0 } else { 0.0 }),
            PARAM_MIX => Some(1.0),
            PARAM_TARGET_LEVEL => Some(self.target_level),
            PARAM_ATTACK => Some(self.attack_coeff),
            PARAM_RELEASE => Some(self.release_coeff),
            PARAM_MAX_GAIN => Some(self.max_gain),
            PARAM_MIN_GAIN => Some(self.min_gain),
            PARAM_GATE_THRESHOLD => Some(self.gate_threshold),
            _ => None,
        }
    }

    fn reset(&mut self) {
        self.current_gain = 1.0;
        self.rms_level = 0.0;
        self.rms_accumulator = 0.0;
        self.rms_frame_count = 0;
        for line in self.lookahead_buffers.iter_mut() {
            line.iter_mut().for_each(|s| *s = 0.0);
        }
        self.lookahead_index = 0;
    }

    fn is_enabled(&self) -> bool {
        self.enabled
    }

    fn set_enabled(&mut self, enabled: bool) {
        self.enabled = enabled;
    }

    fn latency_samples(&self) -> u32 {
        // Each per-channel look-ahead line delays its channel by its whole length
        // in frames, so the reported latency is the line length and is independent
        // of the channel count — the plugin delay compensation stays correct for
        // both mono and stereo chains.
        self.lookahead_buffers[0].len().max(1) as u32
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// f64 ground-truth transcription of the channel-aware, frame-linked AGC,
    /// carrying the detector, one shared gain and a per-channel look-ahead line,
    /// used to measure the production f32 implementation's numerical fidelity.
    /// The look-ahead is a fixed integer delay (not modulated), so a straight f64
    /// transcription tracks the f32 production to well within the -60 dB bound.
    struct Reference {
        target: f64,
        att: f64,
        rel: f64,
        gate: f64,
        max_g: f64,
        min_g: f64,
        channels: usize,
        current_gain: f64,
        rms_level: f64,
        rms_acc: f64,
        rms_count: usize,
        lookahead: Vec<Vec<f64>>,
        lookahead_index: usize,
    }

    impl Reference {
        fn new(
            sample_rate: f64,
            target: f64,
            att: f64,
            rel: f64,
            gate: f64,
            max_g: f64,
            min_g: f64,
            channels: usize,
        ) -> Self {
            let lookahead_length = (0.005 * sample_rate) as usize;
            Self {
                target,
                att,
                rel,
                gate,
                max_g,
                min_g,
                channels,
                current_gain: 1.0,
                rms_level: 0.0,
                rms_acc: 0.0,
                rms_count: 0,
                lookahead: vec![vec![0.0; lookahead_length.max(1)]; channels],
                lookahead_index: 0,
            }
        }

        fn process(&mut self, buffer: &[f32]) -> Vec<f32> {
            let mut out = vec![0.0f32; buffer.len()];
            let inv_att = 1.0 - self.att;
            let inv_rel = 1.0 - self.rel;
            let lookahead_length = self.lookahead[0].len();
            let slew = GAIN_SLEW_RATE as f64;
            let ch = self.channels;
            let window_samples = (RMS_WINDOW_SIZE * ch) as f64;

            for (out_frame, in_frame) in out.chunks_mut(ch).zip(buffer.chunks(ch)) {
                for &s in in_frame.iter().take(ch) {
                    let x = s as f64;
                    self.rms_acc += x * x;
                }
                self.rms_count += 1;
                if self.rms_count >= RMS_WINDOW_SIZE {
                    let current_rms = (self.rms_acc / window_samples).sqrt();
                    if current_rms > self.rms_level {
                        self.rms_level = self.att * self.rms_level + inv_att * current_rms;
                    } else {
                        self.rms_level = self.rel * self.rms_level + inv_rel * current_rms;
                    }
                    self.rms_acc = 0.0;
                    self.rms_count = 0;
                }

                if self.rms_level >= self.gate {
                    let effective_level = self.rms_level.max(0.0001);
                    let target_gain = (self.target / effective_level).clamp(self.min_g, self.max_g);
                    let gain_diff = target_gain - self.current_gain;
                    self.current_gain += gain_diff.clamp(-slew, slew);
                }

                for c in 0..ch {
                    let delayed_sample = self.lookahead[c][self.lookahead_index];
                    self.lookahead[c][self.lookahead_index] = in_frame[c] as f64;

                    let mut output = delayed_sample * self.current_gain;
                    if output > 0.95 {
                        output = 0.95 + (output - 0.95) * 0.1;
                    } else if output < -0.95 {
                        output = -0.95 + (output + 0.95) * 0.1;
                    }
                    out_frame[c] = output.clamp(-0.99, 0.99) as f32;
                }

                self.lookahead_index = (self.lookahead_index + 1) % lookahead_length;
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

    /// A quiet, slowly swelling stereo tone so the AGC has to pull the gain up
    /// toward the target and exercise the attack / release smoothing.
    fn stereo_swell(frames: usize) -> Vec<f32> {
        let mut v = vec![0.0f32; frames * 2];
        for f in 0..frames {
            let t = f as f32 / 48_000.0;
            let env = 0.05 + 0.1 * (1.0 - (-3.0 * t).exp());
            let s = env * (2.0 * std::f32::consts::PI * 220.0 * t).sin();
            v[f * 2] = s;
            v[f * 2 + 1] = s * 0.9;
        }
        v
    }

    #[test]
    fn defaults_match_reference() {
        let a = AutoGain::new(48_000.0);
        assert_eq!(a.get_param(PARAM_TARGET_LEVEL), Some(0.25));
        assert_eq!(a.get_param(PARAM_ATTACK), Some(0.99));
        assert_eq!(a.get_param(PARAM_RELEASE), Some(0.999));
        assert_eq!(a.get_param(PARAM_GATE_THRESHOLD), Some(0.001));
        assert_eq!(a.get_param(PARAM_MAX_GAIN), Some(4.0));
        assert_eq!(a.get_param(PARAM_MIN_GAIN), Some(0.25));
        assert_eq!(a.get_param(PARAM_MIX), Some(1.0));
        assert!(a.is_enabled());
    }

    #[test]
    fn params_clamp_to_reference_ranges() {
        let mut a = AutoGain::new(48_000.0);
        a.set_param(PARAM_TARGET_LEVEL, 5.0);
        assert_eq!(a.get_param(PARAM_TARGET_LEVEL), Some(1.0));
        a.set_param(PARAM_TARGET_LEVEL, 0.0);
        assert_eq!(a.get_param(PARAM_TARGET_LEVEL), Some(0.01));
        a.set_param(PARAM_ATTACK, 2.0);
        assert_eq!(a.get_param(PARAM_ATTACK), Some(0.999));
        a.set_param(PARAM_MAX_GAIN, 100.0);
        assert_eq!(a.get_param(PARAM_MAX_GAIN), Some(10.0));
        a.set_param(PARAM_MIN_GAIN, 0.0);
        assert_eq!(a.get_param(PARAM_MIN_GAIN), Some(0.1));
        a.set_param(PARAM_GATE_THRESHOLD, 1.0);
        assert_eq!(a.get_param(PARAM_GATE_THRESHOLD), Some(0.01));
    }

    #[test]
    fn unknown_param_is_rejected() {
        let mut a = AutoGain::new(48_000.0);
        assert!(!a.set_param(999, 1.0));
        assert_eq!(a.get_param(999), None);
    }

    #[test]
    fn disabled_effect_passes_signal_through_untouched() {
        let mut a = AutoGain::new(48_000.0);
        a.set_enabled(false);
        let input = stereo_swell(256);
        let mut buf = input.clone();
        a.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn output_is_finite_and_length_preserved() {
        let mut a = AutoGain::new(48_000.0);
        let input = stereo_swell(4_096);
        let mut buf = input.clone();
        a.process(&mut buf, 2);
        assert_eq!(buf.len(), input.len());
        assert!(buf.iter().all(|s| s.is_finite()));
    }

    #[test]
    fn matches_reference_dsp_within_minus_60_db() {
        // 2.2 acceptance: the production f32 AGC must reproduce the f64 ground
        // truth (transcribed from the C# algorithm, carrying the full detector,
        // gain and look-ahead state) to better than -60 dB RMS error across the
        // parameter space.  Production flushes the recursive RMS level out of the
        // subnormal range; the reference does not, but the two differ only at
        // subnormal magnitudes, far below the -60 dB bound.
        let input = stereo_swell(16_384);
        for &(target, att, rel, gate, max_g, min_g) in &[
            (0.25f64, 0.99f64, 0.999f64, 0.001f64, 4.0f64, 0.25f64),
            (0.20, 0.995, 0.9995, 0.002, 2.5, 0.40),
            (0.32, 0.985, 0.995, 0.0005, 4.0, 0.18),
        ] {
            let mut a = AutoGain::new(48_000.0);
            a.set_param(PARAM_TARGET_LEVEL, target as f32);
            a.set_param(PARAM_ATTACK, att as f32);
            a.set_param(PARAM_RELEASE, rel as f32);
            a.set_param(PARAM_GATE_THRESHOLD, gate as f32);
            a.set_param(PARAM_MAX_GAIN, max_g as f32);
            a.set_param(PARAM_MIN_GAIN, min_g as f32);

            let mut produced = input.clone();
            a.process(&mut produced, 2);

            let mut reference = Reference::new(48_000.0, target, att, rel, gate, max_g, min_g, 2);
            let expected = reference.process(&input);
            let err = rms_error_db(&produced, &expected);
            assert!(
                err < -60.0,
                "target={target} att={att} rel={rel} gate={gate} max={max_g} min={min_g}: RMS error {err:.1} dB exceeds -60 dB"
            );
        }
    }

    #[test]
    fn mono_matches_reference() {
        let mut mono = vec![0.0f32; 16_384];
        for (i, s) in mono.iter_mut().enumerate() {
            let t = i as f32 / 48_000.0;
            let env = 0.05 + 0.1 * (1.0 - (-3.0 * t).exp());
            *s = env * (2.0 * std::f32::consts::PI * 220.0 * t).sin();
        }
        let mut a = AutoGain::new(48_000.0);
        let mut produced = mono.clone();
        a.process(&mut produced, 1);

        let mut reference = Reference::new(48_000.0, 0.25, 0.99, 0.999, 0.001, 4.0, 0.25, 1);
        let expected = reference.process(&mono);
        let err = rms_error_db(&produced, &expected);
        assert!(err < -60.0, "mono RMS error {err:.1} dB exceeds -60 dB");
    }

    #[test]
    fn latency_is_channel_independent() {
        let a = AutoGain::new(48_000.0);
        // 5 ms at 48 kHz = 240 frames, reported regardless of the channel count.
        assert_eq!(a.latency_samples(), 240);
    }

    #[test]
    fn reset_clears_state() {
        let mut a = AutoGain::new(48_000.0);
        let input = stereo_swell(1_024);
        let mut first = input.clone();
        a.process(&mut first, 2);
        a.reset();
        let mut second = input.clone();
        a.process(&mut second, 2);
        assert_eq!(first, second);
    }

    #[test]
    fn decaying_level_does_not_produce_subnormals() {
        // A burst of signal then a long silence: the denormal flush on the RMS
        // level must keep the recursive detector state either normal or exactly
        // zero.
        let mut a = AutoGain::new(48_000.0);
        let mut burst = stereo_swell(1_024);
        a.process(&mut burst, 2);
        let mut silence = vec![0.0f32; 2 * 200_000];
        a.process(&mut silence, 2);
        assert!(
            a.rms_level == 0.0 || a.rms_level.abs() >= f32::MIN_POSITIVE,
            "subnormal in RMS level: {:e}",
            a.rms_level
        );
    }
}
