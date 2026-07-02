//! Stereo delay (echo) with ping-pong and feedback damping.
//!
//! Faithful Rust port of the reference C# `OwnaudioNET.Effects.DelayEffect`: a
//! fractional-delay stereo echo with a one-pole damping low-pass in the feedback
//! path, a hard feedback clamp to prevent runaway, and an optional ping-pong
//! cross-feed.  Parameter identifiers, ranges, defaults and the per-sample DSP
//! mirror the C# effect so the two are numerically equivalent (the basis of the
//! 2.2 reference comparison).
//!
//! The delay line is sized for 5 s at the construction sample rate and never
//! reallocated on the audio thread.  The damping filter state is denormal
//! protected (2.12) — a decaying echo tail would otherwise drift into the
//! subnormal range and stall the CPU on the audio thread.

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};
use crate::denormal;
use crate::smoothing::{RampedParam, DEFAULT_SMOOTH_MS};

/// Param ID 2 — delay time in milliseconds (1 … 5000).
pub const PARAM_TIME_MS: u32 = 2;
/// Param ID 3 — feedback / repeat amount (0.0 … 1.0).
pub const PARAM_FEEDBACK: u32 = 3;
/// Param ID 4 — feedback damping (0.0 … 1.0). Higher = darker repeats.
pub const PARAM_DAMPING: u32 = 4;
/// Param ID 5 — ping-pong mode (0.0 = off, 1.0 = on).
pub const PARAM_PING_PONG: u32 = 5;

/// Mix values below this threshold bypass processing entirely (mirrors C#).
const MIX_BYPASS_THRESHOLD: f32 = 0.001;

/// Stereo delay / echo effect.
pub struct Delay {
    enabled: bool,
    mix: f32,
    time_ms: f32,
    feedback: f32,
    damping: f32,
    ping_pong: bool,
    sample_rate: f32,

    delay_buffer_l: Vec<f32>,
    delay_buffer_r: Vec<f32>,
    write_index: usize,
    last_output_l: f32,
    last_output_r: f32,
    delay_samples: f32,
    mix_ramp: RampedParam,
}

impl Delay {
    /// Creates a new [`Delay`] sized for `sample_rate`, with the reference
    /// default parameters (time 375 ms, feedback 0.35, mix 0.30, damping 0.25,
    /// ping-pong off).
    pub fn new(sample_rate: f32) -> Self {
        let capacity = (5.0 * sample_rate) as usize;
        let mut delay = Self {
            enabled: true,
            mix: 0.30,
            time_ms: 375.0,
            feedback: 0.35,
            damping: 0.25,
            ping_pong: false,
            sample_rate,
            delay_buffer_l: vec![0.0; capacity.max(1)],
            delay_buffer_r: vec![0.0; capacity.max(1)],
            write_index: 0,
            last_output_l: 0.0,
            last_output_r: 0.0,
            delay_samples: 0.0,
            mix_ramp: RampedParam::new(0.30, sample_rate, DEFAULT_SMOOTH_MS),
        };
        delay.update_delay_samples();
        delay
    }

    /// Recomputes the fractional delay length from the current time and sample
    /// rate; mirrors the reference `UpdateDelaySamples`.
    fn update_delay_samples(&mut self) {
        self.delay_samples = (self.time_ms / 1000.0) * self.sample_rate;
    }
}

impl Effect for Delay {
    fn effect_type(&self) -> EffectType {
        EffectType::Delay
    }

    fn process(&mut self, buffer: &mut [f32], channels: u16) {
        // The reference effect only processes stereo material.
        self.mix_ramp.begin_block();
        if !self.enabled
            || (self.mix < MIX_BYPASS_THRESHOLD && self.mix_ramp.current() < MIX_BYPASS_THRESHOLD)
            || channels != 2
        {
            return;
        }

        let buf_len = self.delay_buffer_l.len();
        let frame_count = buffer.len() / 2;

        let rep = self.feedback;
        let damp = self.damping;
        let ds = self.delay_samples;
        let pp = self.ping_pong;

        let mut last_l = self.last_output_l;
        let mut last_r = self.last_output_r;

        for frame in 0..frame_count {
            let mx = self.mix_ramp.advance();
            let idx_l = frame * 2;
            let idx_r = frame * 2 + 1;

            let input_l = buffer[idx_l];
            let input_r = buffer[idx_r];

            // Fractional read position, one delay length behind the writer.
            let mut read_pos = self.write_index as f32 - ds;
            if read_pos < 0.0 {
                read_pos += buf_len as f32;
            }
            let read_idx_a = read_pos as usize;
            let mut read_idx_b = read_idx_a + 1;
            if read_idx_b >= buf_len {
                read_idx_b = 0;
            }
            let frac = read_pos - read_idx_a as f32;

            let delayed_l = self.delay_buffer_l[read_idx_a]
                + frac * (self.delay_buffer_l[read_idx_b] - self.delay_buffer_l[read_idx_a]);
            let delayed_r = self.delay_buffer_r[read_idx_a]
                + frac * (self.delay_buffer_r[read_idx_b] - self.delay_buffer_r[read_idx_a]);

            // One-pole damping low-pass in the feedback path; denormal-flushed so
            // the decaying tail never drifts into the subnormal range (2.12).
            let damped_l = denormal::flush(last_l + damp * (delayed_l - last_l));
            let damped_r = denormal::flush(last_r + damp * (delayed_r - last_r));
            last_l = damped_l;
            last_r = damped_r;

            // Feedback, hard-limited to prevent runaway.
            let feedback_l = (damped_l * rep).clamp(-1.0, 1.0);
            let feedback_r = (damped_r * rep).clamp(-1.0, 1.0);

            if pp {
                self.delay_buffer_l[self.write_index] = input_l + feedback_r;
                self.delay_buffer_r[self.write_index] = input_r + feedback_l;
            } else {
                self.delay_buffer_l[self.write_index] = input_l + feedback_l;
                self.delay_buffer_r[self.write_index] = input_r + feedback_r;
            }

            buffer[idx_l] = input_l * (1.0 - mx) + damped_l * mx;
            buffer[idx_r] = input_r * (1.0 - mx) + damped_r * mx;

            self.write_index += 1;
            if self.write_index >= buf_len {
                self.write_index = 0;
            }
        }

        self.last_output_l = last_l;
        self.last_output_r = last_r;
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
            PARAM_TIME_MS => {
                self.time_ms = value.clamp(1.0, 5000.0);
                self.update_delay_samples();
                true
            }
            PARAM_FEEDBACK => {
                self.feedback = value.clamp(0.0, 1.0);
                true
            }
            PARAM_DAMPING => {
                self.damping = value.clamp(0.0, 1.0);
                true
            }
            PARAM_PING_PONG => {
                self.ping_pong = value >= 0.5;
                true
            }
            _ => false,
        }
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        match param_id {
            PARAM_ENABLED => Some(if self.enabled { 1.0 } else { 0.0 }),
            PARAM_MIX => Some(self.mix),
            PARAM_TIME_MS => Some(self.time_ms),
            PARAM_FEEDBACK => Some(self.feedback),
            PARAM_DAMPING => Some(self.damping),
            PARAM_PING_PONG => Some(if self.ping_pong { 1.0 } else { 0.0 }),
            _ => None,
        }
    }

    fn reset(&mut self) {
        self.delay_buffer_l.iter_mut().for_each(|s| *s = 0.0);
        self.delay_buffer_r.iter_mut().for_each(|s| *s = 0.0);
        self.write_index = 0;
        self.last_output_l = 0.0;
        self.last_output_r = 0.0;
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

    /// f64 ground-truth transcription of the reference C# delay, carrying the
    /// full delay line and damping state, used to measure the production f32
    /// implementation's numerical fidelity.
    struct Reference {
        rep: f64,
        mix: f64,
        damp: f64,
        ds: f64,
        ping_pong: bool,
        buf_l: Vec<f64>,
        buf_r: Vec<f64>,
        write_index: usize,
        last_l: f64,
        last_r: f64,
    }

    impl Reference {
        fn new(sample_rate: f64, time_ms: f64, rep: f64, mix: f64, damp: f64, ping_pong: bool) -> Self {
            let capacity = (5.0 * sample_rate) as usize;
            Self {
                rep,
                mix,
                damp,
                ds: (time_ms / 1000.0) * sample_rate,
                ping_pong,
                buf_l: vec![0.0; capacity.max(1)],
                buf_r: vec![0.0; capacity.max(1)],
                write_index: 0,
                last_l: 0.0,
                last_r: 0.0,
            }
        }

        fn process(&mut self, buffer: &[f32]) -> Vec<f32> {
            let mut out = vec![0.0f32; buffer.len()];
            let frames = buffer.len() / 2;
            let buf_len = self.buf_l.len();
            for frame in 0..frames {
                let idx_l = frame * 2;
                let idx_r = frame * 2 + 1;
                let input_l = buffer[idx_l] as f64;
                let input_r = buffer[idx_r] as f64;

                let mut read_pos = self.write_index as f64 - self.ds;
                if read_pos < 0.0 {
                    read_pos += buf_len as f64;
                }
                let read_idx_a = read_pos as usize;
                let read_idx_b = if read_idx_a + 1 >= buf_len { 0 } else { read_idx_a + 1 };
                let frac = read_pos - read_idx_a as f64;

                let delayed_l = self.buf_l[read_idx_a] + frac * (self.buf_l[read_idx_b] - self.buf_l[read_idx_a]);
                let delayed_r = self.buf_r[read_idx_a] + frac * (self.buf_r[read_idx_b] - self.buf_r[read_idx_a]);

                let damped_l = self.last_l + self.damp * (delayed_l - self.last_l);
                let damped_r = self.last_r + self.damp * (delayed_r - self.last_r);
                self.last_l = damped_l;
                self.last_r = damped_r;

                let feedback_l = (damped_l * self.rep).clamp(-1.0, 1.0);
                let feedback_r = (damped_r * self.rep).clamp(-1.0, 1.0);

                if self.ping_pong {
                    self.buf_l[self.write_index] = input_l + feedback_r;
                    self.buf_r[self.write_index] = input_r + feedback_l;
                } else {
                    self.buf_l[self.write_index] = input_l + feedback_l;
                    self.buf_r[self.write_index] = input_r + feedback_r;
                }

                out[idx_l] = (input_l * (1.0 - self.mix) + damped_l * self.mix) as f32;
                out[idx_r] = (input_r * (1.0 - self.mix) + damped_r * self.mix) as f32;

                self.write_index = (self.write_index + 1) % buf_len;
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

    /// Interleaved stereo signal: a decaying pluck so the echoes are audible.
    fn stereo_pluck(frames: usize) -> Vec<f32> {
        let mut v = vec![0.0f32; frames * 2];
        for f in 0..frames {
            let t = f as f32 / 48_000.0;
            let env = (-3.0 * t).exp();
            let s = 0.6 * env * (2.0 * std::f32::consts::PI * 440.0 * t).sin();
            v[f * 2] = s;
            v[f * 2 + 1] = s * 0.8; // slight stereo difference
        }
        v
    }

    #[test]
    fn defaults_match_reference() {
        let d = Delay::new(48_000.0);
        assert_eq!(d.get_param(PARAM_TIME_MS), Some(375.0));
        assert_eq!(d.get_param(PARAM_FEEDBACK), Some(0.35));
        assert_eq!(d.get_param(PARAM_MIX), Some(0.30));
        assert_eq!(d.get_param(PARAM_DAMPING), Some(0.25));
        assert_eq!(d.get_param(PARAM_PING_PONG), Some(0.0));
        assert!(d.is_enabled());
    }

    #[test]
    fn params_clamp_to_reference_ranges() {
        let mut d = Delay::new(48_000.0);
        d.set_param(PARAM_TIME_MS, 100_000.0);
        assert_eq!(d.get_param(PARAM_TIME_MS), Some(5000.0));
        d.set_param(PARAM_TIME_MS, 0.0);
        assert_eq!(d.get_param(PARAM_TIME_MS), Some(1.0));
        d.set_param(PARAM_FEEDBACK, 5.0);
        assert_eq!(d.get_param(PARAM_FEEDBACK), Some(1.0));
        d.set_param(PARAM_FEEDBACK, -1.0);
        assert_eq!(d.get_param(PARAM_FEEDBACK), Some(0.0));
        d.set_param(PARAM_MIX, 2.0);
        assert_eq!(d.get_param(PARAM_MIX), Some(1.0));
    }

    #[test]
    fn unknown_param_is_rejected() {
        let mut d = Delay::new(48_000.0);
        assert!(!d.set_param(999, 1.0));
        assert_eq!(d.get_param(999), None);
    }

    #[test]
    fn disabled_effect_passes_signal_through_untouched() {
        let mut d = Delay::new(48_000.0);
        d.set_enabled(false);
        let input = stereo_pluck(256);
        let mut buf = input.clone();
        d.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn zero_mix_passes_signal_through_untouched() {
        let mut d = Delay::new(48_000.0);
        d.set_param(PARAM_MIX, 0.0);
        let input = stereo_pluck(256);
        let mut buf = input.clone();
        d.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn non_stereo_is_left_untouched() {
        let mut d = Delay::new(48_000.0);
        let input = vec![0.5f32; 256];
        let mut buf = input.clone();
        d.process(&mut buf, 1);
        assert_eq!(buf, input);
    }

    #[test]
    fn output_is_finite_and_length_preserved() {
        let mut d = Delay::new(48_000.0);
        let input = stereo_pluck(2_048);
        let mut buf = input.clone();
        d.process(&mut buf, 2);
        assert_eq!(buf.len(), input.len());
        assert!(buf.iter().all(|s| s.is_finite()));
    }

    #[test]
    fn matches_reference_dsp_within_minus_60_db() {
        // 2.2 acceptance: the production f32 delay must reproduce the f64 ground
        // truth (transcribed from the C# algorithm, carrying the full delay line
        // and damping state) to better than -60 dB RMS error across the
        // parameter space.  Short delay times keep several echo repeats inside
        // the analysed window.
        let input = stereo_pluck(8_192);
        for &(time, rep, mix, damp, pp) in &[
            (60.0f64, 0.35f64, 0.30f64, 0.25f64, false),
            (40.0, 0.55, 0.45, 0.40, false),
            (50.0, 0.48, 0.42, 0.12, true),
        ] {
            let mut d = Delay::new(48_000.0);
            d.set_param(PARAM_TIME_MS, time as f32);
            d.set_param(PARAM_FEEDBACK, rep as f32);
            d.set_param(PARAM_MIX, mix as f32);
            d.set_param(PARAM_DAMPING, damp as f32);
            d.set_param(PARAM_PING_PONG, if pp { 1.0 } else { 0.0 });

            let mut produced = input.clone();
            d.process(&mut produced, 2);

            let mut reference = Reference::new(48_000.0, time, rep, mix, damp, pp);
            let expected = reference.process(&input);
            let err = rms_error_db(&produced, &expected);
            assert!(
                err < -60.0,
                "time={time} rep={rep} mix={mix} damp={damp} pp={pp}: RMS error {err:.1} dB exceeds -60 dB"
            );
        }
    }

    #[test]
    fn reset_clears_state() {
        let mut d = Delay::new(48_000.0);
        let input = stereo_pluck(512);
        let mut first = input.clone();
        d.process(&mut first, 2);
        d.reset();
        let mut second = input.clone();
        d.process(&mut second, 2);
        assert_eq!(first, second);
    }

    #[test]
    fn decaying_echo_does_not_produce_subnormals() {
        // A high-feedback echo fed a single impulse, then a long silent tail:
        // the denormal protection in the damping state must keep the feedback
        // filter state and output either normal or exactly zero.
        let mut d = Delay::new(48_000.0);
        d.set_param(PARAM_TIME_MS, 10.0);
        d.set_param(PARAM_FEEDBACK, 0.7);
        d.set_param(PARAM_DAMPING, 0.5);
        d.set_param(PARAM_MIX, 1.0);
        let mut impulse = vec![0.0f32; 2];
        impulse[0] = 1.0;
        impulse[1] = 1.0;
        d.process(&mut impulse, 2);
        let mut silence = vec![0.0f32; 2 * 300_000];
        d.process(&mut silence, 2);
        for &s in &silence {
            assert!(
                s == 0.0 || s.abs() >= f32::MIN_POSITIVE,
                "subnormal leaked into output: {s:e}"
            );
        }
        assert!(
            d.last_output_l == 0.0 || d.last_output_l.abs() >= f32::MIN_POSITIVE,
            "subnormal in damping state: {:e}",
            d.last_output_l
        );
    }
}
