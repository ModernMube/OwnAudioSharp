//! Freeverb-based algorithmic reverb.
//!
//! Faithful Rust port of the reference C# `OwnaudioNET.Effects.ReverbEffect`:
//! an extended Freeverb (8 parallel damped comb filters + 4 serial all-pass
//! filters per channel) with a stereo spread, a 20 ms mono pre-delay and an
//! input gain.  Parameter identifiers, ranges, defaults and the per-sample DSP
//! mirror the C# effect so the two implementations are numerically equivalent
//! (the basis of the 2.2 reference comparison).
//!
//! All delay-line buffers are sized for the construction sample rate and never
//! reallocated on the audio thread.  The comb filters' damping low-pass state is
//! denormal-protected (2.12) — a decaying reverb tail would otherwise drift into
//! the subnormal range and stall the CPU on the audio thread.

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};
use crate::denormal;
use crate::smoothing::{RampedParam, DEFAULT_SMOOTH_MS};

/// Param ID 2 — room size (0.0 … 1.0). Larger values lengthen the tail.
pub const PARAM_ROOM_SIZE: u32 = 2;
/// Param ID 3 — damping (0.0 … 1.0). Higher values darken the tail.
pub const PARAM_DAMPING: u32 = 3;
/// Param ID 4 — stereo width (0.0 … 2.0).
pub const PARAM_WIDTH: u32 = 4;
/// Param ID 5 — wet level (0.0 … 1.0).
pub const PARAM_WET_LEVEL: u32 = 5;
/// Param ID 6 — dry level (0.0 … 1.0).
pub const PARAM_DRY_LEVEL: u32 = 6;

const NUM_COMBS: usize = 8;
const NUM_ALLPASSES: usize = 4;
const SCALE_WET: f32 = 3.0;
const SCALE_DAMP: f32 = 0.4;
const SCALE_ROOM: f32 = 0.28;
const OFFSET_ROOM: f32 = 0.7;
const STEREO_SPREAD: i32 = 23;

/// Comb-filter tunings (in samples at 44.1 kHz) for the left channel.
const COMB_TUNING_L: [i32; NUM_COMBS] = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617];
/// All-pass tunings (in samples at 44.1 kHz) for the left channel.
const ALLPASS_TUNING_L: [i32; NUM_ALLPASSES] = [556, 441, 341, 225];

/// Extended-Freeverb algorithmic reverb effect.
pub struct Reverb {
    enabled: bool,
    mix: f32,
    room_size: f32,
    damping: f32,
    width: f32,
    wet_level: f32,
    dry_level: f32,
    gain: f32,

    // Comb filters: [channel][filter] delay line + write index + damping store.
    comb_buffers: [[Vec<f32>; NUM_COMBS]; 2],
    comb_indices: [[usize; NUM_COMBS]; 2],
    comb_filter_store: [[f32; NUM_COMBS]; 2],

    // All-pass filters: [channel][filter] delay line + write index.
    allpass_buffers: [[Vec<f32>; NUM_ALLPASSES]; 2],
    allpass_indices: [[usize; NUM_ALLPASSES]; 2],

    // 20 ms mono pre-delay.
    pre_delay_buffer: Vec<f32>,
    pre_delay_index: usize,

    // Cached coefficients derived from the parameters.
    room_size_val: f32,
    damp_val: f32,
    wet1: f32,
    wet2: f32,
    mix_ramp: RampedParam,
}

impl Reverb {
    /// Creates a new [`Reverb`] sized for `sample_rate`, with the reference
    /// default parameters (room 0.5, damping 0.5, width 1.0, wet 0.33,
    /// dry 0.67, mix 0.5, gain 1.0).
    pub fn new(sample_rate: f32) -> Self {
        let scale = sample_rate / 44_100.0;

        // Left channel uses the base tunings, right adds the stereo spread.
        let comb_buffers: [[Vec<f32>; NUM_COMBS]; 2] = [
            std::array::from_fn(|i| sized_line(COMB_TUNING_L[i], scale)),
            std::array::from_fn(|i| sized_line(COMB_TUNING_L[i] + STEREO_SPREAD, scale)),
        ];
        let allpass_buffers: [[Vec<f32>; NUM_ALLPASSES]; 2] = [
            std::array::from_fn(|i| sized_line(ALLPASS_TUNING_L[i], scale)),
            std::array::from_fn(|i| sized_line(ALLPASS_TUNING_L[i] + STEREO_SPREAD, scale)),
        ];

        let pre_delay_len = (0.020 * sample_rate) as usize;

        let mut reverb = Self {
            enabled: true,
            mix: 0.5,
            room_size: 0.5,
            damping: 0.5,
            width: 1.0,
            wet_level: 0.33,
            dry_level: 0.67,
            gain: 1.0,
            comb_buffers,
            comb_indices: [[0; NUM_COMBS]; 2],
            comb_filter_store: [[0.0; NUM_COMBS]; 2],
            allpass_buffers,
            allpass_indices: [[0; NUM_ALLPASSES]; 2],
            pre_delay_buffer: vec![0.0; pre_delay_len.max(1)],
            pre_delay_index: 0,
            room_size_val: 0.0,
            damp_val: 0.0,
            wet1: 0.0,
            wet2: 0.0,
            mix_ramp: RampedParam::new(0.5, sample_rate, DEFAULT_SMOOTH_MS),
        };
        reverb.update_coefficients();
        reverb
    }

    /// Recomputes the cached coefficients from the current parameters; mirrors
    /// the reference `UpdateCoefficients`.
    fn update_coefficients(&mut self) {
        self.room_size_val = self.room_size * SCALE_ROOM + OFFSET_ROOM;
        self.damp_val = self.damping * SCALE_DAMP;
        self.wet1 = self.wet_level * (0.5 * self.width + 0.5);
        self.wet2 = self.wet_level * ((1.0 - self.width) * 0.5);
    }
}

/// Builds a zeroed delay line whose length is `tuning` scaled by `scale`,
/// clamped to at least one sample so indexing never panics.
fn sized_line(tuning: i32, scale: f32) -> Vec<f32> {
    let size = (tuning as f32 * scale) as usize;
    vec![0.0; size.max(1)]
}

impl Effect for Reverb {
    fn effect_type(&self) -> EffectType {
        EffectType::Reverb
    }

    fn process(&mut self, buffer: &mut [f32], channels: u16) {
        self.mix_ramp.begin_block();
        if !self.enabled || channels == 0 {
            return;
        }

        let channels = channels as usize;
        let frame_count = buffer.len() / channels;
        let is_stereo = channels >= 2;

        let room = self.room_size_val;
        let damp = self.damp_val;
        let g = self.gain;
        let dry = self.dry_level;
        let w1 = self.wet1;
        let w2 = self.wet2;

        for frame in 0..frame_count {
            let idx = frame * channels;
            let mix = self.mix_ramp.advance();

            let input_l = buffer[idx];
            let input_r = if is_stereo { buffer[idx + 1] } else { input_l };

            // Input mix to mono, gain, then 20 ms pre-delay.
            let mut input_mono = (input_l + input_r) * 0.5 * g;
            let delayed_input = self.pre_delay_buffer[self.pre_delay_index];
            self.pre_delay_buffer[self.pre_delay_index] = input_mono;
            self.pre_delay_index += 1;
            if self.pre_delay_index >= self.pre_delay_buffer.len() {
                self.pre_delay_index = 0;
            }
            input_mono = delayed_input;

            // Parallel comb filters per channel (dual-mono for stereo width).
            let mut out_l = 0.0f32;
            let mut out_r = 0.0f32;

            for i in 0..NUM_COMBS {
                let buf = &mut self.comb_buffers[0][i];
                let b_idx = self.comb_indices[0][i];
                let output = buf[b_idx];
                let store = output * (1.0 - damp) + self.comb_filter_store[0][i] * damp;
                let store = denormal::flush(store);
                self.comb_filter_store[0][i] = store;
                buf[b_idx] = denormal::flush(input_mono + store * room);
                let next = b_idx + 1;
                self.comb_indices[0][i] = if next >= buf.len() { 0 } else { next };
                out_l += output;
            }

            for i in 0..NUM_COMBS {
                let buf = &mut self.comb_buffers[1][i];
                let b_idx = self.comb_indices[1][i];
                let output = buf[b_idx];
                let store = output * (1.0 - damp) + self.comb_filter_store[1][i] * damp;
                let store = denormal::flush(store);
                self.comb_filter_store[1][i] = store;
                buf[b_idx] = denormal::flush(input_mono + store * room);
                let next = b_idx + 1;
                self.comb_indices[1][i] = if next >= buf.len() { 0 } else { next };
                out_r += output;
            }

            // Serial all-pass filters per channel.
            for i in 0..NUM_ALLPASSES {
                let buf = &mut self.allpass_buffers[0][i];
                let b_idx = self.allpass_indices[0][i];
                let buf_out = buf[b_idx];
                let processed = out_l;
                buf[b_idx] = denormal::flush(processed + buf_out * 0.5);
                out_l = -0.5 * processed + buf_out;
                let next = b_idx + 1;
                self.allpass_indices[0][i] = if next >= buf.len() { 0 } else { next };
            }

            for i in 0..NUM_ALLPASSES {
                let buf = &mut self.allpass_buffers[1][i];
                let b_idx = self.allpass_indices[1][i];
                let buf_out = buf[b_idx];
                let processed = out_r;
                buf[b_idx] = denormal::flush(processed + buf_out * 0.5);
                out_r = -0.5 * processed + buf_out;
                let next = b_idx + 1;
                self.allpass_indices[1][i] = if next >= buf.len() { 0 } else { next };
            }

            // Stereo wet mix with spread, internal wet scaling, dry blend.
            let wet_l = (out_l * w1 + out_r * w2) * SCALE_WET;
            let wet_r = (out_r * w1 + out_l * w2) * SCALE_WET;
            let blended_l = input_l * dry + wet_l;
            let blended_r = input_r * dry + wet_r;

            buffer[idx] = input_l * (1.0 - mix) + blended_l * mix;
            if is_stereo {
                buffer[idx + 1] = input_r * (1.0 - mix) + blended_r * mix;
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
                self.mix_ramp.set(self.mix);
                true
            }
            PARAM_ROOM_SIZE => {
                self.room_size = value.clamp(0.0, 1.0);
                self.update_coefficients();
                true
            }
            PARAM_DAMPING => {
                self.damping = value.clamp(0.0, 1.0);
                self.update_coefficients();
                true
            }
            PARAM_WIDTH => {
                self.width = value.clamp(0.0, 2.0);
                self.update_coefficients();
                true
            }
            PARAM_WET_LEVEL => {
                self.wet_level = value.clamp(0.0, 1.0);
                self.update_coefficients();
                true
            }
            PARAM_DRY_LEVEL => {
                self.dry_level = value.clamp(0.0, 1.0);
                true
            }
            _ => false,
        }
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        match param_id {
            PARAM_ENABLED => Some(if self.enabled { 1.0 } else { 0.0 }),
            PARAM_MIX => Some(self.mix),
            PARAM_ROOM_SIZE => Some(self.room_size),
            PARAM_DAMPING => Some(self.damping),
            PARAM_WIDTH => Some(self.width),
            PARAM_WET_LEVEL => Some(self.wet_level),
            PARAM_DRY_LEVEL => Some(self.dry_level),
            _ => None,
        }
    }

    fn reset(&mut self) {
        for ch in 0..2 {
            for i in 0..NUM_COMBS {
                self.comb_buffers[ch][i].iter_mut().for_each(|s| *s = 0.0);
                self.comb_indices[ch][i] = 0;
                self.comb_filter_store[ch][i] = 0.0;
            }
            for i in 0..NUM_ALLPASSES {
                self.allpass_buffers[ch][i]
                    .iter_mut()
                    .for_each(|s| *s = 0.0);
                self.allpass_indices[ch][i] = 0;
            }
        }
        self.pre_delay_buffer.iter_mut().for_each(|s| *s = 0.0);
        self.pre_delay_index = 0;
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

    /// f64 ground-truth transcription of the reference C# Freeverb, carrying the
    /// full comb/all-pass/pre-delay state, used to measure the production f32
    /// implementation's numerical fidelity.
    struct Reference {
        room: f64,
        damp: f64,
        gain: f64,
        dry: f64,
        mix: f64,
        wet1: f64,
        wet2: f64,
        comb_buffers: [[Vec<f64>; NUM_COMBS]; 2],
        comb_indices: [[usize; NUM_COMBS]; 2],
        comb_filter_store: [[f64; NUM_COMBS]; 2],
        allpass_buffers: [[Vec<f64>; NUM_ALLPASSES]; 2],
        allpass_indices: [[usize; NUM_ALLPASSES]; 2],
        pre_delay: Vec<f64>,
        pre_delay_index: usize,
    }

    impl Reference {
        fn new(
            sample_rate: f64,
            room_size: f64,
            damping: f64,
            width: f64,
            wet: f64,
            dry: f64,
            mix: f64,
        ) -> Self {
            let scale = sample_rate / 44_100.0;
            let line = |tuning: i32| vec![0.0f64; (((tuning as f64) * scale) as usize).max(1)];
            Self {
                room: room_size * SCALE_ROOM as f64 + OFFSET_ROOM as f64,
                damp: damping * SCALE_DAMP as f64,
                gain: 1.0,
                dry,
                mix,
                wet1: wet * (0.5 * width + 0.5),
                wet2: wet * ((1.0 - width) * 0.5),
                comb_buffers: [
                    std::array::from_fn(|i| line(COMB_TUNING_L[i])),
                    std::array::from_fn(|i| line(COMB_TUNING_L[i] + STEREO_SPREAD)),
                ],
                comb_indices: [[0; NUM_COMBS]; 2],
                comb_filter_store: [[0.0; NUM_COMBS]; 2],
                allpass_buffers: [
                    std::array::from_fn(|i| line(ALLPASS_TUNING_L[i])),
                    std::array::from_fn(|i| line(ALLPASS_TUNING_L[i] + STEREO_SPREAD)),
                ],
                allpass_indices: [[0; NUM_ALLPASSES]; 2],
                pre_delay: vec![0.0; ((0.020 * sample_rate) as usize).max(1)],
                pre_delay_index: 0,
            }
        }

        fn process(&mut self, buffer: &[f32]) -> Vec<f32> {
            let mut out = vec![0.0f32; buffer.len()];
            let frames = buffer.len() / 2;
            for frame in 0..frames {
                let idx = frame * 2;
                let input_l = buffer[idx] as f64;
                let input_r = buffer[idx + 1] as f64;

                let mut input_mono = (input_l + input_r) * 0.5 * self.gain;
                let delayed = self.pre_delay[self.pre_delay_index];
                self.pre_delay[self.pre_delay_index] = input_mono;
                self.pre_delay_index = (self.pre_delay_index + 1) % self.pre_delay.len();
                input_mono = delayed;

                let mut out_l = 0.0;
                let mut out_r = 0.0;
                for ch in 0..2 {
                    let acc = if ch == 0 { &mut out_l } else { &mut out_r };
                    for i in 0..NUM_COMBS {
                        let b_idx = self.comb_indices[ch][i];
                        let output = self.comb_buffers[ch][i][b_idx];
                        let store =
                            output * (1.0 - self.damp) + self.comb_filter_store[ch][i] * self.damp;
                        self.comb_filter_store[ch][i] = store;
                        self.comb_buffers[ch][i][b_idx] = input_mono + store * self.room;
                        let len = self.comb_buffers[ch][i].len();
                        self.comb_indices[ch][i] = (b_idx + 1) % len;
                        *acc += output;
                    }
                }
                for ch in 0..2 {
                    let acc = if ch == 0 { &mut out_l } else { &mut out_r };
                    for i in 0..NUM_ALLPASSES {
                        let b_idx = self.allpass_indices[ch][i];
                        let buf_out = self.allpass_buffers[ch][i][b_idx];
                        let processed = *acc;
                        self.allpass_buffers[ch][i][b_idx] = processed + buf_out * 0.5;
                        *acc = -0.5 * processed + buf_out;
                        let len = self.allpass_buffers[ch][i].len();
                        self.allpass_indices[ch][i] = (b_idx + 1) % len;
                    }
                }

                let wet_l = (out_l * self.wet1 + out_r * self.wet2) * SCALE_WET as f64;
                let wet_r = (out_r * self.wet1 + out_l * self.wet2) * SCALE_WET as f64;
                let blended_l = input_l * self.dry + wet_l;
                let blended_r = input_r * self.dry + wet_r;
                out[idx] = (input_l * (1.0 - self.mix) + blended_l * self.mix) as f32;
                out[idx + 1] = (input_r * (1.0 - self.mix) + blended_r * self.mix) as f32;
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

    /// Interleaved stereo sine, moderately loud, to excite the reverb tail.
    fn stereo_sine(frames: usize) -> Vec<f32> {
        let mut v = vec![0.0f32; frames * 2];
        for f in 0..frames {
            let t = f as f32 / 48_000.0;
            let s = 0.5 * (2.0 * std::f32::consts::PI * 330.0 * t).sin();
            v[f * 2] = s;
            v[f * 2 + 1] = s;
        }
        v
    }

    #[test]
    fn defaults_match_reference() {
        let r = Reverb::new(48_000.0);
        assert_eq!(r.get_param(PARAM_ROOM_SIZE), Some(0.5));
        assert_eq!(r.get_param(PARAM_DAMPING), Some(0.5));
        assert_eq!(r.get_param(PARAM_WIDTH), Some(1.0));
        assert_eq!(r.get_param(PARAM_WET_LEVEL), Some(0.33));
        assert_eq!(r.get_param(PARAM_DRY_LEVEL), Some(0.67));
        assert_eq!(r.get_param(PARAM_MIX), Some(0.5));
        assert!(r.is_enabled());
    }

    #[test]
    fn params_clamp_to_reference_ranges() {
        let mut r = Reverb::new(48_000.0);
        r.set_param(PARAM_ROOM_SIZE, 5.0);
        assert_eq!(r.get_param(PARAM_ROOM_SIZE), Some(1.0));
        r.set_param(PARAM_ROOM_SIZE, -1.0);
        assert_eq!(r.get_param(PARAM_ROOM_SIZE), Some(0.0));
        r.set_param(PARAM_WIDTH, 5.0);
        assert_eq!(r.get_param(PARAM_WIDTH), Some(2.0));
        r.set_param(PARAM_WIDTH, -1.0);
        assert_eq!(r.get_param(PARAM_WIDTH), Some(0.0));
        r.set_param(PARAM_MIX, 2.0);
        assert_eq!(r.get_param(PARAM_MIX), Some(1.0));
    }

    #[test]
    fn unknown_param_is_rejected() {
        let mut r = Reverb::new(48_000.0);
        assert!(!r.set_param(999, 1.0));
        assert_eq!(r.get_param(999), None);
    }

    #[test]
    fn disabled_effect_passes_signal_through_untouched() {
        let mut r = Reverb::new(48_000.0);
        r.set_enabled(false);
        let input = stereo_sine(256);
        let mut buf = input.clone();
        r.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn output_is_finite_and_length_preserved() {
        let mut r = Reverb::new(48_000.0);
        let input = stereo_sine(1024);
        let mut buf = input.clone();
        r.process(&mut buf, 2);
        assert_eq!(buf.len(), input.len());
        assert!(buf.iter().all(|s| s.is_finite()));
    }

    #[test]
    fn matches_reference_dsp_within_minus_60_db() {
        // 2.2 acceptance: the production f32 Freeverb must reproduce the f64
        // ground truth (transcribed from the C# algorithm, carrying the full
        // comb/all-pass/pre-delay state) to better than -60 dB RMS error.  The
        // production path additionally flushes subnormals in the comb store
        // (2.12); the f64 reference does not, but flushed values are ~1e-38 and
        // stay far below the -60 dB floor.
        let input = stereo_sine(4_096);
        for &(room, damp, width, wet, dry, mix) in &[
            (0.5f64, 0.5f64, 1.0f64, 0.33f64, 0.67f64, 0.5f64),
            (0.85, 0.45, 1.0, 0.45, 0.70, 0.6),
            (0.30, 0.65, 0.6, 0.18, 0.90, 0.4),
        ] {
            let mut r = Reverb::new(48_000.0);
            r.set_param(PARAM_ROOM_SIZE, room as f32);
            r.set_param(PARAM_DAMPING, damp as f32);
            r.set_param(PARAM_WIDTH, width as f32);
            r.set_param(PARAM_WET_LEVEL, wet as f32);
            r.set_param(PARAM_DRY_LEVEL, dry as f32);
            r.set_param(PARAM_MIX, mix as f32);

            let mut produced = input.clone();
            r.process(&mut produced, 2);

            let mut reference = Reference::new(48_000.0, room, damp, width, wet, dry, mix);
            let expected = reference.process(&input);
            let err = rms_error_db(&produced, &expected);
            assert!(
                err < -60.0,
                "room={room} damp={damp} width={width}: RMS error {err:.1} dB exceeds -60 dB"
            );
        }
    }

    #[test]
    fn reset_clears_state() {
        // The reverb is a long feedback network; after a reset the same input
        // must reproduce the first run exactly.
        let mut r = Reverb::new(48_000.0);
        let input = stereo_sine(512);
        let mut first = input.clone();
        r.process(&mut first, 2);
        r.reset();
        let mut second = input.clone();
        r.process(&mut second, 2);
        assert_eq!(first, second);
    }

    #[test]
    fn decaying_tail_does_not_leave_subnormals_in_state() {
        // Feed an impulse, then let the tail decay to silence over a long run.
        // The 2.12 guarantee is about the *recursive state*: every value that
        // recirculates (comb damping store, comb delay line, all-pass delay
        // line) must stay normal or be exactly zero, so a fading-out reverb
        // never parks the feedback network in the subnormal range and stalls
        // the audio thread.
        let mut r = Reverb::new(48_000.0);
        r.set_param(PARAM_MIX, 1.0);
        let mut buf = vec![1.0f32; 2];
        r.process(&mut buf, 2);
        // Long enough for the feedback network to decay past the subnormal
        // threshold, so the flush demonstrably snaps the tail to exact zero.
        let mut silence = vec![0.0f32; 2 * 1_000_000];
        r.process(&mut silence, 2);

        let check = |v: f32, what: &str| {
            assert!(
                v == 0.0 || v.abs() >= f32::MIN_POSITIVE,
                "subnormal in {what}: {v:e}"
            );
        };
        for ch in 0..2 {
            for i in 0..NUM_COMBS {
                check(r.comb_filter_store[ch][i], "comb store");
                for &s in &r.comb_buffers[ch][i] {
                    check(s, "comb buffer");
                }
            }
            for i in 0..NUM_ALLPASSES {
                for &s in &r.allpass_buffers[ch][i] {
                    check(s, "all-pass buffer");
                }
            }
        }
        // After a long silent tail the output has fully settled to exact zero.
        assert!(silence[silence.len() - 2..].iter().all(|&s| s == 0.0));
    }
}
