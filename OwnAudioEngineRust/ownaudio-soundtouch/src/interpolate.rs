//! Sample-rate transposers — ports of `TransposerBase.cs`,
//! `InterpolateLinearFloat.cs` and `InterpolateCubic.cs`.
//!
//! A single [`Transposer`] value carries the running fractional read position
//! and dispatches to the linear or cubic interpolation kernel.  The cubic
//! kernel is the default, matching `TransposerBase.CreateInstance`.

use crate::fifo_buffer::FifoSampleBuffer;

/// Cubic interpolation coefficient matrix (row-major 4×4), from
/// `InterpolateCubic._coeffs`.
const CUBIC_COEFFS: [f32; 16] = [
    -0.5, 1.0, -0.5, 0.0, //
    1.5, -2.5, 0.0, 1.0, //
    -1.5, 2.0, 0.5, 0.0, //
    0.5, -0.5, 0.0, 0.0,
];

/// Interpolation algorithm selector.
#[derive(Debug, Copy, Clone, PartialEq, Eq)]
pub enum Algorithm {
    /// First-order linear interpolation (zero latency).
    Linear,
    /// Third-order cubic interpolation (one sample latency) — default.
    Cubic,
}

/// Fractional-rate sample transposer.
pub struct Transposer {
    algorithm: Algorithm,
    rate: f64,
    channels: usize,
    fract: f64,
}

impl Transposer {
    /// Creates a transposer using the default (cubic) algorithm.
    pub fn new() -> Self {
        Transposer {
            algorithm: Algorithm::Cubic,
            rate: 1.0,
            channels: 0,
            fract: 0.0,
        }
    }

    /// Approximate input/output latency in frames.
    #[inline]
    pub fn latency(&self) -> usize {
        match self.algorithm {
            Algorithm::Linear => 0,
            Algorithm::Cubic => 1,
        }
    }

    /// Current resampling rate.
    #[inline]
    pub fn rate(&self) -> f64 {
        self.rate
    }

    /// Configured channel count.
    #[inline]
    pub fn channels(&self) -> usize {
        self.channels
    }

    /// Sets the resampling rate (`1.0` = no change, `>1` faster, `<1` slower).
    #[inline]
    pub fn set_rate(&mut self, new_rate: f64) {
        self.rate = new_rate;
    }

    /// Sets the channel count and resets the fractional read position.
    #[inline]
    pub fn set_channels(&mut self, channels: usize) {
        self.channels = channels;
        self.reset_registers();
    }

    /// Clears the fractional read position.
    #[inline]
    pub fn reset_registers(&mut self) {
        self.fract = 0.0;
    }

    /// Transposes all available frames from `src` into `dest`.  Returns the
    /// number of output frames produced.
    pub fn transpose(&mut self, dest: &mut FifoSampleBuffer, src: &mut FifoSampleBuffer) -> usize {
        let mut num_src = src.available_samples();
        if num_src == 0 {
            return 0;
        }
        let size_demand = (num_src as f64 / self.rate) as usize + 8;

        let num_output = {
            let pdest = dest.ptr_end(size_demand);
            let psrc = src.ptr_begin();
            match (self.algorithm, self.channels) {
                (Algorithm::Linear, 1) => self.linear_mono(pdest, psrc, &mut num_src),
                (Algorithm::Linear, 2) => self.linear_stereo(pdest, psrc, &mut num_src),
                (Algorithm::Linear, _) => self.linear_multi(pdest, psrc, &mut num_src),
                (Algorithm::Cubic, 1) => self.cubic_mono(pdest, psrc, &mut num_src),
                (Algorithm::Cubic, 2) => self.cubic_stereo(pdest, psrc, &mut num_src),
                (Algorithm::Cubic, _) => self.cubic_multi(pdest, psrc, &mut num_src),
            }
        };

        dest.commit_put(num_output);
        src.receive_samples(num_src);
        num_output
    }

    // ---- linear interpolation ------------------------------------------------

    fn linear_mono(&mut self, dest: &mut [f32], src: &[f32], src_samples: &mut usize) -> usize {
        let src_end = *src_samples as isize - 1;
        let mut src_count = 0isize;
        let mut pos = 0usize;
        let mut index = 0usize;
        while src_count < src_end {
            let out = ((1.0 - self.fract) * src[pos] as f64) + (self.fract * src[pos + 1] as f64);
            dest[index] = out as f32;
            index += 1;
            self.fract += self.rate;
            let whole = self.fract as isize;
            self.fract -= whole as f64;
            pos += whole as usize;
            src_count += whole;
        }
        *src_samples = src_count as usize;
        index
    }

    fn linear_stereo(&mut self, dest: &mut [f32], src: &[f32], src_samples: &mut usize) -> usize {
        let src_end = *src_samples as isize - 1;
        let mut src_count = 0isize;
        let mut off = 0usize;
        let mut index = 0usize;
        while src_count < src_end {
            let ol = ((1.0 - self.fract) * src[off] as f64) + (self.fract * src[off + 2] as f64);
            let or =
                ((1.0 - self.fract) * src[off + 1] as f64) + (self.fract * src[off + 3] as f64);
            dest[2 * index] = ol as f32;
            dest[2 * index + 1] = or as f32;
            index += 1;
            self.fract += self.rate;
            let whole = self.fract as isize;
            self.fract -= whole as f64;
            off += 2 * whole as usize;
            src_count += whole;
        }
        *src_samples = src_count as usize;
        index
    }

    fn linear_multi(&mut self, dest: &mut [f32], src: &[f32], src_samples: &mut usize) -> usize {
        let nch = self.channels;
        let src_end = *src_samples as isize - 1;
        let mut src_count = 0isize;
        let mut off = 0usize;
        let mut ip = 0usize;
        let mut index = 0usize;
        while src_count < src_end {
            let vol1 = (1.0 - self.fract) as f32;
            let ff = self.fract as f32;
            for c in 0..nch {
                dest[ip] = (vol1 * src[off + c]) + (ff * src[off + c + nch]);
                ip += 1;
            }
            index += 1;
            self.fract += self.rate;
            let whole = self.fract as isize;
            self.fract -= whole as f64;
            off += whole as usize * nch;
            src_count += whole;
        }
        *src_samples = src_count as usize;
        index
    }

    // ---- cubic interpolation -------------------------------------------------

    #[inline]
    fn cubic_weights(&self) -> (f32, f32, f32, f32) {
        let x3 = 1.0f32;
        let x2 = self.fract as f32;
        let x1 = x2 * x2;
        let x0 = x1 * x2;
        let c = &CUBIC_COEFFS;
        let y0 = (c[0] * x0) + (c[1] * x1) + (c[2] * x2) + (c[3] * x3);
        let y1 = (c[4] * x0) + (c[5] * x1) + (c[6] * x2) + (c[7] * x3);
        let y2 = (c[8] * x0) + (c[9] * x1) + (c[10] * x2) + (c[11] * x3);
        let y3 = (c[12] * x0) + (c[13] * x1) + (c[14] * x2) + (c[15] * x3);
        (y0, y1, y2, y3)
    }

    fn cubic_mono(&mut self, dest: &mut [f32], src: &[f32], src_samples: &mut usize) -> usize {
        let src_end = *src_samples as isize - 4;
        let mut src_count = 0isize;
        let mut pos = 0usize;
        let mut index = 0usize;
        while src_count < src_end {
            let (y0, y1, y2, y3) = self.cubic_weights();
            dest[index] =
                (y0 * src[pos]) + (y1 * src[pos + 1]) + (y2 * src[pos + 2]) + (y3 * src[pos + 3]);
            index += 1;
            self.fract += self.rate;
            let whole = self.fract as isize;
            self.fract -= whole as f64;
            pos += whole as usize;
            src_count += whole;
        }
        *src_samples = src_count as usize;
        index
    }

    fn cubic_stereo(&mut self, dest: &mut [f32], src: &[f32], src_samples: &mut usize) -> usize {
        let src_end = *src_samples as isize - 4;
        let mut src_count = 0isize;
        let mut off = 0usize;
        let mut index = 0usize;
        while src_count < src_end {
            let (y0, y1, y2, y3) = self.cubic_weights();
            let ol =
                (y0 * src[off]) + (y1 * src[off + 2]) + (y2 * src[off + 4]) + (y3 * src[off + 6]);
            let or = (y0 * src[off + 1])
                + (y1 * src[off + 3])
                + (y2 * src[off + 5])
                + (y3 * src[off + 7]);
            dest[2 * index] = ol;
            dest[2 * index + 1] = or;
            index += 1;
            self.fract += self.rate;
            let whole = self.fract as isize;
            self.fract -= whole as f64;
            off += 2 * whole as usize;
            src_count += whole;
        }
        *src_samples = src_count as usize;
        index
    }

    fn cubic_multi(&mut self, dest: &mut [f32], src: &[f32], src_samples: &mut usize) -> usize {
        let nch = self.channels;
        let src_end = *src_samples as isize - 4;
        let mut src_count = 0isize;
        let mut off = 0usize;
        let mut ip = 0usize;
        let mut index = 0usize;
        while src_count < src_end {
            let (y0, y1, y2, y3) = self.cubic_weights();
            for c in 0..nch {
                dest[ip] = (y0 * src[off + c])
                    + (y1 * src[off + c + nch])
                    + (y2 * src[off + c + 2 * nch])
                    + (y3 * src[off + c + 3 * nch]);
                ip += 1;
            }
            index += 1;
            self.fract += self.rate;
            let whole = self.fract as isize;
            self.fract -= whole as f64;
            off += whole as usize * nch;
            src_count += whole;
        }
        *src_samples = src_count as usize;
        index
    }
}

impl Default for Transposer {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn unity_rate_passes_through_cubic() {
        // At rate 1.0 the cubic transposer reproduces the input (apart from the
        // one-sample group delay) for a smooth signal.
        let mut t = Transposer::new();
        t.set_channels(1);
        t.set_rate(1.0);

        let mut src = FifoSampleBuffer::new(1);
        let mut dst = FifoSampleBuffer::new(1);
        let input: Vec<f32> = (0..64).map(|i| (i as f32 * 0.1).sin()).collect();
        src.put_samples_from(&input, 64);
        let out = t.transpose(&mut dst, &mut src);
        assert!(out > 50);
    }

    #[test]
    fn downrate_produces_more_output() {
        // rate < 1 → slower playback → more output frames than input.
        let mut t = Transposer::new();
        t.set_channels(1);
        t.set_rate(0.5);
        let mut src = FifoSampleBuffer::new(1);
        let mut dst = FifoSampleBuffer::new(1);
        let input = vec![0.25f32; 200];
        src.put_samples_from(&input, 200);
        let out = t.transpose(&mut dst, &mut src);
        assert!(out > 200, "expected upsampled output, got {out}");
    }

    #[test]
    fn uprate_produces_less_output() {
        let mut t = Transposer::new();
        t.set_channels(2);
        t.set_rate(2.0);
        let mut src = FifoSampleBuffer::new(2);
        let mut dst = FifoSampleBuffer::new(2);
        let input = vec![0.1f32; 400 * 2];
        src.put_samples_from(&input, 400);
        let out = t.transpose(&mut dst, &mut src);
        assert!(out < 400 && out > 100, "got {out}");
    }
}
