//! RBJ biquad sections shared by the SmartMaster stages (subsonic HPF, the
//! parametric EQ and the subharmonic synth's band-passes).
//!
//! The crossover keeps its own copy — it is a straight port and predates this
//! module.

use crate::denormal;

/// Normalized biquad coefficients (`a0` already divided out).
#[derive(Clone, Copy)]
pub(crate) struct Coeffs {
    pub b0: f32,
    pub b1: f32,
    pub b2: f32,
    pub a1: f32,
    pub a2: f32,
}

impl Coeffs {
    /// Pass-through.
    pub const IDENTITY: Self = Self {
        b0: 1.0,
        b1: 0.0,
        b2: 0.0,
        a1: 0.0,
        a2: 0.0,
    };

    fn normalize(b: [f32; 3], a: [f32; 3]) -> Self {
        let inv = 1.0 / a[0];
        Self {
            b0: b[0] * inv,
            b1: b[1] * inv,
            b2: b[2] * inv,
            a1: a[1] * inv,
            a2: a[2] * inv,
        }
    }

    /// 2nd-order high-pass at `freq` with the given Q.
    pub fn highpass(sample_rate: f32, freq: f32, q: f32) -> Self {
        let (sin_w, cos_w) = omega(sample_rate, freq);
        let alpha = sin_w / (2.0 * q);
        Self::normalize(
            [(1.0 + cos_w) / 2.0, -(1.0 + cos_w), (1.0 + cos_w) / 2.0],
            [1.0 + alpha, -2.0 * cos_w, 1.0 - alpha],
        )
    }

    /// Band-pass with unity gain at the centre.
    pub fn bandpass(sample_rate: f32, freq: f32, q: f32) -> Self {
        let (sin_w, cos_w) = omega(sample_rate, freq);
        let alpha = sin_w / (2.0 * q);
        Self::normalize(
            [alpha, 0.0, -alpha],
            [1.0 + alpha, -2.0 * cos_w, 1.0 - alpha],
        )
    }

    /// Peaking bell, `gain_db` at the centre.
    pub fn peaking(sample_rate: f32, freq: f32, q: f32, gain_db: f32) -> Self {
        let (sin_w, cos_w) = omega(sample_rate, freq);
        let alpha = sin_w / (2.0 * q);
        let a = 10.0f32.powf(gain_db / 40.0);
        Self::normalize(
            [1.0 + alpha * a, -2.0 * cos_w, 1.0 - alpha * a],
            [1.0 + alpha / a, -2.0 * cos_w, 1.0 - alpha / a],
        )
    }

    /// Low shelf, `gain_db` below the corner.
    pub fn low_shelf(sample_rate: f32, freq: f32, q: f32, gain_db: f32) -> Self {
        let (sin_w, cos_w) = omega(sample_rate, freq);
        let a = 10.0f32.powf(gain_db / 40.0);
        let beta = 2.0 * a.sqrt() * (sin_w / (2.0 * q));
        let ap1 = a + 1.0;
        let am1 = a - 1.0;

        Self::normalize(
            [
                a * (ap1 - am1 * cos_w + beta),
                2.0 * a * (am1 - ap1 * cos_w),
                a * (ap1 - am1 * cos_w - beta),
            ],
            [
                ap1 + am1 * cos_w + beta,
                -2.0 * (am1 + ap1 * cos_w),
                ap1 + am1 * cos_w - beta,
            ],
        )
    }

    /// High shelf, `gain_db` above the corner.
    pub fn high_shelf(sample_rate: f32, freq: f32, q: f32, gain_db: f32) -> Self {
        let (sin_w, cos_w) = omega(sample_rate, freq);
        let a = 10.0f32.powf(gain_db / 40.0);
        let beta = 2.0 * a.sqrt() * (sin_w / (2.0 * q));
        let ap1 = a + 1.0;
        let am1 = a - 1.0;

        Self::normalize(
            [
                a * (ap1 + am1 * cos_w + beta),
                -2.0 * a * (am1 + ap1 * cos_w),
                a * (ap1 + am1 * cos_w - beta),
            ],
            [
                ap1 - am1 * cos_w + beta,
                2.0 * (am1 - ap1 * cos_w),
                ap1 - am1 * cos_w - beta,
            ],
        )
    }
}

/// Clamped angular frequency, as sin/cos. Kept below Nyquist so a silly
/// parameter can't blow the coefficients up.
fn omega(sample_rate: f32, freq: f32) -> (f32, f32) {
    let sr = if sample_rate > 0.0 {
        sample_rate
    } else {
        44_100.0
    };
    let f = freq.clamp(1.0, sr * 0.49);
    let w = 2.0 * std::f32::consts::PI * f / sr;
    (w.sin(), w.cos())
}

/// Transposed-DF-II state for one section on one channel.
#[derive(Clone, Copy, Default)]
pub(crate) struct State {
    z1: f32,
    z2: f32,
}

impl State {
    #[inline]
    pub fn tick(&mut self, c: &Coeffs, x: f32) -> f32 {
        let y = c.b0 * x + self.z1;
        self.z1 = denormal::flush(c.b1 * x - c.a1 * y + self.z2);
        self.z2 = denormal::flush(c.b2 * x - c.a2 * y);
        y
    }

    pub fn clear(&mut self) {
        self.z1 = 0.0;
        self.z2 = 0.0;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Magnitude response at `freq` by running a tone through and measuring RMS
    /// after the transient.
    fn magnitude(c: &Coeffs, sample_rate: f32, freq: f32) -> f32 {
        let mut s = State::default();
        let n = 20_000;
        let mut sum = 0.0f64;
        for i in 0..n {
            let x = (2.0 * std::f32::consts::PI * freq * i as f32 / sample_rate).sin();
            let y = s.tick(c, x);
            if i > n / 2 {
                sum += (y * y) as f64;
            }
        }
        ((sum / (n / 2) as f64).sqrt() * std::f64::consts::SQRT_2) as f32
    }

    #[test]
    fn highpass_cuts_below_and_passes_above() {
        let c = Coeffs::highpass(48_000.0, 100.0, 0.707);
        assert!(magnitude(&c, 48_000.0, 20.0) < 0.1);
        assert!(magnitude(&c, 48_000.0, 1_000.0) > 0.95);
    }

    #[test]
    fn bandpass_peaks_at_centre() {
        let c = Coeffs::bandpass(48_000.0, 60.0, 2.0);
        let centre = magnitude(&c, 48_000.0, 60.0);
        assert!(centre > 0.9 && centre < 1.1);
        assert!(magnitude(&c, 48_000.0, 600.0) < 0.2);
    }

    #[test]
    fn peaking_hits_its_gain() {
        let c = Coeffs::peaking(48_000.0, 1_000.0, 4.318, 6.0);
        let g = 20.0 * magnitude(&c, 48_000.0, 1_000.0).log10();
        assert!((g - 6.0).abs() < 0.3, "got {g} dB");
    }
}
