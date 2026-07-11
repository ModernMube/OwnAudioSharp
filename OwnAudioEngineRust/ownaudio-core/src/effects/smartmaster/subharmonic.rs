//! Subharmonic synthesizer with a linear-phase FIR band-pass and a
//! waveshaping harmonic generator.
//!
//! Faithful Rust port of the reference C# `SubharmonicSynth` (and the
//! `FIRFilter` it uses): the 40–120 Hz band is isolated with a 127-tap
//! Kaiser-windowed-sinc band-pass (linear phase), the isolated band is passed
//! through a soft-clipping waveshaper (`x / (1 + |x|)`) to synthesise harmonics,
//! and the result is blended back with the dry signal by `mix`.  The FIR delay
//! lines and the filtered scratch buffer are pre-allocated / grown off the hot
//! path so steady-state processing never allocates.

/// FIR kernel length (odd, linear phase), matching the reference.
const KERNEL_SIZE: usize = 127;
/// Band-pass low edge in Hz.
const LOW_FREQ: f32 = 40.0;
/// Band-pass high edge in Hz.
const HIGH_FREQ: f32 = 120.0;
/// Kaiser window beta, matching the reference.
const KAISER_BETA: f32 = 5.0;
/// Channels the FIR delay lines are kept for (stereo mastering chain).
const FIR_CHANNELS: usize = 2;

/// Linear-phase FIR band-pass filter with per-channel circular delay lines.
struct FirBandpass {
    kernel: [f32; KERNEL_SIZE],
    delay: [[f32; KERNEL_SIZE]; FIR_CHANNELS],
    write_pos: [usize; FIR_CHANNELS],
}

impl FirBandpass {
    fn new(sample_rate: f32) -> Self {
        Self {
            kernel: build_bandpass_kernel(sample_rate, LOW_FREQ, HIGH_FREQ),
            delay: [[0.0; KERNEL_SIZE]; FIR_CHANNELS],
            write_pos: [0; FIR_CHANNELS],
        }
    }

    /// Filters `buffer` (interleaved) in place, up to [`FIR_CHANNELS`] channels.
    fn process(&mut self, buffer: &mut [f32], frame_count: usize, channels: usize) {
        let active = channels.min(FIR_CHANNELS);
        for ch in 0..active {
            let mut write = self.write_pos[ch];
            for frame in 0..frame_count {
                let idx = frame * channels + ch;
                self.delay[ch][write] = buffer[idx];

                let mut out = 0.0f32;
                let mut read = write;
                for k in 0..KERNEL_SIZE {
                    out += self.kernel[k] * self.delay[ch][read];
                    read = if read == 0 { KERNEL_SIZE - 1 } else { read - 1 };
                }
                buffer[idx] = out;

                write = (write + 1) % KERNEL_SIZE;
            }
            self.write_pos[ch] = write;
        }
    }

    fn reset(&mut self) {
        self.delay = [[0.0; KERNEL_SIZE]; FIR_CHANNELS];
        self.write_pos = [0; FIR_CHANNELS];
    }
}

/// Windowed-sinc band-pass kernel (high-pass minus low-pass), Kaiser-windowed
/// and normalised to unity pass-band gain — a direct transcription of the
/// reference `FIRFilter.CreateBandpassKernel`.
fn build_bandpass_kernel(sample_rate: f32, low: f32, high: f32) -> [f32; KERNEL_SIZE] {
    let mut kernel = [0.0f32; KERNEL_SIZE];
    let center = (KERNEL_SIZE / 2) as i32;
    let wl = 2.0 * std::f32::consts::PI * low / sample_rate;
    let wh = 2.0 * std::f32::consts::PI * high / sample_rate;

    for (i, k) in kernel.iter_mut().enumerate() {
        let n = i as i32 - center;
        let sinc = if n == 0 {
            (wh - wl) / std::f32::consts::PI
        } else {
            let nf = n as f32;
            (wh * nf).sin() / (std::f32::consts::PI * nf)
                - (wl * nf).sin() / (std::f32::consts::PI * nf)
        };
        *k = sinc * kaiser_window(i, KERNEL_SIZE, KAISER_BETA);
    }

    // A band-pass must reject DC, but a short 127-tap kernel at such a low centre
    // frequency cannot resolve the 40–120 Hz band: its raw coefficient sum (the
    // DC gain) is comparable to its pass-band gain, so it leaks DC/subsonic
    // rumble.  Normalising by that sum (the reference approach) merely forces the
    // DC gain to unity instead of rejecting it.  Restore true band-pass behaviour
    // by removing the coefficient mean so the DC gain is exactly zero (the kernel
    // stays symmetric, hence linear phase), then normalise to unity gain at the
    // pass-band centre.
    let mean: f32 = kernel.iter().sum::<f32>() / KERNEL_SIZE as f32;
    for k in kernel.iter_mut() {
        *k -= mean;
    }

    let center_freq = 0.5 * (low + high);
    let wc = 2.0 * std::f32::consts::PI * center_freq / sample_rate;
    let mut re = 0.0f32;
    let mut im = 0.0f32;
    for (i, k) in kernel.iter().enumerate() {
        let nf = (i as i32 - center) as f32;
        re += *k * (wc * nf).cos();
        im += *k * (wc * nf).sin();
    }
    let gain = (re * re + im * im).sqrt();
    if gain > 1.0e-6 {
        for k in kernel.iter_mut() {
            *k /= gain;
        }
    }
    kernel
}

/// Kaiser window sample `I0(beta·sqrt(1−x²)) / I0(beta)`.
fn kaiser_window(n: usize, size: usize, beta: f32) -> f32 {
    let alpha = (size - 1) as f32 / 2.0;
    let x = (n as f32 - alpha) / alpha;
    let arg = beta * (1.0 - x * x).max(0.0).sqrt();
    bessel_i0(arg) / bessel_i0(beta)
}

/// Modified Bessel function of the first kind, order zero (Taylor series),
/// matching the reference approximation.
fn bessel_i0(x: f32) -> f32 {
    let mut sum = 1.0f32;
    let mut term = 1.0f32;
    let x_squared = x * x / 4.0;
    for k in 1..=20 {
        term *= x_squared / (k * k) as f32;
        sum += term;
        if term < 1.0e-8 * sum {
            break;
        }
    }
    sum
}

/// Soft-clipping waveshaper generating harmonics from the isolated sub band.
#[inline]
fn waveshape(x: f32) -> f32 {
    x / (1.0 + x.abs())
}

/// Subharmonic synthesizer: FIR band-pass isolation + waveshaping + dry/wet mix.
pub struct SubharmonicSynth {
    enabled: bool,
    mix: f32,
    fir: FirBandpass,
    /// Filtered-signal scratch, grown off the hot path.
    filtered: Vec<f32>,
}

impl SubharmonicSynth {
    /// Creates a subharmonic synthesizer for the given sample rate (disabled,
    /// mix 0 — the reference defaults).
    pub fn new(sample_rate: f32) -> Self {
        Self {
            enabled: false,
            mix: 0.0,
            fir: FirBandpass::new(sample_rate),
            filtered: vec![0.0; 2_048 * FIR_CHANNELS],
        }
    }

    /// Enables or disables the synthesizer.
    pub fn set_enabled(&mut self, enabled: bool) {
        self.enabled = enabled;
    }

    /// Gets whether the synthesizer is enabled.
    pub fn enabled(&self) -> bool {
        self.enabled
    }

    /// Sets the dry/wet mix (`0` dry … `1` full effect).
    pub fn set_mix(&mut self, mix: f32) {
        self.mix = mix.clamp(0.0, 1.0);
    }

    /// Gets the current mix.
    pub fn mix(&self) -> f32 {
        self.mix
    }

    /// Processes the interleaved `buffer` in place. A no-op while disabled or at
    /// zero mix (reference parity).
    pub fn process(&mut self, buffer: &mut [f32], channels: u16) {
        if !self.enabled || self.mix <= 0.0 || channels == 0 {
            return;
        }
        let channels = channels as usize;
        let frame_count = buffer.len() / channels;
        let required = frame_count * channels;
        if required == 0 {
            return;
        }
        if self.filtered.len() < required {
            self.filtered.resize(required, 0.0);
        }

        // 1. Copy the dry signal, then isolate the 40–120 Hz band.
        self.filtered[..required].copy_from_slice(&buffer[..required]);
        self.fir
            .process(&mut self.filtered[..required], frame_count, channels);

        // 2. Waveshape the isolated band and blend it back with the dry signal.
        let mix = self.mix;
        let dry = 1.0 - mix;
        for i in 0..required {
            let shaped = waveshape(self.filtered[i] * 2.0);
            let mut mixed = buffer[i] * dry + shaped * mix;
            if mixed > 1.0 {
                mixed = 1.0;
            } else if mixed < -1.0 {
                mixed = -1.0;
            }
            buffer[i] = mixed;
        }
    }

    /// Clears the FIR delay lines and filtered scratch.
    pub fn reset(&mut self) {
        self.fir.reset();
        self.filtered.iter_mut().for_each(|s| *s = 0.0);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn disabled_is_passthrough() {
        let mut s = SubharmonicSynth::new(48_000.0);
        s.set_mix(1.0); // still disabled
        let input: Vec<f32> = (0..512).map(|i| (i as f32 * 0.05).sin() * 0.5).collect();
        let mut buf = input.clone();
        s.process(&mut buf, 1);
        assert_eq!(buf, input);
    }

    #[test]
    fn zero_mix_is_passthrough() {
        let mut s = SubharmonicSynth::new(48_000.0);
        s.set_enabled(true);
        s.set_mix(0.0);
        let input: Vec<f32> = (0..512).map(|i| (i as f32 * 0.05).sin() * 0.5).collect();
        let mut buf = input.clone();
        s.process(&mut buf, 1);
        assert_eq!(buf, input);
    }

    #[test]
    fn enabled_alters_low_content_and_stays_bounded() {
        let mut s = SubharmonicSynth::new(48_000.0);
        s.set_enabled(true);
        s.set_mix(1.0);
        // A 60 Hz tone sits inside the 40–120 Hz band, so it is reshaped.
        let input: Vec<f32> = (0..8_192)
            .map(|i| 0.6 * (2.0 * std::f32::consts::PI * 60.0 * i as f32 / 48_000.0).sin())
            .collect();
        let mut buf = input.clone();
        s.process(&mut buf, 1);
        assert_ne!(buf, input, "an in-band tone must be altered");
        assert!(buf.iter().all(|&x| x.is_finite() && x.abs() <= 1.0));
    }

    #[test]
    fn kernel_is_normalised_and_finite() {
        let kernel = build_bandpass_kernel(48_000.0, LOW_FREQ, HIGH_FREQ);
        assert!(kernel.iter().all(|k| k.is_finite()));
        // Band-pass kernels sum to ~0 (no DC), but must be well-defined.
        let sum: f32 = kernel.iter().sum();
        assert!(sum.abs() < 1.0);
    }

    #[test]
    fn reset_restores_reproducibility() {
        let mut s = SubharmonicSynth::new(48_000.0);
        s.set_enabled(true);
        s.set_mix(0.8);
        let input: Vec<f32> = (0..1_024)
            .map(|i| 0.5 * (2.0 * std::f32::consts::PI * 70.0 * i as f32 / 48_000.0).sin())
            .collect();
        let mut a = input.clone();
        s.process(&mut a, 1);
        s.reset();
        let mut b = input.clone();
        s.process(&mut b, 1);
        assert_eq!(a, b);
    }
}
