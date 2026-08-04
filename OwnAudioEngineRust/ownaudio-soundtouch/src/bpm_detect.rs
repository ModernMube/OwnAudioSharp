//! BPM detector.
//!
//! Spectral-flux onset detection followed by normalised-autocorrelation tempo
//! estimation with a log-Gaussian perceptual prior.  To keep this crate
//! dependency-free the FFT is implemented here as a small in-place radix-2
//! Cooley-Tukey routine (the window size is always 512, a power of two).
//!
//! Two things in here are less obvious than they look:
//!
//! * The autocorrelation of every analysis window is *accumulated* over the whole
//!   stream instead of only looking at the last window.  Songs tend to end in a
//!   fade, a reverb tail or plain silence, so an estimate taken from the newest
//!   few seconds says more about the outro than about the track.
//! * The prior is symmetric in log-tempo, which means it flips in favour of the
//!   half tempo above `PREFERRED_BPM * sqrt(2)` — see [`PREFERRED_BPM`].
//!
//! All processing buffers — including the FFT twiddle table — are allocated at
//! construction; neither [`BpmDetect::input_samples`] nor [`BpmDetect::get_bpm`]
//! touches the heap.

use std::f64::consts::PI;

const FFT_SIZE: usize = 512;
const HOP_SIZE: usize = 64;
const TARGET_SAMPLE_RATE: usize = 11025;
const MIN_BPM: f32 = 45.0;
const MAX_BPM: f32 = 190.0;

/// Centre of the tempo prior.  Since the prior is symmetric in log-tempo, the point
/// where a candidate and its half score the same weight is `PREFERRED_BPM * sqrt(2)`
/// regardless of the sigma — above that the half tempo wins on weight alone, and a
/// periodic onset train correlates just as well at twice its beat period.  Keep this
/// high enough that the crossover (here 198 BPM) stays clear of [`MAX_BPM`].
const PREFERRED_BPM: f32 = 140.0;
const TEMPO_PRIOR_SIGMA: f32 = 0.9;

/// Length of one autocorrelation window, and the shortest input worth answering for.
const WINDOW_SECONDS: f32 = 12.0;
const MIN_ANALYSIS_SECONDS: f32 = 4.0;

/// Below this peak correlation there is no tempo to speak of (silence, noise, a
/// single sustained chord) and [`BpmDetect::get_bpm`] reports 0 instead of guessing.
const MIN_PEAK_CORRELATION: f32 = 0.25;

/// How strong the twice-as-fast reading has to be, relative to the winning lag, to be
/// taken instead.  Patterns accented on every other beat (kick 1 and 3, softer snare
/// on 2 and 4) correlate best at two beats, which otherwise reads as half tempo.
const OCTAVE_PREFERENCE: f32 = 0.85;

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
    /// Ring of onset strengths, one per hop, `window_size` long.
    onset_window: Vec<f32>,
    /// Mean-removed copy of the ring, laid out oldest to newest.
    detrended: Vec<f32>,
    /// Autocorrelation summed over every window folded in so far.
    xcorr_sum: Vec<f32>,
    xcorr: Vec<f32>,
    smoothed: Vec<f32>,

    channels: usize,
    window_size: usize,
    decimate_by: usize,
    hop_rate: f32,
    lag_min: usize,
    lag_max: usize,
    min_frames: usize,

    slide_pos: usize,
    hop_accum: usize,
    onset_write_pos: usize,
    onset_count: usize,
    hops_since_fold: usize,
    windows_folded: u32,
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

        let lag_min = ((hop_rate * 60.0 / MAX_BPM) as usize).max(1);
        let lag_max = (hop_rate * 60.0 / MIN_BPM) as usize + 1;
        // A window shorter than twice the slowest lag cannot show that lag at all, so both
        // the window and the "enough data" threshold are floored there.
        let floor = 2 * lag_max + 4;
        let window_size = ((WINDOW_SECONDS * hop_rate) as usize).max(floor);
        let min_frames = ((MIN_ANALYSIS_SECONDS * hop_rate) as usize).max(floor);

        let mut window = vec![0.0f32; FFT_SIZE];
        for (i, w) in window.iter_mut().enumerate() {
            *w = 0.54
                - 0.46 * (2.0 * std::f32::consts::PI * i as f32 / (FFT_SIZE as f32 - 1.0)).cos();
        }

        BpmDetect {
            fft: Fft::new(FFT_SIZE),
            fft_buffer: vec![Complex::default(); FFT_SIZE],
            prev_magnitudes: vec![0.0; FFT_SIZE / 2 + 1],
            window,
            slide_buffer: vec![0.0; FFT_SIZE],
            onset_window: vec![0.0; window_size],
            detrended: vec![0.0; window_size],
            xcorr_sum: vec![0.0; lag_max + 1],
            xcorr: vec![0.0; lag_max + 1],
            smoothed: vec![0.0; lag_max + 1],
            channels,
            window_size,
            decimate_by,
            hop_rate,
            lag_min,
            lag_max,
            min_frames,
            slide_pos: 0,
            hop_accum: 0,
            onset_write_pos: 0,
            onset_count: 0,
            hops_since_fold: 0,
            windows_folded: 0,
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

    /// Estimated tempo over everything fed in so far, or `0.0` when there is either not
    /// enough audio (under [`MIN_ANALYSIS_SECONDS`]) or no periodicity worth reporting.
    ///
    /// Fair warning: sustained material with no percussive onsets at all — a held chord,
    /// a solo pad — has no tempo to find, and what comes back for it is the strongest
    /// ripple in the flux, not a beat.
    pub fn get_bpm(&mut self) -> f32 {
        let count = self.onset_count;
        if count < self.min_frames {
            return 0.0;
        }

        // Newest (possibly partial) window plus every window folded in along the way.
        self.detrend(count);
        let weight = self.windows_folded as f32 + 1.0;
        let lag_max = self.lag_max;
        {
            let src = &self.detrended[..count];
            for (lag, out) in self.xcorr[..=lag_max].iter_mut().enumerate() {
                *out = (correlate(src, lag) + self.xcorr_sum[lag]) / weight;
            }
        }

        self.smoothed[0] = self.xcorr[0];
        self.smoothed[lag_max] = self.xcorr[lag_max];
        for i in 1..lag_max {
            self.smoothed[i] = (self.xcorr[i - 1] + self.xcorr[i] + self.xcorr[i + 1]) / 3.0;
        }

        let mut best_lag = 0usize;
        let mut best_score = f32::NEG_INFINITY;
        for lag in self.lag_min..=lag_max {
            let bpm_at_lag = self.hop_rate * 60.0 / lag as f32;
            let log_ratio = (bpm_at_lag / PREFERRED_BPM).log2() / TEMPO_PRIOR_SIGMA;
            let score = self.smoothed[lag] * (-0.5 * log_ratio * log_ratio).exp();
            if score > best_score {
                best_score = score;
                best_lag = lag;
            }
        }
        if best_lag == 0 {
            return 0.0;
        }

        let faster = best_lag / 2;
        if faster >= self.lag_min
            && self.smoothed[faster] >= OCTAVE_PREFERENCE * self.smoothed[best_lag]
        {
            best_lag = faster;
        }

        if self.smoothed[best_lag] < MIN_PEAK_CORRELATION {
            return 0.0;
        }

        // Parabolic interpolation for sub-bin lag resolution.
        let mut peak_lag = best_lag as f32;
        if best_lag > self.lag_min && best_lag < lag_max {
            let y0 = self.smoothed[best_lag - 1];
            let y1 = self.smoothed[best_lag];
            let y2 = self.smoothed[best_lag + 1];
            let curvature = y0 - 2.0 * y1 + y2;
            if curvature.abs() > 1e-12 {
                let delta = 0.5 * (y0 - y2) / curvature;
                if delta > -1.0 && delta < 1.0 {
                    peak_lag += delta;
                }
            }
        }

        (self.hop_rate * 60.0 / peak_lag).clamp(MIN_BPM, MAX_BPM)
    }

    /// Copies the newest `count` onsets out of the ring, oldest first, mean removed.
    fn detrend(&mut self, count: usize) {
        let start = self.onset_write_pos + self.window_size - count;
        let mut sum = 0.0f32;
        for i in 0..count {
            let value = self.onset_window[(start + i) % self.window_size];
            self.detrended[i] = value;
            sum += value;
        }

        let mean = sum / count as f32;
        for value in self.detrended[..count].iter_mut() {
            *value -= mean;
        }
    }

    /// Folds the current window's autocorrelation into the running sum, so that the whole
    /// stream gets a say in the estimate and not just whatever was played last.
    fn fold_window(&mut self) {
        let count = self.window_size;
        self.detrend(count);

        let src = &self.detrended[..count];
        for (lag, acc) in self.xcorr_sum[..=self.lag_max].iter_mut().enumerate() {
            *acc += correlate(src, lag);
        }
        self.windows_folded += 1;
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
            // magnitude, not power: squaring gives an accented beat several times the weight
            // of the beats around it and the autocorrelation locks onto the accent period
            let magnitude = (c.re * c.re + c.im * c.im).sqrt() as f32;
            let diff = magnitude - self.prev_magnitudes[k];
            if diff > 0.0 {
                spectral_flux += diff;
            }
            self.prev_magnitudes[k] = magnitude;
        }

        self.onset_window[self.onset_write_pos] = spectral_flux;
        self.onset_write_pos = (self.onset_write_pos + 1) % self.window_size;
        self.onset_count = (self.onset_count + 1).min(self.window_size);
        self.hops_since_fold += 1;

        // 50% overlap between folded windows
        if self.onset_count == self.window_size && self.hops_since_fold >= self.window_size / 2 {
            self.fold_window();
            self.hops_since_fold = 0;
        }
    }
}

/// Normalised autocorrelation of `data` at `lag`, in `-1..1`.
#[inline]
fn correlate(data: &[f32], lag: usize) -> f32 {
    let a = &data[..data.len() - lag];
    let b = &data[lag..];
    let denom = (dot(a, a) * dot(b, b)).sqrt();
    if denom > 1e-9 {
        dot(a, b) / denom
    } else {
        0.0
    }
}

#[inline]
fn dot(a: &[f32], b: &[f32]) -> f32 {
    a.iter().zip(b).map(|(x, y)| x * y).sum()
}

#[cfg(test)]
mod tests {
    use super::*;

    const SR: usize = 22050;

    /// Click train at `bpm`, mono, `secs` long.
    fn click_track(bpm: f32, secs: f32) -> Vec<f32> {
        let period = 60.0 / bpm * SR as f32;
        (0..(SR as f32 * secs) as usize)
            .map(|i| if (i as f32 % period) < 64.0 { 1.0 } else { 0.0 })
            .collect()
    }

    fn detect(mono: &[f32]) -> f32 {
        let mut bpm = BpmDetect::new(1, SR);
        for chunk in mono.chunks(4096) {
            bpm.input_samples(chunk, chunk.len());
        }
        bpm.get_bpm()
    }

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
        let detected = detect(&click_track(120.0, 20.0));
        assert!(
            (detected - 120.0).abs() < 3.0,
            "expected 120, got {detected}"
        );
    }

    /// The prior used to hand everything above 170 BPM over to its half tempo.
    #[test]
    fn fast_tempo_is_not_halved() {
        for truth in [174.0f32, 190.0] {
            let detected = detect(&click_track(truth, 20.0));
            assert!(
                (detected - truth).abs() < 4.0,
                "expected {truth}, got {detected}"
            );
        }
    }

    /// A quiet outro must not decide the tempo of the whole track.
    #[test]
    fn silent_ending_does_not_hijack_the_estimate() {
        let mut samples = click_track(150.0, 40.0);
        samples.extend(std::iter::repeat_n(0.0, SR * 10));

        let detected = detect(&samples);
        assert!(
            (detected - 150.0).abs() < 4.0,
            "expected 150, got {detected}"
        );
    }

    #[test]
    fn no_tempo_in_silence() {
        assert_eq!(detect(&vec![0.0f32; SR * 20]), 0.0);
    }
}
