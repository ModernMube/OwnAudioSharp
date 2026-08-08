//! Subsonic high-pass — 4th-order Butterworth, 24 dB/oct.
//!
//! Everything below the cabinets' usable range is wasted excursion: it eats
//! amplifier headroom, muddies the low mids and makes the limiter work for
//! nothing.  Every PA processor opens with one; this chain had none at all.

use super::biquad::{Coeffs, State};

/// Butterworth pole Qs for a 4th-order cascade.
const STAGE_Q: [f32; 2] = [0.541_196, 1.306_563];

const PREALLOC_CHANNELS: usize = 2;

/// Cascaded high-pass with per-channel state.
pub struct Subsonic {
    sample_rate: f32,
    frequency: f32,
    enabled: bool,
    coeffs: [Coeffs; 2],
    /// Flattened `channel * 2 + stage`.
    state: Vec<State>,
    channels: usize,
}

impl Subsonic {
    pub fn new(sample_rate: f32, frequency: f32) -> Self {
        let mut s = Self {
            sample_rate,
            frequency: frequency.max(1.0),
            enabled: false,
            coeffs: [Coeffs::IDENTITY; 2],
            state: vec![State::default(); PREALLOC_CHANNELS * 2],
            channels: PREALLOC_CHANNELS,
        };
        s.recompute();
        s
    }

    pub fn set_enabled(&mut self, enabled: bool) {
        self.enabled = enabled;
    }

    pub fn enabled(&self) -> bool {
        self.enabled
    }

    pub fn set_frequency(&mut self, frequency: f32) {
        let frequency = frequency.clamp(10.0, 300.0);
        if (self.frequency - frequency).abs() > 0.01 {
            self.frequency = frequency;
            self.recompute();
            self.reset();
        }
    }

    pub fn frequency(&self) -> f32 {
        self.frequency
    }

    fn recompute(&mut self) {
        for (c, q) in self.coeffs.iter_mut().zip(STAGE_Q) {
            *c = Coeffs::highpass(self.sample_rate, self.frequency, q);
        }
    }

    pub fn process(&mut self, buffer: &mut [f32], channels: u16) {
        if !self.enabled || channels == 0 {
            return;
        }
        let ch = channels as usize;
        if ch > self.channels {
            self.state.resize(ch * 2, State::default());
            self.channels = ch;
        }

        for frame in buffer.chunks_mut(ch) {
            for (c, sample) in frame.iter_mut().enumerate() {
                let mut x = *sample;
                for stage in 0..2 {
                    x = self.state[c * 2 + stage].tick(&self.coeffs[stage], x);
                }
                *sample = x;
            }
        }
    }

    pub fn reset(&mut self) {
        for s in self.state.iter_mut() {
            s.clear();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const SR: f32 = 48_000.0;

    fn level_at(f: &mut Subsonic, freq: f32) -> f32 {
        let n = 48_000;
        let mut buf: Vec<f32> = (0..n)
            .map(|i| (2.0 * std::f32::consts::PI * freq * i as f32 / SR).sin())
            .collect();
        f.process(&mut buf, 1);
        let tail = &buf[n / 2..];
        let rms = (tail.iter().map(|x| x * x).sum::<f32>() / tail.len() as f32).sqrt();
        20.0 * (rms * std::f32::consts::SQRT_2).log10()
    }

    #[test]
    fn disabled_is_passthrough() {
        let mut s = Subsonic::new(SR, 40.0);
        let input: Vec<f32> = (0..512).map(|i| (i as f32 * 0.02).sin()).collect();
        let mut buf = input.clone();
        s.process(&mut buf, 1);
        assert_eq!(buf, input);
    }

    #[test]
    fn rolls_off_at_24_db_per_octave() {
        let mut s = Subsonic::new(SR, 40.0);
        s.set_enabled(true);

        let passband = level_at(&mut s, 400.0);
        s.reset();
        let octave_down = level_at(&mut s, 20.0);
        s.reset();
        let two_octaves = level_at(&mut s, 10.0);

        assert!(passband.abs() < 0.3, "passband {passband} dB");
        assert!(
            (octave_down + 24.0).abs() < 3.0,
            "one octave below: {octave_down} dB"
        );
        assert!(two_octaves < octave_down - 20.0, "slope flattened out");
    }
}
