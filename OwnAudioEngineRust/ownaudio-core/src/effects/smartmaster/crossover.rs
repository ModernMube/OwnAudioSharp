//! Linkwitz-Riley 4th-order crossover (two cascaded Butterworth biquads).
//!
//! Faithful Rust port of the reference C# `CrossoverFilter` used inside the
//! SmartMaster mastering chain: each input channel is split into a low band
//! (sub) and a high band (L/R) by a low-pass / high-pass pair, each realised as
//! two cascaded RBJ Butterworth (`Q = 0.707`) biquads — a 4th-order
//! Linkwitz-Riley alignment.  Per-channel Transposed-Direct-Form-II state is
//! kept for up to two channels (the mastering chain is stereo).
//!
//! **Denormal protection:** the recursive states are flushed every sample so a
//! decaying tail never parks in the subnormal range.

use crate::denormal;

/// Channels the per-channel biquad state is kept for (stereo mastering chain).
const CHANNELS: usize = 2;

/// One cascaded (2-stage) Butterworth section's normalized coefficients.
#[derive(Clone, Copy)]
struct BiquadCoeffs {
    b0: f32,
    b1: f32,
    b2: f32,
    a1: f32,
    a2: f32,
}

impl BiquadCoeffs {
    const IDENTITY: Self = Self {
        b0: 1.0,
        b1: 0.0,
        b2: 0.0,
        a1: 0.0,
        a2: 0.0,
    };
}

/// Transposed-Direct-Form-II state for one biquad on one channel.
#[derive(Clone, Copy, Default)]
struct BiquadState {
    z1: f32,
    z2: f32,
}

/// Linkwitz-Riley 4th-order crossover splitting each channel into a low (sub)
/// and a high (L/R) band.
pub struct Crossover {
    sample_rate: f32,
    frequency: f32,

    lp: BiquadCoeffs,
    hp: BiquadCoeffs,

    // Two cascaded stages per band, per channel: `[channel][stage]`.
    lp_state: [[BiquadState; 2]; CHANNELS],
    hp_state: [[BiquadState; 2]; CHANNELS],
}

impl Crossover {
    /// Creates a crossover at `frequency` Hz for the given sample rate.
    pub fn new(sample_rate: f32, frequency: f32) -> Self {
        let sample_rate = if sample_rate > 0.0 {
            sample_rate
        } else {
            44_100.0
        };
        let mut c = Self {
            sample_rate,
            frequency: frequency.max(1.0),
            lp: BiquadCoeffs::IDENTITY,
            hp: BiquadCoeffs::IDENTITY,
            lp_state: [[BiquadState::default(); 2]; CHANNELS],
            hp_state: [[BiquadState::default(); 2]; CHANNELS],
        };
        c.recompute();
        c
    }

    /// Sets a new crossover frequency, recomputing coefficients and clearing
    /// state when it actually changes (reference parity).
    pub fn set_frequency(&mut self, frequency: f32) {
        let frequency = frequency.max(1.0);
        if (self.frequency - frequency).abs() > 0.01 {
            self.frequency = frequency;
            self.recompute();
            self.reset();
        }
    }

    /// Recomputes the low-pass / high-pass RBJ Butterworth coefficients (both
    /// cascade stages share the same coefficients, as in the reference).
    fn recompute(&mut self) {
        let omega = 2.0 * std::f32::consts::PI * self.frequency / self.sample_rate;
        let sin_omega = omega.sin();
        let cos_omega = omega.cos();
        let alpha = sin_omega / (2.0 * 0.707); // Q = 0.707 (Butterworth)

        let a0 = 1.0 + alpha;
        let a1 = -2.0 * cos_omega;
        let a2 = 1.0 - alpha;
        let inv_a0 = 1.0 / a0;

        // Low-pass.
        let lp_b0 = (1.0 - cos_omega) / 2.0;
        let lp_b1 = 1.0 - cos_omega;
        let lp_b2 = (1.0 - cos_omega) / 2.0;
        self.lp = BiquadCoeffs {
            b0: lp_b0 * inv_a0,
            b1: lp_b1 * inv_a0,
            b2: lp_b2 * inv_a0,
            a1: a1 * inv_a0,
            a2: a2 * inv_a0,
        };

        // High-pass.
        let hp_b0 = (1.0 + cos_omega) / 2.0;
        let hp_b1 = -(1.0 + cos_omega);
        let hp_b2 = (1.0 + cos_omega) / 2.0;
        self.hp = BiquadCoeffs {
            b0: hp_b0 * inv_a0,
            b1: hp_b1 * inv_a0,
            b2: hp_b2 * inv_a0,
            a1: a1 * inv_a0,
            a2: a2 * inv_a0,
        };
    }

    /// Splits one channel's `frames` samples into a high band (written back into
    /// `io_high` in place) and a low band (written into `sub_low`).
    ///
    /// Mirrors the reference call convention where the input buffer is reused as
    /// the high-band output; each sample is read before the high value overwrites
    /// it. `channel` selects the per-channel filter state (0 or 1).
    pub fn process_channel(&mut self, io_high: &mut [f32], sub_low: &mut [f32], channel: usize) {
        let channel = channel.min(CHANNELS - 1);
        let frames = io_high.len().min(sub_low.len());

        let lp = self.lp;
        let hp = self.hp;
        let mut lp_s = self.lp_state[channel];
        let mut hp_s = self.hp_state[channel];

        for i in 0..frames {
            let sample = io_high[i];

            // Low-pass (sub) — two cascaded stages.
            let lp1 = lp.b0 * sample + lp_s[0].z1;
            lp_s[0].z1 = denormal::flush(lp.b1 * sample - lp.a1 * lp1 + lp_s[0].z2);
            lp_s[0].z2 = denormal::flush(lp.b2 * sample - lp.a2 * lp1);
            let lp2 = lp.b0 * lp1 + lp_s[1].z1;
            lp_s[1].z1 = denormal::flush(lp.b1 * lp1 - lp.a1 * lp2 + lp_s[1].z2);
            lp_s[1].z2 = denormal::flush(lp.b2 * lp1 - lp.a2 * lp2);
            sub_low[i] = lp2;

            // High-pass (L/R) — two cascaded stages.
            let hp1 = hp.b0 * sample + hp_s[0].z1;
            hp_s[0].z1 = denormal::flush(hp.b1 * sample - hp.a1 * hp1 + hp_s[0].z2);
            hp_s[0].z2 = denormal::flush(hp.b2 * sample - hp.a2 * hp1);
            let hp2 = hp.b0 * hp1 + hp_s[1].z1;
            hp_s[1].z1 = denormal::flush(hp.b1 * hp1 - hp.a1 * hp2 + hp_s[1].z2);
            hp_s[1].z2 = denormal::flush(hp.b2 * hp1 - hp.a2 * hp2);
            io_high[i] = hp2;
        }

        self.lp_state[channel] = lp_s;
        self.hp_state[channel] = hp_s;
    }

    /// Clears every channel's filter state.
    pub fn reset(&mut self) {
        self.lp_state = [[BiquadState::default(); 2]; CHANNELS];
        self.hp_state = [[BiquadState::default(); 2]; CHANNELS];
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn rms(buf: &[f32]) -> f32 {
        (buf.iter().map(|s| s * s).sum::<f32>() / buf.len() as f32).sqrt()
    }

    fn tone(freq: f32, frames: usize) -> Vec<f32> {
        (0..frames)
            .map(|i| (2.0 * std::f32::consts::PI * freq * i as f32 / 48_000.0).sin())
            .collect()
    }

    #[test]
    fn low_tone_goes_to_sub_band() {
        let mut x = Crossover::new(48_000.0, 200.0);
        let mut high = tone(50.0, 4_096);
        let mut sub = vec![0.0f32; high.len()];
        // Warm-up then measure the steady-state tail.
        x.process_channel(&mut high, &mut sub, 0);
        let tail_hi = rms(&high[high.len() / 2..]);
        let tail_sub = rms(&sub[sub.len() / 2..]);
        assert!(tail_sub > tail_hi, "50 Hz should favour the sub band");
    }

    #[test]
    fn high_tone_goes_to_lr_band() {
        let mut x = Crossover::new(48_000.0, 200.0);
        let mut high = tone(2_000.0, 4_096);
        let mut sub = vec![0.0f32; high.len()];
        x.process_channel(&mut high, &mut sub, 0);
        let tail_hi = rms(&high[high.len() / 2..]);
        let tail_sub = rms(&sub[sub.len() / 2..]);
        assert!(tail_hi > tail_sub, "2 kHz should favour the L/R band");
    }

    #[test]
    fn reset_clears_state() {
        let mut x = Crossover::new(48_000.0, 120.0);
        let mut high = tone(80.0, 512);
        let mut sub = vec![0.0f32; high.len()];
        x.process_channel(&mut high, &mut sub, 0);
        x.reset();

        let mut h1 = tone(80.0, 512);
        let mut s1 = vec![0.0f32; h1.len()];
        x.process_channel(&mut h1, &mut s1, 0);
        x.reset();
        let mut h2 = tone(80.0, 512);
        let mut s2 = vec![0.0f32; h2.len()];
        x.process_channel(&mut h2, &mut s2, 0);
        assert_eq!(h1, h2);
        assert_eq!(s1, s2);
    }

    #[test]
    fn output_is_finite() {
        let mut x = Crossover::new(44_100.0, 80.0);
        let mut high = tone(100.0, 1_000);
        let mut sub = vec![0.0f32; high.len()];
        x.process_channel(&mut high, &mut sub, 1);
        assert!(high.iter().chain(sub.iter()).all(|s| s.is_finite()));
    }
}
