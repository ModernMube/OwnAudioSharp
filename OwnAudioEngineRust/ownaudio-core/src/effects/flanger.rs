//! Flanger with a short LFO-modulated delay and feedback.
//!
//! Rust DSP derived from the reference C# `OwnaudioNET.Effects.FlangerEffect`: a
//! sub-20 ms circular delay line read back at a **fractional** tap whose length
//! sweeps between 1 ms and 10 ms under one global LFO scaled by the depth; the
//! delayed sample is fed back into the line (hard-clamped to ±1) and mixed with
//! the dry signal.  Parameter identifiers, ranges and defaults mirror the C#
//! effect.
//!
//! **Fractional tap (B.5.1 — intentional divergence from the C# reference):** the
//! C# effect (and this port's earlier revision) truncated the swept tap to a
//! whole sample, so the sweep advanced in ±1-sample steps — an audible "zipper"
//! staircase.  This port reads the tap with linear interpolation between the two
//! neighbouring samples, so the sweep is continuous.  The reference DSP compared
//! against is the fractional algorithm in f64.
//!
//! **Channel-aware, frame-linked processing (intentional divergence from the C#
//! reference):** the C# effect steps a single delay line and LFO across the
//! interleaved samples, so on stereo material the tap sweeps at twice the
//! intended rate and its parity flips channel every sample (an audible L↔R
//! flutter).  This port keeps an independent delay line per channel with a shared
//! read/write frame index and advances the LFO once per frame, so both channels
//! see the same tap and the sweep runs at the correct rate.  Mono input stays bit
//! identical to the per-sample reference; stereo is compared against a
//! frame-linked f64 ground truth.
//!
//! The feedback path is recursive, so the value written back into each delay line
//! is denormal-flushed (2.12): a decaying tail would otherwise park in the
//! subnormal range and stall the CPU on the audio thread.  The per-channel delay
//! lines are pre-allocated during construction and never reallocated on the audio
//! thread.

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};
use crate::denormal;
use crate::smoothing::{RampedParam, DEFAULT_SMOOTH_MS};

/// Param ID 2 — LFO modulation rate in Hz (0.1 … 5.0).
pub const PARAM_RATE: u32 = 2;
/// Param ID 3 — modulation depth (0.0 … 1.0).
pub const PARAM_DEPTH: u32 = 3;
/// Param ID 4 — feedback amount (0.0 … 0.95).
pub const PARAM_FEEDBACK: u32 = 4;

/// Highest interleaved channel count with a dedicated delay line (the engine is
/// stereo; any extra channels pass through unprocessed).
const MAX_CHANNELS: usize = 2;

/// Flanger with a short modulated delay and feedback.
pub struct Flanger {
    enabled: bool,
    mix: f32,
    rate_hz: f32,
    depth: f32,
    feedback: f32,
    sample_rate: f32,

    delay_buffers: [Vec<f32>; MAX_CHANNELS],
    buffer_index: usize,
    lfo_phase: f32,
    mix_ramp: RampedParam,
}

impl Flanger {
    /// Creates a new [`Flanger`] sized for `sample_rate`, with the reference
    /// default parameters (rate 0.5 Hz, depth 0.8, feedback 0.6, mix 0.5).
    pub fn new(sample_rate: f32) -> Self {
        let sample_rate = if sample_rate > 0.0 {
            sample_rate
        } else {
            44_100.0
        };
        // 20 ms circular delay line per channel, covering the 1–10 ms modulated tap.
        let max_delay_samples = (0.02 * sample_rate) as usize;
        Self {
            enabled: true,
            mix: 0.5,
            rate_hz: 0.5,
            depth: 0.8,
            feedback: 0.6,
            sample_rate,
            delay_buffers: [
                vec![0.0; max_delay_samples.max(2)],
                vec![0.0; max_delay_samples.max(2)],
            ],
            buffer_index: 0,
            lfo_phase: 0.0,
            mix_ramp: RampedParam::new(0.5, sample_rate, DEFAULT_SMOOTH_MS),
        }
    }
}

impl Effect for Flanger {
    fn effect_type(&self) -> EffectType {
        EffectType::Flanger
    }

    #[allow(clippy::needless_range_loop)]
    fn process(&mut self, buffer: &mut [f32], channels: u16) {
        self.mix_ramp.begin_block();
        if !self.enabled {
            return;
        }

        let stride = (channels.max(1)) as usize;
        let ch = stride.min(MAX_CHANNELS);
        let buf_len = self.delay_buffers[0].len();
        let two_pi = std::f32::consts::PI * 2.0;

        let depth = self.depth;
        let feedback = self.feedback;
        let lfo_increment = two_pi * self.rate_hz / self.sample_rate;

        let mut lfo_phase = self.lfo_phase;
        let mut buffer_index = self.buffer_index;

        for frame in buffer.chunks_mut(stride) {
            let mix = self.mix_ramp.advance();

            let lfo_value = lfo_phase.sin();
            let delay_time = 0.001 + 0.009 * (1.0 + lfo_value * depth) * 0.5;
            let delay_frames = (delay_time * self.sample_rate).clamp(1.0, (buf_len - 1) as f32);

            // Fractional read position one delay length behind the writer.
            let mut read_pos = buffer_index as f32 - delay_frames;
            while read_pos < 0.0 {
                read_pos += buf_len as f32;
            }
            let idx_a = read_pos as usize;
            let idx_b = if idx_a + 1 >= buf_len { 0 } else { idx_a + 1 };
            let frac = read_pos - idx_a as f32;

            for c in 0..ch {
                let input = frame[c];
                let line = &mut self.delay_buffers[c];
                let delayed_sample = line[idx_a] + frac * (line[idx_b] - line[idx_a]);

                let feedback_sample = input + delayed_sample * feedback;
                line[buffer_index] = denormal::flush(feedback_sample.clamp(-1.0, 1.0));

                frame[c] = input * (1.0 - mix) + delayed_sample * mix;
            }

            buffer_index = (buffer_index + 1) % buf_len;
            lfo_phase += lfo_increment;
            if lfo_phase >= two_pi {
                lfo_phase -= two_pi;
            }
        }

        self.buffer_index = buffer_index;
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
                self.rate_hz = value.clamp(0.1, 5.0);
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
            _ => None,
        }
    }

    fn reset(&mut self) {
        for line in self.delay_buffers.iter_mut() {
            line.iter_mut().for_each(|s| *s = 0.0);
        }
        self.buffer_index = 0;
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

    /// f64 ground-truth transcription of the channel-aware, frame-linked flanger
    /// with a fractional (linearly interpolated) tap, used to measure the
    /// production f32 implementation's numerical fidelity.
    ///
    /// The fractional read position is derived from the f32 LFO phase exactly as
    /// production does (so the interpolation weights match), while the audio-sample
    /// arithmetic (interpolation, feedback accumulation, clamp, mix) is elevated to
    /// f64 — the smooth quantity the -60 dB bound measures.  The delay lines and
    /// LFO are carried per channel and advanced once per frame, mirroring
    /// production.
    struct Reference {
        sample_rate: f32,
        depth: f32,
        mix: f64,
        feedback: f64,
        rate: f32,
        channels: usize,
        bufs: Vec<Vec<f64>>,
        buffer_index: usize,
        lfo_phase: f32,
    }

    impl Reference {
        fn new(
            sample_rate: f32,
            rate: f32,
            depth: f32,
            feedback: f64,
            mix: f64,
            channels: usize,
        ) -> Self {
            let max_delay_samples = (0.02 * sample_rate) as usize;
            Self {
                sample_rate,
                depth,
                mix,
                feedback,
                rate,
                channels,
                bufs: vec![vec![0.0; max_delay_samples.max(2)]; channels],
                buffer_index: 0,
                lfo_phase: 0.0,
            }
        }

        fn process(&mut self, buffer: &[f32]) -> Vec<f32> {
            let mut out = vec![0.0f32; buffer.len()];
            let buf_len = self.bufs[0].len();
            let two_pi = std::f32::consts::PI * 2.0;
            let lfo_increment = two_pi * self.rate / self.sample_rate;
            let ch = self.channels;

            for (out_frame, in_frame) in out.chunks_mut(ch).zip(buffer.chunks(ch)) {
                // Fractional read position — f32 path to production for the tap.
                let lfo_value = self.lfo_phase.sin();
                let delay_time = 0.001 + 0.009 * (1.0 + lfo_value * self.depth) * 0.5;
                let delay_frames = (delay_time * self.sample_rate).clamp(1.0, (buf_len - 1) as f32);
                let mut read_pos = self.buffer_index as f32 - delay_frames;
                while read_pos < 0.0 {
                    read_pos += buf_len as f32;
                }
                let idx_a = read_pos as usize;
                let idx_b = if idx_a + 1 >= buf_len { 0 } else { idx_a + 1 };
                let frac = (read_pos - idx_a as f32) as f64;

                for c in 0..ch {
                    let input = in_frame[c] as f64;
                    let delayed_sample =
                        self.bufs[c][idx_a] + frac * (self.bufs[c][idx_b] - self.bufs[c][idx_a]);

                    let feedback_sample = input + delayed_sample * self.feedback;
                    self.bufs[c][self.buffer_index] = feedback_sample.clamp(-1.0, 1.0);

                    out_frame[c] = (input * (1.0 - self.mix) + delayed_sample * self.mix) as f32;
                }

                self.buffer_index = (self.buffer_index + 1) % buf_len;
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
        let f = Flanger::new(48_000.0);
        assert_eq!(f.get_param(PARAM_RATE), Some(0.5));
        assert_eq!(f.get_param(PARAM_DEPTH), Some(0.8));
        assert_eq!(f.get_param(PARAM_FEEDBACK), Some(0.6));
        assert_eq!(f.get_param(PARAM_MIX), Some(0.5));
        assert!(f.is_enabled());
    }

    #[test]
    fn params_clamp_to_reference_ranges() {
        let mut f = Flanger::new(48_000.0);
        f.set_param(PARAM_RATE, 100.0);
        assert_eq!(f.get_param(PARAM_RATE), Some(5.0));
        f.set_param(PARAM_RATE, 0.0);
        assert_eq!(f.get_param(PARAM_RATE), Some(0.1));
        f.set_param(PARAM_DEPTH, 5.0);
        assert_eq!(f.get_param(PARAM_DEPTH), Some(1.0));
        f.set_param(PARAM_FEEDBACK, 5.0);
        assert_eq!(f.get_param(PARAM_FEEDBACK), Some(0.95));
        f.set_param(PARAM_MIX, 2.0);
        assert_eq!(f.get_param(PARAM_MIX), Some(1.0));
    }

    #[test]
    fn unknown_param_is_rejected() {
        let mut f = Flanger::new(48_000.0);
        assert!(!f.set_param(999, 1.0));
        assert_eq!(f.get_param(999), None);
    }

    #[test]
    fn disabled_effect_passes_signal_through_untouched() {
        let mut f = Flanger::new(48_000.0);
        f.set_enabled(false);
        let input = stereo_pluck(256);
        let mut buf = input.clone();
        f.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn zero_mix_passes_signal_through_untouched() {
        // Mix = 0 keeps the dry signal but the flanger still runs its recursive
        // feedback line, so this only asserts the output equals the input for the
        // reference's mix-scaled blend — with mix 0 the wet term vanishes.
        let mut f = Flanger::new(48_000.0);
        f.set_param(PARAM_MIX, 0.0);
        let input = stereo_pluck(256);
        let mut buf = input.clone();
        f.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn output_is_finite_and_length_preserved() {
        let mut f = Flanger::new(48_000.0);
        let input = stereo_pluck(2_048);
        let mut buf = input.clone();
        f.process(&mut buf, 2);
        assert_eq!(buf.len(), input.len());
        assert!(buf.iter().all(|s| s.is_finite()));
    }

    #[test]
    fn matches_reference_dsp_within_minus_60_db() {
        // 2.2 acceptance: the production f32 flanger must reproduce the f64 ground
        // truth (the channel-aware, frame-linked algorithm carrying a delay line
        // per channel and a per-frame LFO) to better than -60 dB RMS error across
        // the parameter space.  Production flushes the recursive feedback state
        // out of the subnormal range; the reference does not, but the two differ
        // only at subnormal magnitudes, far below the -60 dB bound.
        let input = stereo_pluck(8_192);
        for &(rate, depth, feedback, mix) in &[
            (0.5f64, 0.8f64, 0.6f64, 0.5f64),
            (0.7, 0.75, 0.65, 0.45),
            (2.8, 0.95, 0.85, 0.65),
            (0.25, 0.35, 0.25, 0.30),
        ] {
            let mut f = Flanger::new(48_000.0);
            f.set_param(PARAM_RATE, rate as f32);
            f.set_param(PARAM_DEPTH, depth as f32);
            f.set_param(PARAM_FEEDBACK, feedback as f32);
            f.set_param(PARAM_MIX, mix as f32);

            let mut produced = input.clone();
            f.process(&mut produced, 2);

            let mut reference =
                Reference::new(48_000.0, rate as f32, depth as f32, feedback, mix, 2);
            let expected = reference.process(&input);
            let err = rms_error_db(&produced, &expected);
            assert!(
                err < -60.0,
                "rate={rate} depth={depth} feedback={feedback} mix={mix}: RMS error {err:.1} dB exceeds -60 dB"
            );
        }
    }

    #[test]
    fn mono_matches_frame_linked_reference() {
        let mut mono = vec![0.0f32; 4_096];
        for (i, s) in mono.iter_mut().enumerate() {
            let t = i as f32 / 48_000.0;
            *s = 0.5 * (-1.5 * t).exp() * (2.0 * std::f32::consts::PI * 220.0 * t).sin();
        }
        let mut f = Flanger::new(48_000.0);
        let mut produced = mono.clone();
        f.process(&mut produced, 1);

        let mut reference = Reference::new(48_000.0, 0.5, 0.8, 0.6, 0.5, 1);
        let expected = reference.process(&mono);
        let err = rms_error_db(&produced, &expected);
        assert!(err < -60.0, "mono RMS error {err:.1} dB exceeds -60 dB");
    }

    #[test]
    fn reset_clears_state() {
        let mut f = Flanger::new(48_000.0);
        let input = stereo_pluck(512);
        let mut first = input.clone();
        f.process(&mut first, 2);
        f.reset();
        let mut second = input.clone();
        f.process(&mut second, 2);
        assert_eq!(first, second);
    }

    #[test]
    fn decaying_feedback_does_not_produce_subnormals() {
        // A high-feedback flanger fed a single impulse then a long silent tail:
        // the denormal flush on the stored feedback must keep the recursive state
        // either normal or exactly zero.
        let mut f = Flanger::new(48_000.0);
        f.set_param(PARAM_FEEDBACK, 0.95);
        f.set_param(PARAM_MIX, 1.0);
        let mut impulse = vec![0.0f32; 2];
        impulse[0] = 1.0;
        impulse[1] = 1.0;
        f.process(&mut impulse, 2);
        let mut silence = vec![0.0f32; 2 * 200_000];
        f.process(&mut silence, 2);
        for line in &f.delay_buffers {
            for &s in line {
                assert!(
                    s == 0.0 || s.abs() >= f32::MIN_POSITIVE,
                    "subnormal leaked into delay state: {s:e}"
                );
            }
        }
    }
}
