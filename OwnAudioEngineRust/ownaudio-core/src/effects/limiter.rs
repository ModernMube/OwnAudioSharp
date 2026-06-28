//! Look-ahead brick-wall limiter.
//!
//! Faithful Rust port of the reference C# `OwnaudioNET.Effects.LimiterEffect`
//! DSP: a look-ahead delay line feeds a sliding-window-maximum peak detector
//! (an array-based monotonic deque, O(1) amortized, zero-allocation); the
//! per-sample required gain reduction feeds a second, sliding-window-**minimum**
//! monotonic deque, and the applied gain is that window minimum, smoothed with
//! an adaptive release toward unity (instant attack).  The window minimum is the
//! same value the reference computes with a per-sample linear scan of an
//! envelope buffer, but computed in O(1) amortized rather than O(window).  A
//! final hard ceiling clamps the output.  All buffers are pre-allocated at the
//! maximum look-ahead size during construction, so parameter changes never
//! reallocate on the audio thread.
//! Parameter identifiers, ranges and defaults mirror the C# effect (threshold,
//! ceiling and release are dB / ms facing on both sides) so the two
//! implementations are numerically equivalent (the basis of the 2.2 reference
//! comparison).
//!
//! No denormal flush is required here: the smoothed gain converges toward unity
//! and the gain-reduction floor is 0.1, so the recursive state is always bounded
//! well away from the subnormal range.

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};

/// Param ID 2 — threshold in dB (-20 … 0).
pub const PARAM_THRESHOLD: u32 = 2;
/// Param ID 3 — output ceiling in dB (-2 … 0).
pub const PARAM_CEILING: u32 = 3;
/// Param ID 4 — release time in ms (1 … 1000).
pub const PARAM_RELEASE: u32 = 4;
/// Param ID 5 — look-ahead time in ms (1 … 20).
pub const PARAM_LOOKAHEAD: u32 = 5;

const MIN_THRESHOLD_DB: f32 = -20.0;
const MAX_THRESHOLD_DB: f32 = 0.0;
const MIN_CEILING_DB: f32 = -2.0;
const MAX_CEILING_DB: f32 = 0.0;
const MIN_RELEASE_MS: f32 = 1.0;
const MAX_RELEASE_MS: f32 = 1000.0;
const MIN_LOOKAHEAD_MS: f32 = 1.0;
const MAX_LOOKAHEAD_MS: f32 = 20.0;

/// Minimum gain the reduction is allowed to reach (reference parity, -20 dB).
const MIN_GAIN: f32 = 0.1;

#[inline]
fn db_to_linear(db: f32) -> f32 {
    10.0f32.powf(db / 20.0)
}

/// Release coefficient from a time in ms, mirroring the reference.
#[inline]
fn release_coeff(time_ms: f32, sample_rate: f32) -> f32 {
    1.0 - (-1.0 / (time_ms * sample_rate / 1000.0)).exp()
}

/// Look-ahead brick-wall limiter.
pub struct Limiter {
    enabled: bool,
    sample_rate: f32,
    threshold_db: f32,
    ceiling_db: f32,
    release_ms: f32,
    lookahead_ms: f32,

    // Derived values (recomputed on parameter change).
    threshold_lin: f32,
    ceiling_lin: f32,
    release: f32,
    max_buffer_size: usize,
    lookahead_samples: usize,
    active_buffer_size: usize,

    // Look-ahead delay line.
    delay_buffer: Vec<f32>,
    delay_index: usize,

    // Monotonic deque for the sliding-window maximum of |input| (array-based,
    // circular).
    deque_indices: Vec<i64>,
    deque_values: Vec<f32>,
    deque_head: usize,
    deque_tail: usize,
    deque_size: usize,

    // Monotonic deque for the sliding-window minimum of the required gain.
    // Replaces the former per-sample linear scan over an envelope buffer with an
    // O(1) amortized window minimum that returns the identical value.
    min_deque_indices: Vec<i64>,
    min_deque_values: Vec<f32>,
    min_deque_head: usize,
    min_deque_tail: usize,
    min_deque_size: usize,

    current_gain: f32,
    target_gain: f32,
    absolute_sample_index: i64,
}

impl Limiter {
    /// Creates a new [`Limiter`] with the reference default parameters
    /// (threshold -3 dB, ceiling -0.1 dB, release 50 ms, look-ahead 5 ms).
    pub fn new(sample_rate: f32) -> Self {
        let sample_rate = if sample_rate > 0.0 {
            sample_rate
        } else {
            44_100.0
        };
        let max_buffer_size = ((MAX_LOOKAHEAD_MS * sample_rate / 1000.0) as usize).max(1);
        let lookahead_ms = 5.0f32;
        let lookahead_samples = ((lookahead_ms * sample_rate / 1000.0) as usize).clamp(1, max_buffer_size);

        let mut limiter = Self {
            enabled: true,
            sample_rate,
            threshold_db: -3.0,
            ceiling_db: -0.1,
            release_ms: 50.0,
            lookahead_ms,
            threshold_lin: db_to_linear(-3.0),
            ceiling_lin: db_to_linear(-0.1),
            release: release_coeff(50.0, sample_rate),
            max_buffer_size,
            lookahead_samples,
            active_buffer_size: lookahead_samples,
            delay_buffer: vec![0.0; max_buffer_size],
            delay_index: 0,
            deque_indices: vec![0; max_buffer_size],
            deque_values: vec![0.0; max_buffer_size],
            deque_head: 0,
            deque_tail: 0,
            deque_size: 0,
            min_deque_indices: vec![0; max_buffer_size],
            min_deque_values: vec![0.0; max_buffer_size],
            min_deque_head: 0,
            min_deque_tail: 0,
            min_deque_size: 0,
            current_gain: 1.0,
            target_gain: 1.0,
            absolute_sample_index: 0,
        };
        limiter.reset_state();
        limiter
    }

    /// Clears the active state without changing parameters (reference `Reset`).
    fn reset_state(&mut self) {
        for s in self.delay_buffer.iter_mut().take(self.active_buffer_size) {
            *s = 0.0;
        }
        self.current_gain = 1.0;
        self.target_gain = 1.0;
        self.delay_index = 0;
        self.absolute_sample_index = 0;
        self.deque_head = 0;
        self.deque_tail = 0;
        self.deque_size = 0;
        self.min_deque_head = 0;
        self.min_deque_tail = 0;
        self.min_deque_size = 0;
    }

    /// Recomputes the look-ahead window from `lookahead_ms`; resets when it
    /// changes (reference parity).
    fn update_lookahead(&mut self) {
        let new_samples = ((self.lookahead_ms * self.sample_rate / 1000.0) as usize)
            .clamp(1, self.max_buffer_size);
        if new_samples != self.lookahead_samples {
            self.lookahead_samples = new_samples;
            self.active_buffer_size = new_samples;
            self.reset_state();
        }
    }

    /// Sliding-window maximum of `|input|` over the active window (monotonic
    /// deque).  Returns the current window peak.
    fn peak_level(&mut self) -> f32 {
        let expire_threshold = self.absolute_sample_index - self.active_buffer_size as i64;

        while self.deque_size > 0 && self.deque_indices[self.deque_head] <= expire_threshold {
            self.deque_head = (self.deque_head + 1) % self.max_buffer_size;
            self.deque_size -= 1;
        }

        let current_abs = self.delay_buffer[self.delay_index].abs();

        while self.deque_size > 0 {
            let back_idx = (self.deque_tail + self.max_buffer_size - 1) % self.max_buffer_size;
            if self.deque_values[back_idx] >= current_abs {
                break;
            }
            self.deque_tail = back_idx;
            self.deque_size -= 1;
        }

        self.deque_indices[self.deque_tail] = self.absolute_sample_index;
        self.deque_values[self.deque_tail] = current_abs;
        self.deque_tail = (self.deque_tail + 1) % self.max_buffer_size;
        self.deque_size += 1;

        if self.deque_size > 0 {
            self.deque_values[self.deque_head]
        } else {
            0.0
        }
    }

    /// Sliding-window minimum of the required gain over the active window
    /// (monotonic deque).  Pushes `required` for the current sample and returns
    /// the window minimum — the same value the former linear scan of the
    /// envelope buffer produced, computed in O(1) amortized.
    fn min_gain_window(&mut self, required: f32) -> f32 {
        let expire_threshold = self.absolute_sample_index - self.active_buffer_size as i64;

        while self.min_deque_size > 0
            && self.min_deque_indices[self.min_deque_head] <= expire_threshold
        {
            self.min_deque_head = (self.min_deque_head + 1) % self.max_buffer_size;
            self.min_deque_size -= 1;
        }

        while self.min_deque_size > 0 {
            let back_idx = (self.min_deque_tail + self.max_buffer_size - 1) % self.max_buffer_size;
            if self.min_deque_values[back_idx] <= required {
                break;
            }
            self.min_deque_tail = back_idx;
            self.min_deque_size -= 1;
        }

        self.min_deque_indices[self.min_deque_tail] = self.absolute_sample_index;
        self.min_deque_values[self.min_deque_tail] = required;
        self.min_deque_tail = (self.min_deque_tail + 1) % self.max_buffer_size;
        self.min_deque_size += 1;

        self.min_deque_values[self.min_deque_head]
    }

    /// Required instantaneous gain for a given window peak (reference parity).
    fn gain_reduction(&self, peak_level: f32) -> f32 {
        if peak_level <= self.threshold_lin {
            return 1.0;
        }
        let excess = peak_level / self.threshold_lin;
        let target_level = self.threshold_lin / excess;
        (target_level / peak_level).max(MIN_GAIN)
    }

    /// Smoothed gain from the window-minimum required gain, with instant attack
    /// and an adaptive release toward unity (reference parity).
    fn smoothed_gain(&mut self, min_gain: f32) -> f32 {
        self.target_gain = min_gain;

        if self.target_gain < self.current_gain {
            self.current_gain = self.target_gain;
        } else {
            let gain_diff = 1.0 - self.current_gain;
            let mut adaptive_release = self.release;
            if gain_diff > 0.3 {
                adaptive_release *= 1.5;
            } else if gain_diff < 0.1 {
                adaptive_release *= 0.5;
            }
            adaptive_release = adaptive_release.clamp(0.0001, 0.9999);

            self.current_gain += (self.target_gain - self.current_gain) * adaptive_release;
            if (self.target_gain - self.current_gain).abs() < 0.0001 {
                self.current_gain = self.target_gain;
            }
        }

        self.current_gain
    }

    /// Hard ceiling clamp on the output sample (reference parity).
    fn apply_ceiling(&self, sample: f32) -> f32 {
        if sample.abs() > self.ceiling_lin {
            if sample > 0.0 {
                self.ceiling_lin
            } else {
                -self.ceiling_lin
            }
        } else {
            sample
        }
    }
}

impl Effect for Limiter {
    fn effect_type(&self) -> EffectType {
        EffectType::Limiter
    }

    fn process(&mut self, buffer: &mut [f32], _channels: u16) {
        if !self.enabled {
            return;
        }

        for sample in buffer.iter_mut() {
            let input = *sample;
            self.delay_buffer[self.delay_index] = input;

            let peak = self.peak_level();
            let required = self.gain_reduction(peak);
            let min_gain = self.min_gain_window(required);

            let mut smooth = self.smoothed_gain(min_gain);
            if !smooth.is_finite() {
                smooth = 1.0;
                self.current_gain = 1.0;
                self.target_gain = 1.0;
            }

            let delayed_idx = (self.delay_index + self.active_buffer_size - self.lookahead_samples)
                % self.active_buffer_size;
            let processed = self.apply_ceiling(self.delay_buffer[delayed_idx] * smooth);
            *sample = processed;

            self.delay_index = (self.delay_index + 1) % self.active_buffer_size;
            self.absolute_sample_index += 1;
        }
    }

    fn set_param(&mut self, param_id: u32, value: f32) -> bool {
        match param_id {
            PARAM_ENABLED => {
                self.enabled = value >= 0.5;
                true
            }
            // The limiter is always 100 % processed (reference parity); the mix
            // parameter is accepted but inert.
            PARAM_MIX => true,
            PARAM_THRESHOLD => {
                self.threshold_db = value.clamp(MIN_THRESHOLD_DB, MAX_THRESHOLD_DB);
                self.threshold_lin = db_to_linear(self.threshold_db);
                true
            }
            PARAM_CEILING => {
                self.ceiling_db = value.clamp(MIN_CEILING_DB, MAX_CEILING_DB);
                self.ceiling_lin = db_to_linear(self.ceiling_db);
                true
            }
            PARAM_RELEASE => {
                self.release_ms = value.clamp(MIN_RELEASE_MS, MAX_RELEASE_MS);
                self.release = release_coeff(self.release_ms, self.sample_rate);
                true
            }
            PARAM_LOOKAHEAD => {
                self.lookahead_ms = value.clamp(MIN_LOOKAHEAD_MS, MAX_LOOKAHEAD_MS);
                self.update_lookahead();
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
            PARAM_CEILING => Some(self.ceiling_db),
            PARAM_RELEASE => Some(self.release_ms),
            PARAM_LOOKAHEAD => Some(self.lookahead_ms),
            _ => None,
        }
    }

    fn reset(&mut self) {
        self.reset_state();
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
        threshold_lin: f64,
        ceiling_lin: f64,
        release: f64,
        max_buffer_size: usize,
        lookahead_samples: usize,
        active_buffer_size: usize,
        delay_buffer: Vec<f64>,
        envelope_buffer: Vec<f64>,
        delay_index: usize,
        envelope_index: usize,
        deque_indices: Vec<i64>,
        deque_values: Vec<f64>,
        deque_head: usize,
        deque_tail: usize,
        deque_size: usize,
        current_gain: f64,
        target_gain: f64,
        absolute_sample_index: i64,
    }

    impl Reference {
        fn new(
            sample_rate: f64,
            threshold_db: f64,
            ceiling_db: f64,
            release_ms: f64,
            lookahead_ms: f64,
        ) -> Self {
            let max_buffer_size = ((MAX_LOOKAHEAD_MS as f64 * sample_rate / 1000.0) as usize).max(1);
            let lookahead_samples =
                ((lookahead_ms * sample_rate / 1000.0) as usize).clamp(1, max_buffer_size);
            Self {
                threshold_lin: 10.0f64.powf(threshold_db / 20.0),
                ceiling_lin: 10.0f64.powf(ceiling_db / 20.0),
                release: 1.0 - (-1.0 / (release_ms * sample_rate / 1000.0)).exp(),
                max_buffer_size,
                lookahead_samples,
                active_buffer_size: lookahead_samples,
                delay_buffer: vec![0.0; max_buffer_size],
                envelope_buffer: vec![1.0; max_buffer_size],
                delay_index: 0,
                envelope_index: 0,
                deque_indices: vec![0; max_buffer_size],
                deque_values: vec![0.0; max_buffer_size],
                deque_head: 0,
                deque_tail: 0,
                deque_size: 0,
                current_gain: 1.0,
                target_gain: 1.0,
                absolute_sample_index: 0,
            }
        }

        fn peak_level(&mut self) -> f64 {
            let expire = self.absolute_sample_index - self.active_buffer_size as i64;
            while self.deque_size > 0 && self.deque_indices[self.deque_head] <= expire {
                self.deque_head = (self.deque_head + 1) % self.max_buffer_size;
                self.deque_size -= 1;
            }
            let current_abs = self.delay_buffer[self.delay_index].abs();
            while self.deque_size > 0 {
                let back = (self.deque_tail + self.max_buffer_size - 1) % self.max_buffer_size;
                if self.deque_values[back] >= current_abs {
                    break;
                }
                self.deque_tail = back;
                self.deque_size -= 1;
            }
            self.deque_indices[self.deque_tail] = self.absolute_sample_index;
            self.deque_values[self.deque_tail] = current_abs;
            self.deque_tail = (self.deque_tail + 1) % self.max_buffer_size;
            self.deque_size += 1;
            if self.deque_size > 0 {
                self.deque_values[self.deque_head]
            } else {
                0.0
            }
        }

        fn gain_reduction(&self, peak: f64) -> f64 {
            if peak <= self.threshold_lin {
                return 1.0;
            }
            let excess = peak / self.threshold_lin;
            let target = self.threshold_lin / excess;
            (target / peak).max(MIN_GAIN as f64)
        }

        fn smoothed_gain(&mut self) -> f64 {
            let mut min_gain = 1.0f64;
            for &g in self.envelope_buffer.iter().take(self.active_buffer_size) {
                if g < min_gain {
                    min_gain = g;
                }
            }
            self.target_gain = min_gain;
            if self.target_gain < self.current_gain {
                self.current_gain = self.target_gain;
            } else {
                let gain_diff = 1.0 - self.current_gain;
                let mut ar = self.release;
                if gain_diff > 0.3 {
                    ar *= 1.5;
                } else if gain_diff < 0.1 {
                    ar *= 0.5;
                }
                ar = ar.clamp(0.0001, 0.9999);
                self.current_gain += (self.target_gain - self.current_gain) * ar;
                if (self.target_gain - self.current_gain).abs() < 0.0001 {
                    self.current_gain = self.target_gain;
                }
            }
            self.current_gain
        }

        fn apply_ceiling(&self, sample: f64) -> f64 {
            if sample.abs() > self.ceiling_lin {
                if sample > 0.0 {
                    self.ceiling_lin
                } else {
                    -self.ceiling_lin
                }
            } else {
                sample
            }
        }

        fn process(&mut self, input: &[f32]) -> Vec<f32> {
            let mut out = vec![0.0f32; input.len()];
            for (o, &x) in out.iter_mut().zip(input.iter()) {
                self.delay_buffer[self.delay_index] = x as f64;
                let peak = self.peak_level();
                let required = self.gain_reduction(peak);
                self.envelope_buffer[self.envelope_index] = required;
                let mut smooth = self.smoothed_gain();
                if !smooth.is_finite() {
                    smooth = 1.0;
                    self.current_gain = 1.0;
                    self.target_gain = 1.0;
                }
                let idx = (self.delay_index + self.active_buffer_size - self.lookahead_samples)
                    % self.active_buffer_size;
                *o = self.apply_ceiling(self.delay_buffer[idx] * smooth) as f32;
                self.delay_index = (self.delay_index + 1) % self.active_buffer_size;
                self.envelope_index = (self.envelope_index + 1) % self.active_buffer_size;
                self.absolute_sample_index += 1;
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

    /// Stereo tone with loud peaks above any sane threshold (interleaved).
    fn loud_stereo(frames: usize) -> Vec<f32> {
        let mut out = Vec::with_capacity(frames * 2);
        for i in 0..frames {
            let t = i as f32 / 48_000.0;
            // Two partials so the envelope is not a pure sinusoid.
            let v = 0.9 * (2.0 * std::f32::consts::PI * 220.0 * t).sin()
                + 0.3 * (2.0 * std::f32::consts::PI * 1_330.0 * t).sin();
            out.push(v);
            out.push(v * 0.85);
        }
        out
    }

    #[test]
    fn defaults_match_reference() {
        let l = Limiter::new(48_000.0);
        assert!(l.is_enabled());
        assert_eq!(l.get_param(PARAM_THRESHOLD), Some(-3.0));
        assert_eq!(l.get_param(PARAM_CEILING), Some(-0.1));
        assert_eq!(l.get_param(PARAM_RELEASE), Some(50.0));
        assert_eq!(l.get_param(PARAM_LOOKAHEAD), Some(5.0));
        assert_eq!(l.get_param(PARAM_MIX), Some(1.0));
    }

    #[test]
    fn params_clamp_to_reference_ranges() {
        let mut l = Limiter::new(48_000.0);
        l.set_param(PARAM_THRESHOLD, 50.0);
        assert_eq!(l.get_param(PARAM_THRESHOLD), Some(0.0));
        l.set_param(PARAM_THRESHOLD, -200.0);
        assert_eq!(l.get_param(PARAM_THRESHOLD), Some(-20.0));
        l.set_param(PARAM_CEILING, 10.0);
        assert_eq!(l.get_param(PARAM_CEILING), Some(0.0));
        l.set_param(PARAM_CEILING, -50.0);
        assert_eq!(l.get_param(PARAM_CEILING), Some(-2.0));
        l.set_param(PARAM_RELEASE, 9_999.0);
        assert_eq!(l.get_param(PARAM_RELEASE), Some(1000.0));
        l.set_param(PARAM_RELEASE, 0.0);
        assert_eq!(l.get_param(PARAM_RELEASE), Some(1.0));
        l.set_param(PARAM_LOOKAHEAD, 99.0);
        assert_eq!(l.get_param(PARAM_LOOKAHEAD), Some(20.0));
        l.set_param(PARAM_LOOKAHEAD, 0.0);
        assert_eq!(l.get_param(PARAM_LOOKAHEAD), Some(1.0));
    }

    #[test]
    fn unknown_param_is_rejected() {
        let mut l = Limiter::new(48_000.0);
        assert!(!l.set_param(999, 1.0));
        assert_eq!(l.get_param(999), None);
    }

    #[test]
    fn disabled_effect_passes_signal_through_untouched() {
        let mut l = Limiter::new(48_000.0);
        l.set_enabled(false);
        let input = loud_stereo(512);
        let mut buf = input.clone();
        l.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn output_never_exceeds_ceiling() {
        let mut l = Limiter::new(48_000.0);
        l.set_param(PARAM_THRESHOLD, -6.0);
        l.set_param(PARAM_CEILING, -0.5);
        let ceiling = db_to_linear(-0.5);
        let mut buf = loud_stereo(8_192);
        l.process(&mut buf, 2);
        assert!(
            buf.iter().all(|&s| s.abs() <= ceiling + 1e-6),
            "a sample exceeded the ceiling {ceiling}"
        );
    }

    #[test]
    fn loud_signal_is_attenuated() {
        let mut l = Limiter::new(48_000.0);
        l.set_param(PARAM_THRESHOLD, -12.0);
        let input = loud_stereo(8_192);
        let in_rms: f32 = (input.iter().map(|s| s * s).sum::<f32>() / input.len() as f32).sqrt();
        let mut buf = input.clone();
        l.process(&mut buf, 2);
        let tail = &buf[buf.len() / 2..];
        let out_rms: f32 = (tail.iter().map(|s| s * s).sum::<f32>() / tail.len() as f32).sqrt();
        assert!(out_rms < in_rms, "in_rms={in_rms} out_rms={out_rms}");
    }

    #[test]
    fn output_is_finite_and_length_preserved() {
        let mut l = Limiter::new(48_000.0);
        l.set_param(PARAM_THRESHOLD, -10.0);
        let input = loud_stereo(777);
        let mut buf = input.clone();
        l.process(&mut buf, 2);
        assert_eq!(buf.len(), input.len());
        assert!(buf.iter().all(|s| s.is_finite()));
    }

    #[test]
    fn reset_restores_reproducibility() {
        let mut l = Limiter::new(48_000.0);
        l.set_param(PARAM_THRESHOLD, -10.0);
        let input = loud_stereo(2_048);

        let mut first = input.clone();
        l.process(&mut first, 2);
        l.reset();
        let mut second = input.clone();
        l.process(&mut second, 2);
        assert_eq!(first, second);
    }

    #[test]
    fn matches_reference_dsp_within_minus_60_db() {
        let input = loud_stereo(8_192);
        let cases = [
            (-3.0f32, -0.1f32, 50.0f32, 5.0f32),
            (-12.0, -0.5, 25.0, 3.0),
            (-6.0, -0.3, 200.0, 10.0),
        ];
        for (thr, ceil, rel, look) in cases {
            let mut l = Limiter::new(48_000.0);
            l.set_param(PARAM_THRESHOLD, thr);
            l.set_param(PARAM_CEILING, ceil);
            l.set_param(PARAM_RELEASE, rel);
            l.set_param(PARAM_LOOKAHEAD, look);

            let mut reference = Reference::new(
                48_000.0, thr as f64, ceil as f64, rel as f64, look as f64,
            );

            let mut produced = input.clone();
            l.process(&mut produced, 2);
            let expected = reference.process(&input);

            let err = rms_error_db(&produced, &expected);
            assert!(
                err < -60.0,
                "thr={thr} ceil={ceil} rel={rel} look={look}: RMS error {err:.1} dB exceeds -60 dB"
            );
        }
    }
}
