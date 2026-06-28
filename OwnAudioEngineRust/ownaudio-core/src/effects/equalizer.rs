//! 10-band parametric equalizer (ISO frequencies, cascaded biquad peaking).
//!
//! Faithful Rust port of the reference C# `OwnaudioNET.Effects.EqualizerEffect`
//! DSP: ten ISO octave bands, each realised as **two cascaded** RBJ peaking
//! biquads (the band gain is split in half across the cascade for a steeper,
//! smoother slope).  Coefficients are stored as a structure-of-arrays indexed by
//! filter (`b0/b1/b2/a1/a2`, 20 filters); each channel keeps its own
//! Transposed-Direct-Form-II state (`z1/z2`).  Only bands with non-zero gain are
//! evaluated (a 0 dB peaking filter is an exact identity, so skipping it is
//! numerically lossless), the wet sample is soft-limited with `0.95·tanh` above
//! 0.95, then blended with the dry signal by `mix`.  Parameter identifiers,
//! ranges and defaults mirror the C# effect so the two implementations are
//! numerically equivalent (the basis of the 2.2 reference comparison).
//!
//! **Denormal protection (2.12):** the recursive `z1/z2` states are flushed on
//! every update so a fading-out IIR tail never parks in the subnormal range
//! (which would trigger a 10–100× CPU assist on x86_64 / some ARM cores).

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};
use crate::denormal;

/// Band gain parameter IDs (dB, -12 … +12).
pub const PARAM_BAND_0: u32 = 2; // 31.25 Hz
/// See [`PARAM_BAND_0`]. 62.5 Hz.
pub const PARAM_BAND_1: u32 = 3;
/// See [`PARAM_BAND_0`]. 125 Hz.
pub const PARAM_BAND_2: u32 = 4;
/// See [`PARAM_BAND_0`]. 250 Hz.
pub const PARAM_BAND_3: u32 = 5;
/// See [`PARAM_BAND_0`]. 500 Hz.
pub const PARAM_BAND_4: u32 = 6;
/// See [`PARAM_BAND_0`]. 1 kHz.
pub const PARAM_BAND_5: u32 = 7;
/// See [`PARAM_BAND_0`]. 2 kHz.
pub const PARAM_BAND_6: u32 = 8;
/// See [`PARAM_BAND_0`]. 4 kHz.
pub const PARAM_BAND_7: u32 = 9;
/// See [`PARAM_BAND_0`]. 8 kHz.
pub const PARAM_BAND_8: u32 = 10;
/// See [`PARAM_BAND_0`]. 16 kHz.
pub const PARAM_BAND_9: u32 = 11;

const BANDS: usize = 10;
/// Cascaded biquads per band (steeper slope), mirroring the reference.
const FILTERS_PER_BAND: usize = 2;
/// Total biquad filters across all bands.
const TOTAL_FILTERS: usize = BANDS * FILTERS_PER_BAND;

/// Per-band gain clamp, matching the reference C# (±12 dB).
const GAIN_MIN_DB: f32 = -12.0;
/// See [`GAIN_MIN_DB`].
const GAIN_MAX_DB: f32 = 12.0;

/// A band is treated as active (and evaluated) only when its gain magnitude
/// exceeds this threshold, mirroring the reference active-band optimization.
const ACTIVE_GAIN_THRESHOLD_DB: f32 = 0.01;

/// Above this magnitude the wet sample is soft-limited (reference parity).
const SOFT_LIMIT_THRESHOLD: f32 = 0.95;

/// Mix values below this bypass processing entirely (mirrors C#).
const MIX_BYPASS_THRESHOLD: f32 = 0.01;

/// Channels the per-channel state is pre-allocated for; stereo is the working
/// format.  A larger channel count grows the state once (amortized), never per
/// block in steady state.
const PREALLOC_CHANNELS: usize = 2;

/// Fixed per-band Q, matching the reference default.
const Q: f32 = 1.0;

/// ISO octave centre frequencies (Hz), 31.25 Hz … 16 kHz.
const STANDARD_FREQUENCIES: [f32; BANDS] = [
    31.25, 62.5, 125.0, 250.0, 500.0, 1000.0, 2000.0, 4000.0, 8000.0, 16000.0,
];

/// 10-band parametric equalizer.
pub struct Equalizer {
    enabled: bool,
    mix: f32,
    sample_rate: f32,
    gains_db: [f32; BANDS],

    // Normalized peaking-biquad coefficients (structure-of-arrays, one per filter;
    // the two filters of a band share identical coefficients).
    b0: [f32; TOTAL_FILTERS],
    b1: [f32; TOTAL_FILTERS],
    b2: [f32; TOTAL_FILTERS],
    a1: [f32; TOTAL_FILTERS],
    a2: [f32; TOTAL_FILTERS],

    // Indices of bands with non-zero gain, evaluated on the hot path.
    active: [usize; BANDS],
    active_count: usize,

    // Per-channel Transposed-Direct-Form-II state, flattened as
    // `channel * TOTAL_FILTERS + filter`.
    z1: Vec<f32>,
    z2: Vec<f32>,
}

impl Equalizer {
    /// Creates a new flat (0 dB) [`Equalizer`] at the given sample rate.
    pub fn new(sample_rate: f32) -> Self {
        let sample_rate = if sample_rate > 0.0 {
            sample_rate
        } else {
            44_100.0
        };
        let mut eq = Self {
            enabled: true,
            mix: 1.0,
            sample_rate,
            gains_db: [0.0; BANDS],
            b0: [1.0; TOTAL_FILTERS],
            b1: [0.0; TOTAL_FILTERS],
            b2: [0.0; TOTAL_FILTERS],
            a1: [0.0; TOTAL_FILTERS],
            a2: [0.0; TOTAL_FILTERS],
            active: [0; BANDS],
            active_count: 0,
            z1: vec![0.0; PREALLOC_CHANNELS * TOTAL_FILTERS],
            z2: vec![0.0; PREALLOC_CHANNELS * TOTAL_FILTERS],
        };
        for band in 0..BANDS {
            eq.update_filter(band);
        }
        eq
    }

    /// Recomputes the normalized RBJ peaking-biquad coefficients for one band
    /// (both cascaded filters share them; the gain is halved across the cascade).
    fn update_filter(&mut self, band: usize) {
        let freq = STANDARD_FREQUENCIES[band];
        let gain = self.gains_db[band];
        let half_gain = gain * 0.5;

        let omega = 2.0 * std::f32::consts::PI * freq / self.sample_rate;
        let sin_omega = omega.sin();
        let cos_omega = omega.cos();
        let alpha = sin_omega / (2.0 * Q);
        let a = 10.0f32.powf(half_gain / 40.0);

        let b0 = 1.0 + alpha * a;
        let b1 = -2.0 * cos_omega;
        let b2 = 1.0 - alpha * a;
        let a0 = 1.0 + alpha / a;
        let a1 = -2.0 * cos_omega;
        let a2 = 1.0 - alpha / a;

        let inv_a0 = 1.0 / a0;
        let base = band * FILTERS_PER_BAND;
        for f in [base, base + 1] {
            self.b0[f] = b0 * inv_a0;
            self.b1[f] = b1 * inv_a0;
            self.b2[f] = b2 * inv_a0;
            self.a1[f] = a1 * inv_a0;
            self.a2[f] = a2 * inv_a0;
        }
    }

    /// Rebuilds the active-band index list (control thread, allocation-free).
    fn rebuild_active(&mut self) {
        self.active_count = 0;
        for band in 0..BANDS {
            if self.gains_db[band].abs() > ACTIVE_GAIN_THRESHOLD_DB {
                self.active[self.active_count] = band;
                self.active_count += 1;
            }
        }
    }

    /// Ensures the per-channel state holds at least `channels` channels.
    ///
    /// Grows the state once when a wider channel count first appears (amortized,
    /// off the steady-state hot path); the flattened layout keeps existing
    /// channel/filter indices valid across a grow.
    fn ensure_channel_state(&mut self, channels: usize) {
        let needed = channels * TOTAL_FILTERS;
        if self.z1.len() < needed {
            self.z1.resize(needed, 0.0);
            self.z2.resize(needed, 0.0);
        }
    }
}

impl Effect for Equalizer {
    fn effect_type(&self) -> EffectType {
        EffectType::Equalizer
    }

    fn process(&mut self, buffer: &mut [f32], channels: u16) {
        if !self.enabled
            || channels == 0
            || self.active_count == 0
            || self.mix < MIX_BYPASS_THRESHOLD
        {
            return;
        }

        let channels = channels as usize;
        self.ensure_channel_state(channels);

        let dry = 1.0 - self.mix;
        let frame_count = buffer.len() / channels;
        // Frame-outer / channel-inner traversal keeps the interleaved buffer
        // access sequential (cache-friendly, auto-vectorizable); each channel's
        // recursive state still evolves in frame order, so the output is bit
        // identical to a channel-outer traversal.
        for frame in 0..frame_count {
            let frame_base = frame * channels;
            for ch in 0..channels {
                let state_base = ch * TOTAL_FILTERS;
                let i = frame_base + ch;
                let input = buffer[i];
                let mut sample = input;

                for a in 0..self.active_count {
                    let base = self.active[a] * FILTERS_PER_BAND;
                    for f in [base, base + 1] {
                        let s = state_base + f;
                        let output = self.b0[f] * sample + self.z1[s];
                        self.z1[s] = denormal::flush(
                            self.b1[f] * sample - self.a1[f] * output + self.z2[s],
                        );
                        self.z2[s] = denormal::flush(self.b2[f] * sample - self.a2[f] * output);
                        sample = output;
                    }
                }

                if sample.abs() > SOFT_LIMIT_THRESHOLD {
                    sample = SOFT_LIMIT_THRESHOLD * sample.tanh();
                }

                buffer[i] = input * dry + sample * self.mix;
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
            PARAM_BAND_0..=PARAM_BAND_9 => {
                let idx = (param_id - PARAM_BAND_0) as usize;
                self.gains_db[idx] = value.clamp(GAIN_MIN_DB, GAIN_MAX_DB);
                self.update_filter(idx);
                self.rebuild_active();
                true
            }
            _ => false,
        }
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        match param_id {
            PARAM_ENABLED => Some(if self.enabled { 1.0 } else { 0.0 }),
            PARAM_MIX => Some(self.mix),
            PARAM_BAND_0..=PARAM_BAND_9 => {
                Some(self.gains_db[(param_id - PARAM_BAND_0) as usize])
            }
            _ => None,
        }
    }

    fn reset(&mut self) {
        self.z1.iter_mut().for_each(|s| *s = 0.0);
        self.z2.iter_mut().for_each(|s| *s = 0.0);
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

    /// f64 ground-truth transcription of the reference C# DSP, used to measure
    /// the production f32 implementation's numerical fidelity.
    struct Reference {
        sample_rate: f64,
        gains_db: [f64; BANDS],
        mix: f64,
        b0: [f64; TOTAL_FILTERS],
        b1: [f64; TOTAL_FILTERS],
        b2: [f64; TOTAL_FILTERS],
        a1: [f64; TOTAL_FILTERS],
        a2: [f64; TOTAL_FILTERS],
        z1: Vec<f64>,
        z2: Vec<f64>,
        channels: usize,
    }

    impl Reference {
        fn new(sample_rate: f64, channels: usize, mix: f64) -> Self {
            let mut r = Self {
                sample_rate,
                gains_db: [0.0; BANDS],
                mix,
                b0: [1.0; TOTAL_FILTERS],
                b1: [0.0; TOTAL_FILTERS],
                b2: [0.0; TOTAL_FILTERS],
                a1: [0.0; TOTAL_FILTERS],
                a2: [0.0; TOTAL_FILTERS],
                z1: vec![0.0; channels * TOTAL_FILTERS],
                z2: vec![0.0; channels * TOTAL_FILTERS],
                channels,
            };
            for band in 0..BANDS {
                r.update_filter(band);
            }
            r
        }

        fn set_gain(&mut self, band: usize, gain_db: f64) {
            self.gains_db[band] = gain_db.clamp(GAIN_MIN_DB as f64, GAIN_MAX_DB as f64);
            self.update_filter(band);
        }

        fn update_filter(&mut self, band: usize) {
            let freq = STANDARD_FREQUENCIES[band] as f64;
            let half_gain = self.gains_db[band] * 0.5;

            let omega = 2.0 * std::f64::consts::PI * freq / self.sample_rate;
            let sin_omega = omega.sin();
            let cos_omega = omega.cos();
            let alpha = sin_omega / (2.0 * Q as f64);
            let a = 10.0f64.powf(half_gain / 40.0);

            let b0 = 1.0 + alpha * a;
            let b1 = -2.0 * cos_omega;
            let b2 = 1.0 - alpha * a;
            let a0 = 1.0 + alpha / a;
            let a1 = -2.0 * cos_omega;
            let a2 = 1.0 - alpha / a;

            let inv_a0 = 1.0 / a0;
            let base = band * FILTERS_PER_BAND;
            for f in [base, base + 1] {
                self.b0[f] = b0 * inv_a0;
                self.b1[f] = b1 * inv_a0;
                self.b2[f] = b2 * inv_a0;
                self.a1[f] = a1 * inv_a0;
                self.a2[f] = a2 * inv_a0;
            }
        }

        fn process(&mut self, input: &[f32]) -> Vec<f32> {
            let frame_count = input.len() / self.channels;
            let mut out = vec![0.0f32; input.len()];
            for ch in 0..self.channels {
                let base = ch * TOTAL_FILTERS;
                for frame in 0..frame_count {
                    let i = frame * self.channels + ch;
                    let dry = input[i] as f64;
                    let mut x = dry;
                    for band in 0..BANDS {
                        if self.gains_db[band].abs() <= ACTIVE_GAIN_THRESHOLD_DB as f64 {
                            continue;
                        }
                        let fbase = band * FILTERS_PER_BAND;
                        for f in [fbase, fbase + 1] {
                            let s = base + f;
                            let y = self.b0[f] * x + self.z1[s];
                            self.z1[s] = self.b1[f] * x - self.a1[f] * y + self.z2[s];
                            self.z2[s] = self.b2[f] * x - self.a2[f] * y;
                            x = y;
                        }
                    }
                    if x.abs() > SOFT_LIMIT_THRESHOLD as f64 {
                        x = SOFT_LIMIT_THRESHOLD as f64 * x.tanh();
                    }
                    out[i] = (dry * (1.0 - self.mix) + x * self.mix) as f32;
                }
            }
            out
        }
    }

    /// Root-mean-square error between two signals expressed in decibels.
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

    /// Stereo sine sweep spanning the EQ's working range (interleaved L/R).
    fn sine_sweep_stereo(frames: usize) -> Vec<f32> {
        let mut out = Vec::with_capacity(frames * 2);
        for i in 0..frames {
            let t = i as f32 / 48_000.0;
            let f = 60.0 + 6_000.0 * i as f32 / frames as f32;
            let l = 0.5 * (2.0 * std::f32::consts::PI * f * t).sin();
            let r = 0.5 * (2.0 * std::f32::consts::PI * (f * 1.5) * t).sin();
            out.push(l);
            out.push(r);
        }
        out
    }

    #[test]
    fn defaults_are_flat_and_enabled() {
        let eq = Equalizer::new(48_000.0);
        assert!(eq.is_enabled());
        assert_eq!(eq.get_param(PARAM_MIX), Some(1.0));
        for band in 0..BANDS as u32 {
            assert_eq!(eq.get_param(PARAM_BAND_0 + band), Some(0.0));
        }
        assert_eq!(eq.active_count, 0);
    }

    #[test]
    fn band_params_clamp_to_reference_range() {
        let mut eq = Equalizer::new(48_000.0);
        eq.set_param(PARAM_BAND_0, 100.0);
        assert_eq!(eq.get_param(PARAM_BAND_0), Some(GAIN_MAX_DB));
        eq.set_param(PARAM_BAND_9, -100.0);
        assert_eq!(eq.get_param(PARAM_BAND_9), Some(GAIN_MIN_DB));
        eq.set_param(PARAM_MIX, 5.0);
        assert_eq!(eq.get_param(PARAM_MIX), Some(1.0));
    }

    #[test]
    fn unknown_param_is_rejected() {
        let mut eq = Equalizer::new(48_000.0);
        assert!(!eq.set_param(999, 1.0));
        assert_eq!(eq.get_param(999), None);
        assert!(!eq.set_param(PARAM_BAND_9 + 1, 1.0));
    }

    #[test]
    fn active_band_tracking_follows_gain() {
        let mut eq = Equalizer::new(48_000.0);
        eq.set_param(PARAM_BAND_2, 6.0);
        eq.set_param(PARAM_BAND_7, -4.0);
        assert_eq!(eq.active_count, 2);
        eq.set_param(PARAM_BAND_2, 0.0);
        assert_eq!(eq.active_count, 1);
        assert_eq!(&eq.active[..eq.active_count], &[7]);
    }

    #[test]
    fn flat_eq_passes_signal_through_untouched() {
        let mut eq = Equalizer::new(48_000.0);
        let input = sine_sweep_stereo(256);
        let mut buf = input.clone();
        eq.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn disabled_effect_passes_signal_through_untouched() {
        let mut eq = Equalizer::new(48_000.0);
        eq.set_param(PARAM_BAND_4, 8.0);
        eq.set_enabled(false);
        let input = sine_sweep_stereo(256);
        let mut buf = input.clone();
        eq.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn zero_mix_passes_signal_through_untouched() {
        let mut eq = Equalizer::new(48_000.0);
        eq.set_param(PARAM_BAND_4, 8.0);
        eq.set_param(PARAM_MIX, 0.0);
        let input = sine_sweep_stereo(256);
        let mut buf = input.clone();
        eq.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn boosting_a_band_raises_that_frequency() {
        // A strong boost at 500 Hz (band 4) should amplify a 500 Hz tone.
        let mut eq = Equalizer::new(48_000.0);
        eq.set_param(PARAM_BAND_4, 12.0);

        let frames = 8_192;
        let mut buf: Vec<f32> = (0..frames)
            .map(|i| {
                let t = i as f32 / 48_000.0;
                0.2 * (2.0 * std::f32::consts::PI * 500.0 * t).sin()
            })
            .collect();
        let input_rms: f32 = (buf.iter().map(|s| s * s).sum::<f32>() / buf.len() as f32).sqrt();

        eq.process(&mut buf, 1);
        let tail = &buf[frames / 2..];
        let out_rms: f32 = (tail.iter().map(|s| s * s).sum::<f32>() / tail.len() as f32).sqrt();
        assert!(
            out_rms > input_rms * 1.5,
            "expected boost at 500 Hz: in_rms={input_rms} out_rms={out_rms}"
        );
    }

    #[test]
    fn output_is_finite_and_length_preserved() {
        let mut eq = Equalizer::new(48_000.0);
        eq.set_param(PARAM_BAND_1, 9.0);
        eq.set_param(PARAM_BAND_8, -6.0);
        let input = sine_sweep_stereo(512);
        let mut buf = input.clone();
        eq.process(&mut buf, 2);
        assert_eq!(buf.len(), input.len());
        assert!(buf.iter().all(|s| s.is_finite()));
    }

    #[test]
    fn output_is_soft_limited() {
        // Stacked boosts on a loud tone must keep the wet path bounded by the
        // soft limiter (well under unity; the tanh asymptote × 0.95 ≈ 0.95).
        let mut eq = Equalizer::new(48_000.0);
        for band in 3..7 {
            eq.set_param(PARAM_BAND_0 + band as u32, 12.0);
        }
        let mut buf: Vec<f32> = (0..4_096)
            .map(|i| {
                let t = i as f32 / 48_000.0;
                0.9 * (2.0 * std::f32::consts::PI * 700.0 * t).sin()
            })
            .collect();
        eq.process(&mut buf, 1);
        assert!(buf.iter().all(|&s| s.abs() <= 0.95 + 1e-6));
    }

    #[test]
    fn reset_restores_reproducibility() {
        let mut eq = Equalizer::new(48_000.0);
        eq.set_param(PARAM_BAND_5, 6.0);
        let input = sine_sweep_stereo(256);

        let mut first = input.clone();
        eq.process(&mut first, 2);
        eq.reset();
        let mut second = input.clone();
        eq.process(&mut second, 2);
        assert_eq!(first, second);
    }

    #[test]
    fn state_stays_out_of_subnormals_after_long_decay() {
        let mut eq = Equalizer::new(48_000.0);
        eq.set_param(PARAM_BAND_2, 10.0);
        eq.set_param(PARAM_BAND_7, -8.0);

        let mut excite = sine_sweep_stereo(512);
        eq.process(&mut excite, 2);

        let mut silence = vec![0.0f32; 2_000_000];
        eq.process(&mut silence, 2);

        for &s in eq.z1.iter().chain(eq.z2.iter()) {
            assert!(
                s == 0.0 || s.abs() >= f32::MIN_POSITIVE,
                "state {s} parked in the subnormal range"
            );
        }
    }

    #[test]
    fn channel_state_grows_for_more_than_two_channels() {
        let mut eq = Equalizer::new(48_000.0);
        eq.set_param(PARAM_BAND_4, 12.0);
        let frames = 1_024;
        let mut buf = vec![0.0f32; frames * 4];
        for frame in 0..frames {
            let t = frame as f32 / 48_000.0;
            let v = 0.3 * (2.0 * std::f32::consts::PI * 500.0 * t).sin();
            for ch in 0..4 {
                buf[frame * 4 + ch] = v;
            }
        }
        eq.process(&mut buf, 4);
        assert!(eq.z1.len() >= 4 * TOTAL_FILTERS);
        let ch3_energy: f32 = (0..frames).map(|f| buf[f * 4 + 3].powi(2)).sum();
        assert!(ch3_energy > 0.0);
    }

    #[test]
    fn matches_reference_dsp_within_minus_60_db() {
        // 2.2 acceptance: the production f32 DSP must reproduce the reference
        // (f64 ground truth, transcribed from the C# algorithm) to better than
        // -60 dB RMS error across several gain/mix profiles.
        let input = sine_sweep_stereo(8_192);
        let cases: &[(&[(usize, f32)], f32)] = &[
            (&[(1, 6.0), (4, -4.0), (7, 8.0)], 1.0),
            (&[(0, 12.0), (5, -12.0), (9, 9.0)], 1.0),
            (&[(2, 4.0), (3, 4.5), (4, 4.0)], 0.6),
        ];

        for (profile, mix) in cases {
            let mut eq = Equalizer::new(48_000.0);
            let mut reference = Reference::new(48_000.0, 2, *mix as f64);
            eq.set_param(PARAM_MIX, *mix);
            for &(band, gain) in *profile {
                eq.set_param(PARAM_BAND_0 + band as u32, gain);
                reference.set_gain(band, gain as f64);
            }

            let mut produced = input.clone();
            eq.process(&mut produced, 2);
            let expected = reference.process(&input);

            let err = rms_error_db(&produced, &expected);
            assert!(
                err < -60.0,
                "profile {profile:?} mix {mix}: RMS error {err:.1} dB exceeds -60 dB"
            );
        }
    }
}
