//! FIR filter — port of `FIRFilter.cs`.
//!
//! Generic sinc-window FIR convolution.  The C# version hand-rolls AVX/SSE
//! intrinsics; this port keeps the scalar reference form with `f64`
//! accumulation (matching the C# `double` accumulators bit-for-bit in intent),
//! relying on the Rust auto-vectorizer for throughput.  That makes the result
//! the most faithful possible match to the C# scalar fallback, which is what the
//! RMS reference test compares against.

use crate::error::{ErrorCode, StResult};
use crate::MAX_CHANNELS;

/// Sinc-window FIR filter with mono / stereo / multichannel evaluation.
pub struct FirFilter {
    /// Number of FIR taps.
    length: usize,
    /// Scaled coefficients, one per tap.
    coeffs: Vec<f32>,
    /// Scaled coefficients duplicated per stereo lane (`c0 c0 c1 c1 …`).
    coeffs_stereo: Vec<f32>,
}

impl FirFilter {
    /// Creates an empty, uninitialised filter.
    pub fn new() -> Self {
        FirFilter {
            length: 0,
            coeffs: Vec::new(),
            coeffs_stereo: Vec::new(),
        }
    }

    /// Number of FIR taps currently configured.
    #[inline]
    pub fn length(&self) -> usize {
        self.length
    }

    /// Installs new coefficients, scaled by `1 / 2^result_div_factor`.
    ///
    /// Configuration-time call; allocates.  `coeffs` must be non-empty and a
    /// multiple of eight, mirroring the C# contract.
    pub fn set_coefficients(&mut self, coeffs: &[f32], result_div_factor: i32) -> StResult<()> {
        if coeffs.is_empty() || !coeffs.len().is_multiple_of(8) {
            return Err(ErrorCode::NotInitialized);
        }

        self.length = coeffs.len();
        let scale = 1.0_f64 / 2.0_f64.powi(result_div_factor);

        self.coeffs.clear();
        self.coeffs.reserve(self.length);
        self.coeffs_stereo.clear();
        self.coeffs_stereo.reserve(self.length * 2);

        for &c in coeffs {
            let v = (c as f64 * scale) as f32;
            self.coeffs.push(v);
            self.coeffs_stereo.push(v);
            self.coeffs_stereo.push(v);
        }
        Ok(())
    }

    /// Applies the filter, writing to `dest` and returning the number of output
    /// frames (which is `num_samples - length` rounded to the tap alignment).
    pub fn evaluate(
        &self,
        dest: &mut [f32],
        src: &[f32],
        num_samples: usize,
        num_channels: usize,
    ) -> usize {
        if self.length == 0 || num_samples < self.length {
            return 0;
        }
        match num_channels {
            1 => self.evaluate_mono(dest, src, num_samples),
            2 => self.evaluate_stereo(dest, src, num_samples),
            _ => self.evaluate_multi(dest, src, num_samples, num_channels),
        }
    }

    fn evaluate_mono(&self, dest: &mut [f32], src: &[f32], num_samples: usize) -> usize {
        let ilength = self.length & !7;
        let end = num_samples - ilength;
        let coeffs = &self.coeffs[..ilength];
        for j in 0..end {
            let mut sum = 0.0_f64;
            let p = &src[j..];
            for i in 0..ilength {
                sum += (p[i] * coeffs[i]) as f64;
            }
            dest[j] = sum as f32;
        }
        end
    }

    fn evaluate_stereo(&self, dest: &mut [f32], src: &[f32], num_samples: usize) -> usize {
        let ilength = self.length & !7;
        let end = 2 * (num_samples - ilength);
        let coeffs = &self.coeffs_stereo[..ilength * 2];
        let mut j = 0;
        while j < end {
            let mut sum_l = 0.0_f64;
            let mut sum_r = 0.0_f64;
            let p = &src[j..];
            for k in 0..ilength {
                sum_l += (p[2 * k] * coeffs[2 * k]) as f64;
                sum_r += (p[2 * k + 1] * coeffs[2 * k + 1]) as f64;
            }
            dest[j] = sum_l as f32;
            dest[j + 1] = sum_r as f32;
            j += 2;
        }
        num_samples - ilength
    }

    fn evaluate_multi(
        &self,
        dest: &mut [f32],
        src: &[f32],
        num_samples: usize,
        num_channels: usize,
    ) -> usize {
        let nch = num_channels.min(MAX_CHANNELS);
        let ilength = self.length & !7;
        let end = nch * (num_samples - ilength);
        let mut sums = [0.0_f64; MAX_CHANNELS];

        let mut j = 0;
        while j < end {
            for s in sums.iter_mut().take(nch) {
                *s = 0.0;
            }
            for i in 0..ilength {
                let coef = self.coeffs[i];
                let base = j + i * nch;
                for (c, s) in sums.iter_mut().take(nch).enumerate() {
                    *s += (src[base + c] * coef) as f64;
                }
            }
            for (c, s) in sums.iter().take(nch).enumerate() {
                dest[j + c] = *s as f32;
            }
            j += nch;
        }
        num_samples - ilength
    }
}

impl Default for FirFilter {
    fn default() -> Self {
        Self::new()
    }
}
