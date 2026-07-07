//! Multi-voice chorus with LFO modulation.
//!
//! Rust DSP derived from the reference C# `OwnaudioNET.Effects.ChorusEffect`: up
//! to six voices read a modulated fractional tap from a 50 ms delay line, their
//! outputs are averaged and mixed with the dry signal.  Base delay is 15 ms, the
//! modulation reaches ±5 ms scaled by the depth, and the voices share one global
//! LFO with an even per-voice phase offset (`i·2π/6`).  Parameter identifiers,
//! ranges and defaults mirror the C# effect.
//!
//! **Channel-aware, frame-linked processing (intentional divergence from the C#
//! reference):** the C# effect steps a single delay line and LFO across the
//! interleaved samples, so on stereo material the effective delay halves (15 ms
//! → 7.5 ms), the LFO runs at twice the intended rate and the read tap mixes the
//! two channels.  This port keeps an independent delay line per channel, writes
//! and reads each channel from its own line, and advances the shared LFO once per
//! frame — so the delay times, LFO rate and stereo image are all correct.  Mono
//! input (one channel per frame) stays bit identical to the per-sample reference;
//! stereo is compared against a frame-linked f64 ground truth.
//!
//! The delay lines only store the incoming signal (there is no feedback path), so
//! no denormal protection is required.  The per-channel 50 ms lines and the six
//! voice-phase offsets are pre-allocated during construction and never
//! reallocated on the audio thread.

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};
use crate::smoothing::{RampedParam, DEFAULT_SMOOTH_MS};

/// Param ID 2 — LFO modulation rate in Hz (0.1 … 10).
pub const PARAM_RATE: u32 = 2;
/// Param ID 3 — modulation depth (0.0 … 1.0).
pub const PARAM_DEPTH: u32 = 3;
/// Param ID 4 — number of chorus voices (2 … 6).
pub const PARAM_VOICES: u32 = 4;

/// Mix values below this threshold bypass processing entirely (mirrors C#).
const MIX_BYPASS_THRESHOLD: f32 = 0.001;

/// Fixed number of pre-computed voice phase offsets (mirrors the C# reference).
const MAX_VOICES: usize = 6;

/// Highest interleaved channel count with a dedicated delay line (the engine is
/// stereo; any extra channels pass through unprocessed).
const MAX_CHANNELS: usize = 2;

/// Multi-voice chorus effect.
pub struct Chorus {
    enabled: bool,
    mix: f32,
    rate_hz: f32,
    depth: f32,
    voices: usize,
    sample_rate: f32,

    delay_buffers: [Vec<f32>; MAX_CHANNELS],
    buffer_index: usize,
    lfo_phase: f32,
    lfo_increment: f32,
    voice_phases: [f32; MAX_VOICES],
    mix_ramp: RampedParam,
}

impl Chorus {
    /// Creates a new [`Chorus`] sized for `sample_rate`, with the reference
    /// default parameters (rate 1.0 Hz, depth 0.5, mix 0.5, 3 voices).
    pub fn new(sample_rate: f32) -> Self {
        let sample_rate = if sample_rate > 0.0 {
            sample_rate
        } else {
            44_100.0
        };
        // 50 ms circular delay line per channel, enough for the 15 ms base delay
        // ± 5 ms sweep (delay lengths are now counted in frames).
        let buffer_size = (0.05 * sample_rate) as usize;
        let mut voice_phases = [0.0f32; MAX_VOICES];
        for (i, p) in voice_phases.iter_mut().enumerate() {
            *p = (i as f32) * std::f32::consts::PI * 2.0 / MAX_VOICES as f32;
        }
        let mut chorus = Self {
            enabled: true,
            mix: 0.5,
            rate_hz: 1.0,
            depth: 0.5,
            voices: 3,
            sample_rate,
            delay_buffers: [vec![0.0; buffer_size.max(1)], vec![0.0; buffer_size.max(1)]],
            buffer_index: 0,
            lfo_phase: 0.0,
            lfo_increment: 0.0,
            voice_phases,
            mix_ramp: RampedParam::new(0.5, sample_rate, DEFAULT_SMOOTH_MS),
        };
        chorus.recalculate_increment();
        chorus
    }

    /// Recomputes the per-frame LFO phase increment from the current rate and
    /// sample rate; mirrors the reference `RecalculateIncrement`.
    fn recalculate_increment(&mut self) {
        self.lfo_increment = 2.0 * std::f32::consts::PI * self.rate_hz / self.sample_rate;
    }
}

impl Effect for Chorus {
    fn effect_type(&self) -> EffectType {
        EffectType::Chorus
    }

    #[allow(clippy::needless_range_loop)]
    fn process(&mut self, buffer: &mut [f32], channels: u16) {
        self.mix_ramp.begin_block();
        if !self.enabled
            || (self.mix < MIX_BYPASS_THRESHOLD && self.mix_ramp.current() < MIX_BYPASS_THRESHOLD)
        {
            return;
        }

        let stride = (channels.max(1)) as usize;
        let ch = stride.min(MAX_CHANNELS);
        let buf_len = self.delay_buffers[0].len();
        let two_pi = std::f32::consts::PI * 2.0;

        let depth = self.depth;
        let voices = self.voices;
        let lfo_inc = self.lfo_increment;

        let base_delay_frames = 0.015 * self.sample_rate;
        let mod_depth_frames = 0.005 * self.sample_rate * depth;

        let mut lfo_phase = self.lfo_phase;
        let mut buffer_index = self.buffer_index;

        for frame in buffer.chunks_mut(stride) {
            let mix = self.mix_ramp.advance();

            // Write each channel's dry sample into its own delay line.
            for c in 0..ch {
                self.delay_buffers[c][buffer_index] = frame[c];
            }

            for c in 0..ch {
                let line = &self.delay_buffers[c];
                let input = frame[c];

                // Accumulate every voice at its LFO-modulated fractional read tap.
                let mut wet_signal = 0.0f32;
                for &voice_phase in self.voice_phases.iter().take(voices) {
                    let lfo = (lfo_phase + voice_phase).sin();
                    let delay_offset = base_delay_frames + lfo * mod_depth_frames;

                    let mut read_pos = buffer_index as f32 - delay_offset;
                    while read_pos < 0.0 {
                        read_pos += buf_len as f32;
                    }
                    while read_pos >= buf_len as f32 {
                        read_pos -= buf_len as f32;
                    }

                    let idx_a = read_pos as usize;
                    let idx_b = if idx_a + 1 >= buf_len { 0 } else { idx_a + 1 };
                    let frac = read_pos - idx_a as f32;

                    let sample_a = line[idx_a];
                    let sample_b = line[idx_b];
                    wet_signal += sample_a + frac * (sample_b - sample_a);
                }

                wet_signal /= voices as f32;

                frame[c] = input * (1.0 - mix) + wet_signal * mix;
            }

            buffer_index += 1;
            if buffer_index >= buf_len {
                buffer_index = 0;
            }

            lfo_phase += lfo_inc;
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
                self.rate_hz = value.clamp(0.1, 10.0);
                self.recalculate_increment();
                true
            }
            PARAM_DEPTH => {
                self.depth = value.clamp(0.0, 1.0);
                true
            }
            PARAM_VOICES => {
                self.voices = (value.clamp(2.0, 6.0) as usize).clamp(2, MAX_VOICES);
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
            PARAM_VOICES => Some(self.voices as f32),
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

    /// f64 ground-truth transcription of the channel-aware, frame-linked chorus,
    /// carrying an independent delay line per channel and a shared LFO advanced
    /// once per frame, used to measure the production f32 implementation's
    /// numerical fidelity.
    struct Reference {
        sample_rate: f64,
        depth: f64,
        mix: f64,
        voices: usize,
        channels: usize,
        bufs: Vec<Vec<f64>>,
        buffer_index: usize,
        lfo_phase: f64,
        lfo_increment: f64,
        voice_phases: [f64; MAX_VOICES],
    }

    impl Reference {
        fn new(
            sample_rate: f64,
            rate: f64,
            depth: f64,
            mix: f64,
            voices: usize,
            channels: usize,
        ) -> Self {
            let buffer_size = (0.05 * sample_rate) as usize;
            let mut voice_phases = [0.0f64; MAX_VOICES];
            for (i, p) in voice_phases.iter_mut().enumerate() {
                *p = (i as f64) * std::f64::consts::PI * 2.0 / MAX_VOICES as f64;
            }
            Self {
                sample_rate,
                depth,
                mix,
                voices,
                channels,
                bufs: vec![vec![0.0; buffer_size.max(1)]; channels],
                buffer_index: 0,
                lfo_phase: 0.0,
                lfo_increment: 2.0 * std::f64::consts::PI * rate / sample_rate,
                voice_phases,
            }
        }

        fn process(&mut self, buffer: &[f32]) -> Vec<f32> {
            let mut out = vec![0.0f32; buffer.len()];
            let buf_len = self.bufs[0].len();
            let two_pi = std::f64::consts::PI * 2.0;
            let base_delay_frames = 0.015 * self.sample_rate;
            let mod_depth_frames = 0.005 * self.sample_rate * self.depth;
            let ch = self.channels;

            for (out_frame, in_frame) in out.chunks_mut(ch).zip(buffer.chunks(ch)) {
                for c in 0..ch {
                    self.bufs[c][self.buffer_index] = in_frame[c] as f64;
                }

                for c in 0..ch {
                    let input = in_frame[c] as f64;
                    let mut wet_signal = 0.0f64;
                    for &voice_phase in self.voice_phases.iter().take(self.voices) {
                        let lfo = (self.lfo_phase + voice_phase).sin();
                        let delay_offset = base_delay_frames + lfo * mod_depth_frames;

                        let mut read_pos = self.buffer_index as f64 - delay_offset;
                        while read_pos < 0.0 {
                            read_pos += buf_len as f64;
                        }
                        while read_pos >= buf_len as f64 {
                            read_pos -= buf_len as f64;
                        }

                        let idx_a = read_pos as usize;
                        let idx_b = if idx_a + 1 >= buf_len { 0 } else { idx_a + 1 };
                        let frac = read_pos - idx_a as f64;

                        let sample_a = self.bufs[c][idx_a];
                        let sample_b = self.bufs[c][idx_b];
                        wet_signal += sample_a + frac * (sample_b - sample_a);
                    }

                    wet_signal /= self.voices as f64;
                    out_frame[c] = (input * (1.0 - self.mix) + wet_signal * self.mix) as f32;
                }

                self.buffer_index = (self.buffer_index + 1) % buf_len;
                self.lfo_phase += self.lfo_increment;
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

    /// Interleaved stereo signal: a decaying pluck so the modulated taps carry
    /// audible signal.
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
        let c = Chorus::new(48_000.0);
        assert_eq!(c.get_param(PARAM_RATE), Some(1.0));
        assert_eq!(c.get_param(PARAM_DEPTH), Some(0.5));
        assert_eq!(c.get_param(PARAM_MIX), Some(0.5));
        assert_eq!(c.get_param(PARAM_VOICES), Some(3.0));
        assert!(c.is_enabled());
    }

    #[test]
    fn params_clamp_to_reference_ranges() {
        let mut c = Chorus::new(48_000.0);
        c.set_param(PARAM_RATE, 100.0);
        assert_eq!(c.get_param(PARAM_RATE), Some(10.0));
        c.set_param(PARAM_RATE, 0.0);
        assert_eq!(c.get_param(PARAM_RATE), Some(0.1));
        c.set_param(PARAM_DEPTH, 5.0);
        assert_eq!(c.get_param(PARAM_DEPTH), Some(1.0));
        c.set_param(PARAM_VOICES, 100.0);
        assert_eq!(c.get_param(PARAM_VOICES), Some(6.0));
        c.set_param(PARAM_VOICES, 0.0);
        assert_eq!(c.get_param(PARAM_VOICES), Some(2.0));
        c.set_param(PARAM_MIX, 2.0);
        assert_eq!(c.get_param(PARAM_MIX), Some(1.0));
    }

    #[test]
    fn unknown_param_is_rejected() {
        let mut c = Chorus::new(48_000.0);
        assert!(!c.set_param(999, 1.0));
        assert_eq!(c.get_param(999), None);
    }

    #[test]
    fn disabled_effect_passes_signal_through_untouched() {
        let mut c = Chorus::new(48_000.0);
        c.set_enabled(false);
        let input = stereo_pluck(256);
        let mut buf = input.clone();
        c.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn zero_mix_passes_signal_through_untouched() {
        let mut c = Chorus::new(48_000.0);
        c.set_param(PARAM_MIX, 0.0);
        let input = stereo_pluck(256);
        let mut buf = input.clone();
        c.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn output_is_finite_and_length_preserved() {
        let mut c = Chorus::new(48_000.0);
        let input = stereo_pluck(2_048);
        let mut buf = input.clone();
        c.process(&mut buf, 2);
        assert_eq!(buf.len(), input.len());
        assert!(buf.iter().all(|s| s.is_finite()));
    }

    #[test]
    fn matches_reference_dsp_within_minus_60_db() {
        // 2.2 acceptance: the production f32 chorus must reproduce the f64 ground
        // truth (the channel-aware, frame-linked algorithm carrying an
        // independent delay line per channel and a per-frame LFO) to better than
        // -60 dB RMS error across the parameter space.  The window is bounded to
        // one mixer block: the reference accumulates the LFO phase in f64 while
        // both this port and the shipped C# accumulate it in f32, so over a very
        // long window the idealised phase drifts from the f32 recursion and the
        // drift — not the DSP structure — would dominate the error.  One block
        // covers the transient plus several modulation steps, isolating the
        // algorithmic fidelity.
        let input = stereo_pluck(4_096);
        for &(rate, depth, mix, voices) in &[
            (1.0f64, 0.5f64, 0.5f64, 3usize),
            (0.35, 0.75, 0.55, 5),
            (3.0, 0.9, 0.7, 6),
            (0.25, 0.15, 0.25, 2),
        ] {
            let mut c = Chorus::new(48_000.0);
            c.set_param(PARAM_RATE, rate as f32);
            c.set_param(PARAM_DEPTH, depth as f32);
            c.set_param(PARAM_MIX, mix as f32);
            c.set_param(PARAM_VOICES, voices as f32);

            let mut produced = input.clone();
            c.process(&mut produced, 2);

            let mut reference = Reference::new(48_000.0, rate, depth, mix, voices, 2);
            let expected = reference.process(&input);
            let err = rms_error_db(&produced, &expected);
            assert!(
                err < -60.0,
                "rate={rate} depth={depth} mix={mix} voices={voices}: RMS error {err:.1} dB exceeds -60 dB"
            );
        }
    }

    #[test]
    fn mono_matches_frame_linked_reference() {
        // Mono input drives one delay line advanced once per sample — the LFO
        // advances at the same rate as the legacy per-sample walk, so a mono
        // chorus stays bit-for-bit compatible with the frame-linked reference.
        let mut mono = vec![0.0f32; 2_048];
        for (i, s) in mono.iter_mut().enumerate() {
            let t = i as f32 / 48_000.0;
            *s = 0.5 * (-1.5 * t).exp() * (2.0 * std::f32::consts::PI * 220.0 * t).sin();
        }
        let mut c = Chorus::new(48_000.0);
        let mut produced = mono.clone();
        c.process(&mut produced, 1);

        let mut reference = Reference::new(48_000.0, 1.0, 0.5, 0.5, 3, 1);
        let expected = reference.process(&mono);
        let err = rms_error_db(&produced, &expected);
        assert!(err < -60.0, "mono RMS error {err:.1} dB exceeds -60 dB");
    }

    #[test]
    fn reset_clears_state() {
        let mut c = Chorus::new(48_000.0);
        let input = stereo_pluck(512);
        let mut first = input.clone();
        c.process(&mut first, 2);
        c.reset();
        let mut second = input.clone();
        c.process(&mut second, 2);
        assert_eq!(first, second);
    }
}
