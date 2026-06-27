//! BPM detector — port of `BpmDetect.cs`.
//!
//! Spectral-flux onset detection followed by normalised-autocorrelation tempo
//! estimation with a log-Gaussian perceptual prior.  The C# version delegates
//! the transform to `OwnAudioFft.Forward`; to keep this crate dependency-free
//! the FFT is implemented here as a small in-place radix-2 Cooley-Tukey routine
//! (the window size is always 512, a power of two).
//!
//! All processing buffers — including the FFT twiddle table — are allocated at
//! construction; [`BpmDetect::input_samples`] performs no heap allocation.

use std::f64::consts::PI;

const FFT_SIZE: usize = 512;
const HOP_SIZE: usize = 128;
const TARGET_SAMPLE_RATE: usize = 11025;
const MIN_BPM: f32 = 45.0;
const MAX_BPM: f32 = 190.0;
const PREFERRED_BPM: f32 = 120.0;
const TEMPO_PRIOR_SIGMA: f32 = 0.9;
const HISTORY_SECONDS: f32 = 8.0;

/// A double-precision complex value (mirrors `System.Numerics.Complex`).
#[derive(Copy, Clone, Default)]
struct Complex {
    re: f64,
    im: f64,
}

/// In-place radix-2 FFT with a precomputed twiddle table.
struct Fft {
    n: usize,
    /// Bit-reversal permutation indices.
    rev: Vec<usize>,
    /// `twiddles[k] = exp(-2πi·k/n)` for `k` in `0..n/2`.
    twiddles: Vec<Complex>,
}

impl Fft {
    fn new(n: usize) -> Self {
        debug_assert!(n.is_power_of_two());
        let bits = n.trailing_zeros();
        let mut rev = vec![0usize; n];
        for (i, slot) in rev.iter_mut().enumerate() {
            *slot = ((i as u32).reverse_bits() >> (32 - bits)) as usize & (n - 1);
        }
        let mut twiddles = vec![Complex::default(); n / 2];
        for (k, t) in twiddles.iter_mut().enumerate() {
            let angle = -2.0 * PI * k as f64 / n as f64;
            t.re = angle.cos();
            t.im = angle.sin();
        }
        Fft { n, rev, twiddles }
    }

    /// Transforms `data` (length `n`) in place.  Allocation-free.
    fn forward(&self, data: &mut [Complex]) {
        let n = self.n;
        for i in 0..n {
            let j = self.rev[i];
            if i < j {
                data.swap(i, j);
            }
        }

        let mut len = 2;
        while len <= n {
            let half = len / 2;
            let stride = n / len;
            let mut base = 0;
            while base < n {
                let mut k = 0;
                for j in 0..half {
                    let w = self.twiddles[k];
                    let a = data[base + j];
                    let b = data[base + j + half];
                    let tw_re = w.re * b.re - w.im * b.im;
                    let tw_im = w.re * b.im + w.im * b.re;
                    data[base + j] = Complex {
                        re: a.re + tw_re,
                        im: a.im + tw_im,
                    };
                    data[base + j + half] = Complex {
                        re: a.re - tw_re,
                        im: a.im - tw_im,
                    };
                    k += stride;
                }
                base += len;
            }
            len <<= 1;
        }
    }
}

/// BPM detector using spectral flux + autocorrelation.
pub struct BpmDetect {
    fft: Fft,
    fft_buffer: Vec<Complex>,
    prev_magnitudes: Vec<f32>,
    window: Vec<f32>,
    slide_buffer: Vec<f32>,
    onset_history: Vec<f32>,
    xcorr_result: Vec<f32>,

    channels: usize,
    history_size: usize,
    decimate_by: usize,
    hop_rate: f32,

    slide_pos: usize,
    hop_accum: usize,
    history_write_pos: usize,
    history_count: usize,
    decimate_count: usize,
    decimate_sum: f64,
}

impl BpmDetect {
    /// Creates a detector for the given channel count and input sample rate.
    pub fn new(num_channels: usize, sample_rate: usize) -> Self {
        let channels = num_channels.max(1);
        let decimate_by = (sample_rate / TARGET_SAMPLE_RATE).max(1);
        let effective_sample_rate = sample_rate as f32 / decimate_by as f32;
        let hop_rate = effective_sample_rate / HOP_SIZE as f32;
        let history_size = (HISTORY_SECONDS * effective_sample_rate / HOP_SIZE as f32) as usize;

        let mut window = vec![0.0f32; FFT_SIZE];
        for (i, w) in window.iter_mut().enumerate() {
            *w = 0.54 - 0.46 * (2.0 * std::f32::consts::PI * i as f32 / (FFT_SIZE as f32 - 1.0)).cos();
        }

        BpmDetect {
            fft: Fft::new(FFT_SIZE),
            fft_buffer: vec![Complex::default(); FFT_SIZE],
            prev_magnitudes: vec![0.0; FFT_SIZE / 2 + 1],
            window,
            slide_buffer: vec![0.0; FFT_SIZE],
            onset_history: vec![0.0; history_size],
            xcorr_result: vec![0.0; history_size / 2 + 1],
            channels,
            history_size,
            decimate_by,
            hop_rate,
            slide_pos: 0,
            hop_accum: 0,
            history_write_pos: 0,
            history_count: 0,
            decimate_count: 0,
            decimate_sum: 0.0,
        }
    }

    /// Feeds `num_samples` interleaved frames into the detector.
    /// Allocation-free.
    pub fn input_samples(&mut self, samples: &[f32], num_samples: usize) {
        for frame in 0..num_samples {
            let base = frame * self.channels;
            let mut mono = 0.0_f64;
            for ch in 0..self.channels {
                mono += samples[base + ch] as f64;
            }

            self.decimate_sum += mono;
            self.decimate_count += 1;

            if self.decimate_count >= self.decimate_by {
                let decimated =
                    (self.decimate_sum / (self.decimate_by * self.channels) as f64) as f32;
                self.decimate_sum = 0.0;
                self.decimate_count = 0;

                self.slide_buffer[self.slide_pos & (FFT_SIZE - 1)] = decimated;
                self.slide_pos += 1;
                self.hop_accum += 1;

                if self.hop_accum >= HOP_SIZE {
                    self.process_hop();
                    self.hop_accum = 0;
                }
            }
        }
    }

    /// Returns the estimated tempo in BPM, or `0.0` if there is not yet enough
    /// data for a reliable estimate.
    pub fn get_bpm(&mut self) -> f32 {
        let count = self.history_count;
        if count < 150 {
            return 0.0;
        }

        let mut history = vec![0.0f32; count];
        let start_slot = self.history_write_pos as isize - count as isize;

        let mut sum_onset = 0.0f32;
        for (i, h) in history.iter_mut().enumerate() {
            let mut slot = start_slot + i as isize;
            if slot < 0 {
                slot += self.history_size as isize;
            }
            let val = self.onset_history[slot as usize];
            *h = val;
            sum_onset += val;
        }

        let mean = sum_onset / count as f32;
        for h in history.iter_mut() {
            *h -= mean;
        }

        let hop_rate = self.hop_rate;
        let lag_min = ((hop_rate * 60.0 / MAX_BPM) as usize).max(1);
        let mut lag_max = ((hop_rate * 60.0 / MIN_BPM) as usize + 1).min(count / 2 - 1);

        if lag_max <= lag_min {
            return 0.0;
        }
        if lag_max >= self.xcorr_result.len() {
            lag_max = self.xcorr_result.len() - 1;
        }

        for x in self.xcorr_result[..=lag_max].iter_mut() {
            *x = 0.0;
        }
        for lag in 0..=lag_max {
            let a = &history[..count - lag];
            let b = &history[lag..count];
            let cross = dot(a, b);
            let energy_a = dot(a, a);
            let energy_b = dot(b, b);
            let denom = (energy_a * energy_b).sqrt();
            self.xcorr_result[lag] = if denom > 1e-9 { cross / denom } else { 0.0 };
        }

        // 3-point smoothing.
        let mut smoothed = vec![0.0f32; lag_max + 1];
        smoothed[0] = self.xcorr_result[0];
        smoothed[lag_max] = self.xcorr_result[lag_max];
        #[allow(clippy::needless_range_loop)]
        for i in 1..lag_max {
            smoothed[i] =
                (self.xcorr_result[i - 1] + self.xcorr_result[i] + self.xcorr_result[i + 1]) / 3.0;
        }

        // Weight by the log-Gaussian tempo prior and take the strongest lag.
        let mut best_lag: isize = -1;
        let mut best_score = f32::NEG_INFINITY;
        #[allow(clippy::needless_range_loop)]
        for lag in lag_min..=lag_max {
            let bpm_at_lag = hop_rate * 60.0 / lag as f32;
            let log_ratio = (bpm_at_lag / PREFERRED_BPM).log2() / TEMPO_PRIOR_SIGMA;
            let weight = (-0.5 * log_ratio * log_ratio).exp();
            let score = smoothed[lag] * weight;
            if score > best_score {
                best_score = score;
                best_lag = lag as isize;
            }
        }

        if best_lag < 0 {
            return 0.0;
        }
        let best_lag = best_lag as usize;

        // Parabolic interpolation for sub-bin lag resolution.
        let mut peak_lag = best_lag as f32;
        if best_lag > lag_min && best_lag < lag_max {
            let y0 = smoothed[best_lag - 1];
            let y1 = smoothed[best_lag];
            let y2 = smoothed[best_lag + 1];
            let curvature = y0 - 2.0 * y1 + y2;
            if curvature.abs() > 1e-12 {
                let delta = 0.5 * (y0 - y2) / curvature;
                if delta > -1.0 && delta < 1.0 {
                    peak_lag = best_lag as f32 + delta;
                }
            }
        }

        if peak_lag < 1e-9 {
            return 0.0;
        }

        let bpm = hop_rate * 60.0 / peak_lag;
        bpm.clamp(MIN_BPM, MAX_BPM)
    }

    fn process_hop(&mut self) {
        let read_start = self.slide_pos.wrapping_sub(FFT_SIZE);
        for i in 0..FFT_SIZE {
            let slot = read_start.wrapping_add(i) & (FFT_SIZE - 1);
            let windowed = self.slide_buffer[slot] * self.window[i];
            self.fft_buffer[i] = Complex {
                re: windowed as f64,
                im: 0.0,
            };
        }

        self.fft.forward(&mut self.fft_buffer);

        let bins = FFT_SIZE / 2 + 1;
        let mut spectral_flux = 0.0f32;
        for k in 0..bins {
            let c = self.fft_buffer[k];
            let magnitude = (c.re * c.re + c.im * c.im) as f32;
            let diff = magnitude - self.prev_magnitudes[k];
            if diff > 0.0 {
                spectral_flux += diff;
            }
            self.prev_magnitudes[k] = magnitude;
        }

        self.onset_history[self.history_write_pos] = spectral_flux;
        self.history_write_pos += 1;
        if self.history_write_pos >= self.history_size {
            self.history_write_pos = 0;
        }
        self.history_count = (self.history_count + 1).min(self.history_size);
    }
}

#[inline]
fn dot(a: &[f32], b: &[f32]) -> f32 {
    a.iter().zip(b).map(|(x, y)| x * y).sum()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fft_matches_naive_dft() {
        let n = 512;
        let fft = Fft::new(n);
        let mut data = vec![Complex::default(); n];
        for (i, d) in data.iter_mut().enumerate() {
            // mix of two tones
            let t = i as f64;
            d.re = (2.0 * PI * 5.0 * t / n as f64).sin()
                + 0.5 * (2.0 * PI * 20.0 * t / n as f64).sin();
        }
        let reference = data.clone();
        fft.forward(&mut data);

        // Naive DFT of bin 5 should match.
        for &bin in &[5usize, 20usize] {
            let mut re = 0.0;
            let mut im = 0.0;
            for (i, c) in reference.iter().enumerate() {
                let ang = -2.0 * PI * bin as f64 * i as f64 / n as f64;
                re += c.re * ang.cos() - c.im * ang.sin();
                im += c.re * ang.sin() + c.im * ang.cos();
            }
            assert!((data[bin].re - re).abs() < 1e-6);
            assert!((data[bin].im - im).abs() < 1e-6);
        }
    }

    #[test]
    fn insufficient_data_returns_zero() {
        let mut bpm = BpmDetect::new(2, 44100);
        let block = vec![0.0f32; 1024 * 2];
        bpm.input_samples(&block, 1024);
        assert_eq!(bpm.get_bpm(), 0.0);
    }

    #[test]
    fn detects_120_bpm_click_track() {
        // Synthesise a 120 BPM click train at 44.1 kHz stereo: an impulse every
        // 0.5 s.  The detector should land near 120 BPM (allowing octave-free
        // tolerance from the perceptual prior).
        let sr = 44100usize;
        let mut bpm = BpmDetect::new(2, sr);
        let beat_period = sr / 2; // 0.5 s → 120 BPM
        let total = sr * 12; // 12 seconds
        let mut block = vec![0.0f32; 2];
        for n in 0..total {
            let v = if n % beat_period < 64 { 1.0 } else { 0.0 };
            block[0] = v;
            block[1] = v;
            bpm.input_samples(&block, 1);
        }
        let detected = bpm.get_bpm();
        assert!(detected > 0.0, "no tempo detected");
        // Accept the true tempo or a near octave; the prior favours 120.
        let near = (detected - 120.0).abs() < 12.0
            || (detected - 60.0).abs() < 8.0
            || (detected - 90.0).abs() < 8.0;
        assert!(near, "unexpected tempo {detected}");
    }
}
