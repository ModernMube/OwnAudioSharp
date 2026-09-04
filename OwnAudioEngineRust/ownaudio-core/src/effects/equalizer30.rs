//! 30-band parametric equalizer (ISO 1/3-octave, biquad peaking).
//!
//! Faithful Rust port of the reference C# `OwnaudioNET.Effects.Equalizer30BandEffect`
//! DSP: each of the 30 ISO 1/3-octave bands is a single RBJ peaking biquad with a
//! per-band, frequency-dependent Q (the `optimizedQ` table from the reference).
//! Coefficients are stored as a structure-of-arrays (`b0/b1/b2/a1/a2`); each
//! channel keeps its own Transposed-Direct-Form-II state (`z1/z2`).  Only bands
//! with non-zero gain are evaluated (a 0 dB peaking filter is an exact identity,
//! so skipping it is numerically lossless), and the post-chain output is hard
//! clipped to ±1.5 just like the reference.  Parameter identifiers, ranges and
//! defaults mirror the C# effect so the two implementations are numerically
//! equivalent (the basis of the 2.2 reference comparison).
//!
//! **Denormal protection (2.12):** the recursive `z1/z2` states are flushed on
//! every update so a fading-out IIR tail never parks in the subnormal range
//! (which would trigger a 10–100× CPU assist on x86_64 / some ARM cores).

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};
use crate::denormal;
use crate::smoothing::{RampedParam, DEFAULT_SMOOTH_MS};

/// Below this dB delta a smoothed band gain is considered unchanged, so its
/// coefficients are not recomputed for the block.
const GAIN_RECALC_EPS_DB: f32 = 1.0e-4;

/// First band gain parameter ID; bands occupy `PARAM_BAND_0 ..= PARAM_BAND_0 + 29`.
///
/// ISO 1/3-octave centre frequencies (Hz):
/// 20, 25, 31.5, 40, 50, 63, 80, 100, 125, 160,
/// 200, 250, 315, 400, 500, 630, 800, 1 k, 1.25 k, 1.6 k,
/// 2 k, 2.5 k, 3.15 k, 4 k, 5 k, 6.3 k, 8 k, 10 k, 12.5 k, 16 k.
pub const PARAM_BAND_0: u32 = 2;
/// Last band gain parameter ID (band index 29, 16 kHz).
pub const PARAM_BAND_29: u32 = 31;

/// First band Q parameter ID; bands occupy `PARAM_BAND_Q_0 ..= PARAM_BAND_Q_0 + 29`.
///
/// Defaults to the constant-Q value below, which is what a plain 1/3-octave EQ
/// wants. Matchering deconvolves its band gains against a Q it picks per band, so
/// feeding those gains into a fixed-Q filterbank draws a different curve.
pub const PARAM_BAND_Q_0: u32 = 32;
/// Last band Q parameter ID.
pub const PARAM_BAND_Q_29: u32 = 61;

/// First band centre-frequency parameter ID (Hz); occupies 30 ids like the others.
///
/// Defaults to the ISO centre for that band. Only worth touching if you want the
/// band somewhere other than its nominal centre.
pub const PARAM_BAND_FREQ_0: u32 = 62;
/// Last band centre-frequency parameter ID.
pub const PARAM_BAND_FREQ_29: u32 = 91;

const BANDS: usize = 30;

/// Per-band gain clamp, matching the reference C# (extended ±18 dB range for
/// aggressive EQ correction).
const GAIN_MIN_DB: f32 = -18.0;
/// See [`GAIN_MIN_DB`].
const GAIN_MAX_DB: f32 = 18.0;

/// A band is treated as active (and evaluated) only when its gain magnitude
/// exceeds this threshold, mirroring the reference active-band optimization.
const ACTIVE_GAIN_THRESHOLD_DB: f32 = 0.01;

/// Output hard-clip ceiling applied after the filter chain.
///
/// **Note (B.6.1):** this is a hidden nonlinearity — an EQ is expected to be
/// linear, but a very hot signal (peaks above ±1.5 after boosting several bands)
/// is hard-clipped here, adding level-dependent distortion that shows up in a
/// null test.  It is retained for parity with the shipped behaviour; removing it
/// (leaving head-room to the master limiter) is a deliberate sound change best
/// gated behind an engine-level quality option.
const HARD_CLIP: f32 = 1.5;

/// Mix values below this bypass processing entirely.
const MIX_BYPASS_THRESHOLD: f32 = 0.01;

/// Channels the per-channel state is pre-allocated for; the library's working
/// format is stereo.  A larger channel count grows the state once (amortized,
/// like the track scratch and engine pre-alloc), never per block in steady state.
const PREALLOC_CHANNELS: usize = 2;

/// ISO 1/3-octave centre frequencies (Hz), 20 Hz … 16 kHz.
const STANDARD_FREQUENCIES: [f32; BANDS] = [
    20.0, 25.0, 31.5, 40.0, 50.0, 63.0, 80.0, 100.0, 125.0, 160.0, 200.0, 250.0, 315.0, 400.0,
    500.0, 630.0, 800.0, 1000.0, 1250.0, 1600.0, 2000.0, 2500.0, 3150.0, 4000.0, 5000.0, 6300.0,
    8000.0, 10000.0, 12500.0, 16000.0,
];

/// Constant-Q for 1/3-octave spacing: `1 / (2·sinh(ln2/6))`.  The old 0.6…1.4
/// table made every bell ~1.4 octaves wide, so a drawn curve summed to about 5×
/// what the sliders read.
const BAND_Q: f32 = 4.318_474;

/// Q clamp, same range the managed `Equalizer30BandEffect.SetBandGain` accepts.
const Q_MIN: f32 = 0.1;
const Q_MAX: f32 = 10.0;

/// Centre-frequency clamp, ditto.
const FREQ_MIN: f32 = 20.0;
const FREQ_MAX: f32 = 20_000.0;

/// 30-band 1/3-octave parametric equalizer.
pub struct Equalizer30 {
    enabled: bool,
    mix: f32,
    sample_rate: f32,
    gains_db: [f32; BANDS],
    // Per-band bell shape. Both default to the 1/3-octave values, so an EQ nobody
    // reshapes behaves exactly as it did before these two parameters existed.
    q: [f32; BANDS],
    freqs: [f32; BANDS],

    // Normalized peaking-biquad coefficients (structure-of-arrays, one per band).
    b0: [f32; BANDS],
    b1: [f32; BANDS],
    b2: [f32; BANDS],
    a1: [f32; BANDS],
    a2: [f32; BANDS],

    // Indices of bands with non-zero gain, evaluated on the hot path.
    active: [usize; BANDS],
    active_count: usize,

    // Per-channel Transposed-Direct-Form-II state, flattened as `channel * BANDS + band`.
    z1: Vec<f32>,
    z2: Vec<f32>,
    // Per-band dB-gain smoother; a gain change during playback glides at the
    // per-sample time constant while its biquad coefficients are recomputed once
    // per block (block-rate coefficient update — no zipper on a live band drag).
    // Before the first block a change snaps, keeping the steady-state output
    // bit-identical to an un-smoothed EQ.
    gain_ramps: [RampedParam; BANDS],
    // Dry/wet mix smoother (B.6.2): the 30-band `mix` parameter used to be inert
    // (stored but never applied), unlike the 10-band EQ.  It now blends the wet
    // filter output with the dry signal exactly like the 10-band effect.
    mix_ramp: RampedParam,
}

impl Equalizer30 {
    /// Creates a new flat (0 dB) [`Equalizer30`] at the given sample rate.
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
            q: [BAND_Q; BANDS],
            freqs: STANDARD_FREQUENCIES,
            b0: [1.0; BANDS],
            b1: [0.0; BANDS],
            b2: [0.0; BANDS],
            a1: [0.0; BANDS],
            a2: [0.0; BANDS],
            active: [0; BANDS],
            active_count: 0,
            z1: vec![0.0; PREALLOC_CHANNELS * BANDS],
            z2: vec![0.0; PREALLOC_CHANNELS * BANDS],
            gain_ramps: [RampedParam::new(0.0, sample_rate, DEFAULT_SMOOTH_MS); BANDS],
            mix_ramp: RampedParam::new(1.0, sample_rate, DEFAULT_SMOOTH_MS),
        };
        for band in 0..BANDS {
            eq.update_filter(band);
        }
        eq
    }

    /// Advances every band's dB-gain smoother by one block and recomputes the
    /// biquad coefficients of any band whose gain moved, rebuilding the active
    /// list if needed.  Call once at the top of `process`; before the first block
    /// a `set_param` snaps the smoother, so this reconciles the deferred gain into
    /// the coefficients on the first block (steady-state parity preserved).
    fn advance_gain_ramps(&mut self, frame_count: usize) {
        let mut dirty = false;
        for band in 0..BANDS {
            self.gain_ramps[band].begin_block();
            let g = self.gain_ramps[band].advance_block(frame_count);
            if (g - self.gains_db[band]).abs() > GAIN_RECALC_EPS_DB {
                self.gains_db[band] = g;
                self.update_filter(band);
                dirty = true;
            }
        }
        if dirty {
            self.rebuild_active();
        }
    }

    /// Recomputes the normalized RBJ peaking-biquad coefficients for one band
    /// from its centre frequency / Q and its current gain.
    fn update_filter(&mut self, band: usize) {
        let freq = self.freqs[band];
        let q = self.q[band];
        let gain = self.gains_db[band];

        let omega = 2.0 * std::f32::consts::PI * freq / self.sample_rate;
        let sin_omega = omega.sin();
        let cos_omega = omega.cos();
        let alpha = sin_omega / (2.0 * q);
        let a = 10.0f32.powf(gain / 40.0);

        let b0 = 1.0 + alpha * a;
        let b1 = -2.0 * cos_omega;
        let b2 = 1.0 - alpha * a;
        let a0 = 1.0 + alpha / a;
        let a1 = -2.0 * cos_omega;
        let a2 = 1.0 - alpha / a;

        let inv_a0 = 1.0 / a0;
        self.b0[band] = b0 * inv_a0;
        self.b1[band] = b1 * inv_a0;
        self.b2[band] = b2 * inv_a0;
        self.a1[band] = a1 * inv_a0;
        self.a2[band] = a2 * inv_a0;
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
    /// channel/band indices valid across a grow.
    fn ensure_channel_state(&mut self, channels: usize) {
        let needed = channels * BANDS;
        if self.z1.len() < needed {
            self.z1.resize(needed, 0.0);
            self.z2.resize(needed, 0.0);
        }
    }
}

impl Effect for Equalizer30 {
    fn effect_type(&self) -> EffectType {
        EffectType::Equalizer30
    }

    fn process(&mut self, buffer: &mut [f32], channels: u16) {
        self.mix_ramp.begin_block();
        if !self.enabled
            || channels == 0
            || (self.mix < MIX_BYPASS_THRESHOLD && self.mix_ramp.current() < MIX_BYPASS_THRESHOLD)
        {
            return;
        }

        let channels = channels as usize;
        let frame_count = buffer.len() / channels;

        // Glide any band gains changed during playback and refresh their
        // coefficients once per block before deciding which bands are active.
        self.advance_gain_ramps(frame_count);
        if self.active_count == 0 {
            return;
        }

        self.ensure_channel_state(channels);
        // Frame-outer / channel-inner traversal keeps the interleaved buffer
        // access sequential (cache-friendly, auto-vectorizable); each channel's
        // recursive state still evolves in frame order, so the output is bit
        // identical to a channel-outer traversal.
        for frame in 0..frame_count {
            let frame_base = frame * channels;
            let mix = self.mix_ramp.advance();
            let dry = 1.0 - mix;
            for ch in 0..channels {
                let state_base = ch * BANDS;
                let i = frame_base + ch;
                let dry_in = buffer[i];
                let mut input = dry_in;

                for a in 0..self.active_count {
                    let band = self.active[a];
                    let s = state_base + band;

                    let output = self.b0[band] * input + self.z1[s];
                    self.z1[s] = denormal::flush(
                        self.b1[band] * input - self.a1[band] * output + self.z2[s],
                    );
                    self.z2[s] = denormal::flush(self.b2[band] * input - self.a2[band] * output);
                    input = output;
                }

                if input.abs() > HARD_CLIP {
                    input = input.signum() * HARD_CLIP;
                }
                // B.6.2: blend the wet filter output with the dry signal by `mix`
                // (previously the parameter was inert and always ran fully wet).
                buffer[i] = dry_in * dry + input * mix;
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
            PARAM_BAND_0..=PARAM_BAND_29 => {
                let idx = (param_id - PARAM_BAND_0) as usize;
                // Snaps before the first block (steady-state parity), glides after
                // playback starts; the coefficients are refreshed in `process`.
                self.gain_ramps[idx].set(value.clamp(GAIN_MIN_DB, GAIN_MAX_DB));
                true
            }
            // Shape, unlike gain, is not smoothed: it is set up before the render,
            // not dragged while one runs.
            PARAM_BAND_Q_0..=PARAM_BAND_Q_29 => {
                let idx = (param_id - PARAM_BAND_Q_0) as usize;
                let q = value.clamp(Q_MIN, Q_MAX);
                if (self.q[idx] - q).abs() > f32::EPSILON {
                    self.q[idx] = q;
                    self.update_filter(idx);
                }
                true
            }
            PARAM_BAND_FREQ_0..=PARAM_BAND_FREQ_29 => {
                let idx = (param_id - PARAM_BAND_FREQ_0) as usize;
                let freq = value.clamp(FREQ_MIN, FREQ_MAX);
                if (self.freqs[idx] - freq).abs() > f32::EPSILON {
                    self.freqs[idx] = freq;
                    self.update_filter(idx);
                }
                true
            }
            _ => false,
        }
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        match param_id {
            PARAM_ENABLED => Some(if self.enabled { 1.0 } else { 0.0 }),
            PARAM_MIX => Some(self.mix),
            PARAM_BAND_0..=PARAM_BAND_29 => {
                // Report the value the user set (the ramp target), not the point
                // the glide currently sits at.
                Some(self.gain_ramps[(param_id - PARAM_BAND_0) as usize].target())
            }
            PARAM_BAND_Q_0..=PARAM_BAND_Q_29 => Some(self.q[(param_id - PARAM_BAND_Q_0) as usize]),
            PARAM_BAND_FREQ_0..=PARAM_BAND_FREQ_29 => {
                Some(self.freqs[(param_id - PARAM_BAND_FREQ_0) as usize])
            }
            _ => None,
        }
    }

    fn reset(&mut self) {
        self.z1.iter_mut().for_each(|s| *s = 0.0);
        self.z2.iter_mut().for_each(|s| *s = 0.0);
        for band in 0..BANDS {
            self.gain_ramps[band].reset(self.gains_db[band]);
        }
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

    /// f64 ground-truth transcription of the reference C# DSP, used to measure
    /// the production f32 implementation's numerical fidelity.
    struct Reference {
        sample_rate: f64,
        gains_db: [f64; BANDS],
        b0: [f64; BANDS],
        b1: [f64; BANDS],
        b2: [f64; BANDS],
        a1: [f64; BANDS],
        a2: [f64; BANDS],
        z1: Vec<f64>,
        z2: Vec<f64>,
        channels: usize,
    }

    impl Reference {
        fn new(sample_rate: f64, channels: usize) -> Self {
            let mut r = Self {
                sample_rate,
                gains_db: [0.0; BANDS],
                b0: [1.0; BANDS],
                b1: [0.0; BANDS],
                b2: [0.0; BANDS],
                a1: [0.0; BANDS],
                a2: [0.0; BANDS],
                z1: vec![0.0; channels * BANDS],
                z2: vec![0.0; channels * BANDS],
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
            let q = BAND_Q as f64;
            let gain = self.gains_db[band];

            let omega = 2.0 * std::f64::consts::PI * freq / self.sample_rate;
            let sin_omega = omega.sin();
            let cos_omega = omega.cos();
            let alpha = sin_omega / (2.0 * q);
            let a = 10.0f64.powf(gain / 40.0);

            let b0 = 1.0 + alpha * a;
            let b1 = -2.0 * cos_omega;
            let b2 = 1.0 - alpha * a;
            let a0 = 1.0 + alpha / a;
            let a1 = -2.0 * cos_omega;
            let a2 = 1.0 - alpha / a;

            let inv_a0 = 1.0 / a0;
            self.b0[band] = b0 * inv_a0;
            self.b1[band] = b1 * inv_a0;
            self.b2[band] = b2 * inv_a0;
            self.a1[band] = a1 * inv_a0;
            self.a2[band] = a2 * inv_a0;
        }

        fn process(&mut self, input: &[f32]) -> Vec<f32> {
            let frame_count = input.len() / self.channels;
            let mut out = vec![0.0f32; input.len()];
            for ch in 0..self.channels {
                let base = ch * BANDS;
                for frame in 0..frame_count {
                    let i = frame * self.channels + ch;
                    let mut x = input[i] as f64;
                    for band in 0..BANDS {
                        if self.gains_db[band].abs() <= ACTIVE_GAIN_THRESHOLD_DB as f64 {
                            continue;
                        }
                        let s = base + band;
                        let y = self.b0[band] * x + self.z1[s];
                        self.z1[s] = self.b1[band] * x - self.a1[band] * y + self.z2[s];
                        self.z2[s] = self.b2[band] * x - self.a2[band] * y;
                        x = y;
                    }
                    if x.abs() > HARD_CLIP as f64 {
                        x = x.signum() * HARD_CLIP as f64;
                    }
                    out[i] = x as f32;
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
        let eq = Equalizer30::new(48_000.0);
        assert!(eq.is_enabled());
        assert_eq!(eq.get_param(PARAM_MIX), Some(1.0));
        for band in 0..BANDS as u32 {
            assert_eq!(eq.get_param(PARAM_BAND_0 + band), Some(0.0));
        }
        assert_eq!(eq.active_count, 0);
    }

    #[test]
    fn band_params_clamp_to_reference_range() {
        let mut eq = Equalizer30::new(48_000.0);
        eq.set_param(PARAM_BAND_0, 100.0);
        assert_eq!(eq.get_param(PARAM_BAND_0), Some(GAIN_MAX_DB));
        eq.set_param(PARAM_BAND_0 + 29, -100.0);
        assert_eq!(eq.get_param(PARAM_BAND_0 + 29), Some(GAIN_MIN_DB));
        eq.set_param(PARAM_MIX, 5.0);
        assert_eq!(eq.get_param(PARAM_MIX), Some(1.0));
    }

    #[test]
    fn unknown_param_is_rejected() {
        let mut eq = Equalizer30::new(48_000.0);
        assert!(!eq.set_param(999, 1.0));
        assert_eq!(eq.get_param(999), None);
        // One past the last shape parameter must not be accepted.
        assert!(!eq.set_param(PARAM_BAND_FREQ_29 + 1, 1.0));
    }

    #[test]
    fn active_band_tracking_follows_gain() {
        // Band gains now take effect through a block-rate smoother, so the active
        // list is refreshed inside `process`.  A gain set before the first block
        // snaps (one block applies it); a change during playback glides, and a
        // band stays active until its gain has drifted below the threshold.
        let mut eq = Equalizer30::new(48_000.0);
        eq.set_param(PARAM_BAND_0 + 5, 6.0);
        eq.set_param(PARAM_BAND_0 + 20, -4.0);
        let mut buf = sine_sweep_stereo(256);
        eq.process(&mut buf, 2);
        assert_eq!(eq.active_count, 2);

        // Returning a band to flat removes it from the active set once the glide
        // has settled.
        eq.set_param(PARAM_BAND_0 + 5, 0.0);
        for _ in 0..64 {
            let mut b = sine_sweep_stereo(256);
            eq.process(&mut b, 2);
        }
        assert_eq!(eq.active_count, 1);
        assert_eq!(&eq.active[..eq.active_count], &[20]);
    }

    #[test]
    fn flat_eq_passes_signal_through_untouched() {
        let mut eq = Equalizer30::new(48_000.0);
        let input = sine_sweep_stereo(256);
        let mut buf = input.clone();
        eq.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn disabled_effect_passes_signal_through_untouched() {
        let mut eq = Equalizer30::new(48_000.0);
        eq.set_param(PARAM_BAND_0 + 10, 8.0);
        eq.set_enabled(false);
        let input = sine_sweep_stereo(256);
        let mut buf = input.clone();
        eq.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn boosting_a_band_raises_that_frequency() {
        // A strong boost at 500 Hz (band 14) should amplify a 500 Hz tone.
        let mut eq = Equalizer30::new(48_000.0);
        eq.set_param(PARAM_BAND_0 + 14, 12.0);

        let frames = 8_192;
        let mut buf: Vec<f32> = (0..frames)
            .map(|i| {
                let t = i as f32 / 48_000.0;
                0.25 * (2.0 * std::f32::consts::PI * 500.0 * t).sin()
            })
            .collect();
        let input_rms: f32 = (buf.iter().map(|s| s * s).sum::<f32>() / buf.len() as f32).sqrt();

        eq.process(&mut buf, 1);
        // Skip the filter warm-up transient before measuring steady-state gain.
        let tail = &buf[frames / 2..];
        let out_rms: f32 = (tail.iter().map(|s| s * s).sum::<f32>() / tail.len() as f32).sqrt();
        assert!(
            out_rms > input_rms * 1.5,
            "expected boost at 500 Hz: in_rms={input_rms} out_rms={out_rms}"
        );
    }

    #[test]
    fn output_is_finite_and_length_preserved() {
        let mut eq = Equalizer30::new(48_000.0);
        eq.set_param(PARAM_BAND_0 + 3, 9.0);
        eq.set_param(PARAM_BAND_0 + 22, -6.0);
        let input = sine_sweep_stereo(512);
        let mut buf = input.clone();
        eq.process(&mut buf, 2);
        assert_eq!(buf.len(), input.len());
        assert!(buf.iter().all(|s| s.is_finite()));
    }

    #[test]
    fn output_is_hard_clipped() {
        // Several stacked boosts on a loud tone must still respect the ±1.5 ceiling.
        let mut eq = Equalizer30::new(48_000.0);
        for band in 12..18 {
            eq.set_param(PARAM_BAND_0 + band as u32, 18.0);
        }
        let mut buf: Vec<f32> = (0..4_096)
            .map(|i| {
                let t = i as f32 / 48_000.0;
                0.9 * (2.0 * std::f32::consts::PI * 700.0 * t).sin()
            })
            .collect();
        eq.process(&mut buf, 1);
        assert!(buf.iter().all(|&s| s.abs() <= HARD_CLIP + 1e-6));
    }

    #[test]
    fn reset_restores_reproducibility() {
        let mut eq = Equalizer30::new(48_000.0);
        eq.set_param(PARAM_BAND_0 + 8, 6.0);
        let input = sine_sweep_stereo(256);

        let mut first = input.clone();
        eq.process(&mut first, 2);
        eq.reset();
        let mut second = input.clone();
        eq.process(&mut second, 2);
        assert_eq!(first, second);
    }

    #[test]
    fn mix_zero_leaves_signal_dry() {
        // B.6.2: with a band boosted but mix = 0 the effect now passes the dry
        // signal through (it used to ignore mix and run fully wet).
        let mut eq = Equalizer30::new(48_000.0);
        eq.set_param(PARAM_BAND_0 + 10, 12.0);
        eq.set_param(PARAM_MIX, 0.0);
        let input = sine_sweep_stereo(512);
        let mut buf = input.clone();
        eq.process(&mut buf, 2);
        assert_eq!(buf, input, "mix=0 must pass the dry signal through");
    }

    #[test]
    fn mix_blends_wet_and_dry() {
        // B.6.2: at mix = 0.5 the output is the average of the dry input and the
        // fully-wet filter output.
        let input = sine_sweep_stereo(1_024);

        let mut wet_eq = Equalizer30::new(48_000.0);
        wet_eq.set_param(PARAM_BAND_0 + 12, 10.0);
        wet_eq.set_param(PARAM_MIX, 1.0);
        let mut wet = input.clone();
        wet_eq.process(&mut wet, 2);

        let mut mid_eq = Equalizer30::new(48_000.0);
        mid_eq.set_param(PARAM_BAND_0 + 12, 10.0);
        mid_eq.set_param(PARAM_MIX, 0.5);
        let mut mid = input.clone();
        mid_eq.process(&mut mid, 2);

        for i in 0..input.len() {
            let expected = 0.5 * input[i] + 0.5 * wet[i];
            assert!(
                (mid[i] - expected).abs() < 1.0e-5,
                "sample {i}: {} != blend {expected}",
                mid[i]
            );
        }
    }

    #[test]
    fn state_stays_out_of_subnormals_after_long_decay() {
        // 2.12: excite the IIR states, then feed silence; the recursive tail
        // must settle to clean zeros (or normals), never park in subnormals.
        let mut eq = Equalizer30::new(48_000.0);
        eq.set_param(PARAM_BAND_0 + 6, 10.0);
        eq.set_param(PARAM_BAND_0 + 19, -8.0);

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
        // Quad input must filter every channel; the per-channel state grows once.
        let mut eq = Equalizer30::new(48_000.0);
        eq.set_param(PARAM_BAND_0 + 14, 12.0);
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
        assert!(eq.z1.len() >= 4 * BANDS);
        // Channel 3 (the highest) must have been filtered, not left untouched.
        let ch3_energy: f32 = (0..frames).map(|f| buf[f * 4 + 3].powi(2)).sum();
        assert!(ch3_energy > 0.0);
    }

    #[test]
    fn matches_reference_dsp_within_minus_60_db() {
        // 2.2 acceptance: the production f32 DSP must reproduce the reference
        // (f64 ground truth, transcribed from the C# algorithm) to better than
        // -60 dB RMS error across several gain profiles.
        let input = sine_sweep_stereo(8_192);
        let profiles: &[&[(usize, f32)]] = &[
            &[(2, 6.0), (10, -4.0), (18, 8.0), (26, -6.0)],
            &[(0, 12.0), (14, -12.0), (29, 9.0)],
            &[(5, 3.5), (6, 4.0), (7, 4.5), (8, 4.0), (9, 3.5)],
        ];

        for profile in profiles {
            let mut eq = Equalizer30::new(48_000.0);
            let mut reference = Reference::new(48_000.0, 2);
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
                "profile {profile:?}: RMS error {err:.1} dB exceeds -60 dB"
            );
        }
    }

    /// A narrower bell has to leave its neighbours alone. Matchering deconvolves its
    /// band gains against a Q it picks per band, so a fixed-Q filterbank draws a
    /// different curve out of the same numbers.
    #[test]
    fn band_q_changes_the_skirt_of_the_bell() {
        let tone = |freq: f32, frames: usize| -> Vec<f32> {
            (0..frames)
                .flat_map(|i| {
                    let v = 0.25 * (2.0 * std::f32::consts::PI * freq * i as f32 / 48_000.0).sin();
                    [v, v]
                })
                .collect::<Vec<f32>>()
        };

        // Band 17 is 1 kHz; 1.25 kHz is the neighbouring ISO centre.
        let neighbour = tone(1_250.0, 4_800);

        let boost_at = |q: f32| -> f64 {
            let mut eq = Equalizer30::new(48_000.0);
            eq.set_param(PARAM_BAND_Q_0 + 17, q);
            eq.set_param(PARAM_BAND_0 + 17, 12.0);
            let mut buf = neighbour.clone();
            eq.process(&mut buf, 2);

            // Compare the settled tail, past the filter's own start-up.
            let tail = &buf[buf.len() / 2..];
            let dry = &neighbour[neighbour.len() / 2..];
            let rms = |x: &[f32]| -> f64 {
                (x.iter().map(|v| (*v as f64) * (*v as f64)).sum::<f64>() / x.len() as f64).sqrt()
            };
            20.0 * (rms(tail) / rms(dry)).log10()
        };

        let wide = boost_at(1.0);
        let narrow = boost_at(9.0);

        assert!(
            wide > narrow + 3.0,
            "a wide bell must spill onto the neighbour far more than a narrow one \
             (Q=1 gave {wide:.2} dB, Q=9 gave {narrow:.2} dB)"
        );
    }

    /// Both shape parameters default to the 1/3-octave values, so an EQ nobody
    /// reshapes is bit-identical to one built before they existed.
    #[test]
    fn shape_defaults_match_the_fixed_third_octave_values() {
        let eq = Equalizer30::new(48_000.0);
        for band in 0..BANDS as u32 {
            assert_eq!(eq.get_param(PARAM_BAND_Q_0 + band), Some(BAND_Q));
            assert_eq!(
                eq.get_param(PARAM_BAND_FREQ_0 + band),
                Some(STANDARD_FREQUENCIES[band as usize])
            );
        }
    }

    /// Moving a band's centre moves where it bites.
    #[test]
    fn band_frequency_moves_the_bell() {
        let mut eq = Equalizer30::new(48_000.0);
        eq.set_param(PARAM_BAND_FREQ_0, 5_000.0);
        assert_eq!(eq.get_param(PARAM_BAND_FREQ_0), Some(5_000.0));

        eq.set_param(PARAM_BAND_0, 12.0);

        let frames = 4_800;
        let mut buf: Vec<f32> = (0..frames)
            .flat_map(|i| {
                let v = 0.25 * (2.0 * std::f32::consts::PI * 5_000.0 * i as f32 / 48_000.0).sin();
                [v, v]
            })
            .collect();
        let dry = buf.clone();
        eq.process(&mut buf, 2);

        let rms = |x: &[f32]| -> f64 {
            (x.iter().map(|v| (*v as f64) * (*v as f64)).sum::<f64>() / x.len() as f64).sqrt()
        };
        let lift = 20.0 * (rms(&buf[frames..]) / rms(&dry[frames..])).log10();
        assert!(
            lift > 6.0,
            "band 0 retuned to 5 kHz has to lift a 5 kHz tone, got {lift:.2} dB"
        );
    }
}
