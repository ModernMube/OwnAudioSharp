//! FIR filter — port of `FIRFilter.cs`.
//!
//! Sinc-window FIR convolution. Mono and stereo sum in [`lanes`] so the loop
//! vectorises; multichannel stays scalar.

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
        for (j, d) in dest[..end].iter_mut().enumerate() {
            *d = lanes::<8>(&src[j..j + ilength], coeffs).iter().sum();
        }
        end
    }

    fn evaluate_stereo(&self, dest: &mut [f32], src: &[f32], num_samples: usize) -> usize {
        let ilength = self.length & !7;
        let frames = num_samples - ilength;
        let coeffs = &self.coeffs_stereo[..ilength * 2];
        for (j, d) in dest[..frames * 2].chunks_exact_mut(2).enumerate() {
            let acc = lanes::<16>(&src[2 * j..2 * (j + ilength)], coeffs);
            d[0] = acc.iter().step_by(2).sum();
            d[1] = acc.iter().skip(1).step_by(2).sum();
        }
        frames
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

/// Dot product split into `N` independent sums — a single float sum can't be reordered, so
/// it never vectorises. For interleaved stereo (`N = 16`) even lanes are left, odd ones right.
#[inline(always)]
fn lanes<const N: usize>(src: &[f32], coeffs: &[f32]) -> [f32; N] {
    let mut acc = [0.0f32; N];
    for (s, c) in src.chunks_exact(N).zip(coeffs.chunks_exact(N)) {
        for k in 0..N {
            acc[k] += s[k] * c[k];
        }
    }
    acc
}

impl Default for FirFilter {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The old per-tap `f64` loop, as reference.
    fn scalar(coeffs: &[f32], src: &[f32], frames: usize, ch: usize) -> Vec<f32> {
        let taps = coeffs.len();
        (0..(frames - taps) * ch)
            .map(|j| {
                let (frame, c) = (j / ch, j % ch);
                (0..taps)
                    .map(|i| (src[(frame + i) * ch + c] * coeffs[i]) as f64)
                    .sum::<f64>() as f32
            })
            .collect()
    }

    #[test]
    fn lanes_match_the_scalar_loop() {
        let aa = crate::filter::AntiAliasFilter::new(64);
        let mut fir = FirFilter::new();
        let coeffs: Vec<f32> = (0..64)
            .map(|i| ((i as f32 - 31.5) * 0.19).sin() / (i as f32 - 31.5) * 0.3)
            .collect();
        fir.set_coefficients(&coeffs, 0).unwrap();

        for ch in [1usize, 2] {
            let frames = 700;
            let src: Vec<f32> = (0..frames * ch)
                .map(|n| (n as f32 * 0.37).sin() * 0.8 + (n as f32 * 2.9).cos() * 0.2)
                .collect();
            let want = scalar(&fir.coeffs, &src, frames, ch);

            let mut got = vec![0.0f32; frames * ch];
            assert_eq!(fir.evaluate(&mut got, &src, frames, ch), frames - 64);
            let worst = want
                .iter()
                .zip(&got)
                .map(|(a, b)| (a - b).abs())
                .fold(0.0f32, f32::max);
            assert!(worst < 1e-5, "{ch}ch drifted {worst} from the scalar loop");

            let mut dest = vec![0.0f32; frames * ch];
            assert_eq!(aa.evaluate(&mut dest, &src, frames, ch), frames - 64);
            assert!(dest.iter().all(|v| v.is_finite()));
        }
    }
}
