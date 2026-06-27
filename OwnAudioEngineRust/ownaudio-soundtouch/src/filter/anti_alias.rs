//! Anti-alias low-pass filter — port of `AntiAliasFilter.cs`.
//!
//! Designs a sinc × Hamming FIR realising a given cut-off (normalised so that
//! the Nyquist frequency is `0.5`) and applies it ahead of / after rate
//! transposition to prevent fold-over of high frequencies.

use crate::error::StResult;
use crate::fifo_buffer::FifoSampleBuffer;
use crate::filter::fir_filter::FirFilter;

use std::f64::consts::PI;

/// Sinc-window anti-alias low-pass filter.
pub struct AntiAliasFilter {
    fir: FirFilter,
    cutoff_freq: f64,
    length: usize,
}

impl AntiAliasFilter {
    /// Creates a filter with `length` taps and the default cut-off (`0.5`).
    pub fn new(length: usize) -> Self {
        let mut f = AntiAliasFilter {
            fir: FirFilter::new(),
            cutoff_freq: 0.5,
            length: 0,
        };
        f.set_length(length);
        f
    }

    /// Number of FIR taps.
    #[inline]
    pub fn length(&self) -> usize {
        self.length
    }

    /// Sets the tap count and recomputes coefficients (configuration time).
    pub fn set_length(&mut self, length: usize) {
        self.length = length;
        self.calculate_coefficients();
    }

    /// Sets the cut-off edge frequency (Nyquist = `0.5`) and recomputes
    /// coefficients (configuration time).
    pub fn set_cutoff_freq(&mut self, new_cutoff: f64) {
        self.cutoff_freq = new_cutoff;
        self.calculate_coefficients();
    }

    /// Applies the filter to a raw interleaved buffer; returns output frame
    /// count.
    pub fn evaluate(
        &self,
        dest: &mut [f32],
        src: &[f32],
        num_samples: usize,
        num_channels: usize,
    ) -> usize {
        self.fir.evaluate(dest, src, num_samples, num_channels)
    }

    /// Pumps samples from `src_buf` through the filter into `dst_buf`, removing
    /// the consumed frames from `src_buf` and appending the produced frames to
    /// `dst_buf`.  Returns the number of frames produced.
    pub fn evaluate_buffers(
        &self,
        dst_buf: &mut FifoSampleBuffer,
        src_buf: &mut FifoSampleBuffer,
    ) -> usize {
        let num_channels = src_buf.channels();
        let num_src = src_buf.available_samples();
        let result = {
            let dest = dst_buf.ptr_end(num_src);
            let source = src_buf.ptr_begin();
            self.fir.evaluate(dest, source, num_src, num_channels)
        };
        src_buf.receive_samples(result);
        dst_buf.commit_put(result);
        result
    }

    /// Designs the FIR coefficients realising the current cut-off frequency.
    fn calculate_coefficients(&mut self) {
        let length = self.length;
        if length < 2 {
            return;
        }
        let mut work = vec![0.0_f64; length];
        let mut coefficients = vec![0.0_f32; length];

        let wc = 2.0 * PI * self.cutoff_freq;
        let temp_coefficient = (2.0 * PI) / length as f64;

        let mut sum = 0.0_f64;
        for (i, w_slot) in work.iter_mut().enumerate() {
            let cnt_temp = i as f64 - (length as f64 / 2.0);
            let temp = cnt_temp * wc;
            let h = if temp != 0.0 {
                temp.sin() / temp
            } else {
                1.0
            };
            let win = 0.54 + (0.46 * (temp_coefficient * cnt_temp).cos());
            let v = win * h;
            *w_slot = v;
            sum += v;
        }

        // Scale so that the result can be divided by 16384 (2^14).
        let scale_coefficient = 16384.0 / sum;
        for (i, c) in coefficients.iter_mut().enumerate() {
            let mut temp = work[i] * scale_coefficient;
            temp += if temp >= 0.0 { 0.5 } else { -0.5 };
            *c = temp as f32;
        }

        let _ = self.set_coefficients(&coefficients);
    }

    fn set_coefficients(&mut self, coeffs: &[f32]) -> StResult<()> {
        self.fir.set_coefficients(coeffs, 14)
    }
}
