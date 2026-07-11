//! Per-channel time-delay and phase-inversion alignment.
//!
//! Faithful Rust port of the reference C# `PhaseAlignment` used inside the
//! SmartMaster chain: three channels (L, R, Sub) each own a delay line (up to
//! 50 ms) and an optional polarity flip.  A channel with zero delay and no
//! inversion is a pass-through.  All delay lines are pre-allocated at
//! construction, so a delay change never allocates on the audio thread.

/// Number of aligned channels (L, R, Sub).
const CHANNELS: usize = 3;

/// Per-channel time-delay + phase-inversion alignment.
pub struct PhaseAlignment {
    sample_rate: f32,

    /// Requested delays in milliseconds, per channel.
    delays_ms: [f32; CHANNELS],
    /// Delay in samples, per channel (derived from `delays_ms`).
    delay_samples: [usize; CHANNELS],
    /// Polarity flip flags, per channel.
    invert: [bool; CHANNELS],

    /// Ring-buffer delay line, per channel (length = max 50 ms).
    delay_buffer: [Vec<f32>; CHANNELS],
    /// Write cursor into each delay line.
    write_index: [usize; CHANNELS],
}

impl PhaseAlignment {
    /// Creates a phase-alignment stage for the given sample rate. Each channel's
    /// delay line spans up to 50 ms.
    pub fn new(sample_rate: f32) -> Self {
        let sample_rate = if sample_rate > 0.0 {
            sample_rate
        } else {
            44_100.0
        };
        let max_delay = ((sample_rate * 0.05) as usize).max(1);
        Self {
            sample_rate,
            delays_ms: [0.0; CHANNELS],
            delay_samples: [0; CHANNELS],
            invert: [false; CHANNELS],
            delay_buffer: [
                vec![0.0; max_delay],
                vec![0.0; max_delay],
                vec![0.0; max_delay],
            ],
            write_index: [0; CHANNELS],
        }
    }

    /// Sets one channel's delay in milliseconds, recomputing its sample count and
    /// clearing that channel's delay line when the value actually changes
    /// (reference parity: a delay change resets the buffers).
    pub fn set_delay_ms(&mut self, channel: usize, delay_ms: f32) {
        if channel >= CHANNELS {
            return;
        }
        if (self.delays_ms[channel] - delay_ms).abs() > 0.01 {
            self.delays_ms[channel] = delay_ms;
            let max = self.delay_buffer[channel].len().saturating_sub(1);
            let samples = (delay_ms * self.sample_rate / 1000.0) as i64;
            self.delay_samples[channel] = samples.clamp(0, max as i64) as usize;
            self.reset_channel(channel);
        }
    }

    /// Sets one channel's polarity flip.
    pub fn set_invert(&mut self, channel: usize, invert: bool) {
        if channel < CHANNELS {
            self.invert[channel] = invert;
        }
    }

    /// Applies delay and inversion to one channel's `buffer` in place. A channel
    /// with zero delay and no inversion is left untouched.
    pub fn process_channel(&mut self, buffer: &mut [f32], channel: usize) {
        if channel >= CHANNELS {
            return;
        }

        let delay = self.delay_samples[channel];
        let invert = self.invert[channel];
        if delay == 0 && !invert {
            return;
        }

        let line = &mut self.delay_buffer[channel];
        let size = line.len();
        let mut write = self.write_index[channel];

        for sample in buffer.iter_mut() {
            line[write] = *sample;

            let read = if write >= delay {
                write - delay
            } else {
                write + size - delay
            };
            let mut out = line[read];
            if invert {
                out = -out;
            }
            *sample = out;

            write += 1;
            if write >= size {
                write = 0;
            }
        }

        self.write_index[channel] = write;
    }

    /// Clears every channel's delay line.
    pub fn reset(&mut self) {
        for ch in 0..CHANNELS {
            self.reset_channel(ch);
        }
    }

    fn reset_channel(&mut self, channel: usize) {
        self.delay_buffer[channel].iter_mut().for_each(|s| *s = 0.0);
        self.write_index[channel] = 0;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn zero_delay_no_invert_is_passthrough() {
        let mut p = PhaseAlignment::new(48_000.0);
        let input: Vec<f32> = (0..256).map(|i| (i as f32 * 0.01).sin()).collect();
        let mut buf = input.clone();
        p.process_channel(&mut buf, 0);
        assert_eq!(buf, input);
    }

    #[test]
    fn invert_flips_polarity() {
        let mut p = PhaseAlignment::new(48_000.0);
        p.set_invert(1, true);
        let input: Vec<f32> = (0..128).map(|i| (i as f32 * 0.02).sin()).collect();
        let mut buf = input.clone();
        p.process_channel(&mut buf, 1);
        for (a, b) in buf.iter().zip(input.iter()) {
            assert_eq!(*a, -*b);
        }
    }

    #[test]
    fn delay_shifts_signal() {
        let mut p = PhaseAlignment::new(48_000.0);
        // 1 ms at 48 kHz = 48 samples.
        p.set_delay_ms(0, 1.0);
        let mut buf = vec![0.0f32; 200];
        buf[0] = 1.0; // impulse
        p.process_channel(&mut buf, 0);
        assert_eq!(buf[0], 0.0, "impulse must be delayed away from sample 0");
        assert_eq!(buf[48], 1.0, "impulse should appear 48 samples later");
    }

    #[test]
    fn reset_clears_delay_line() {
        let mut p = PhaseAlignment::new(48_000.0);
        p.set_delay_ms(2, 2.0);
        let mut a = vec![0.5f32; 300];
        p.process_channel(&mut a, 2);
        p.reset();

        let mut b1 = vec![0.5f32; 300];
        p.process_channel(&mut b1, 2);
        p.reset();
        let mut b2 = vec![0.5f32; 300];
        p.process_channel(&mut b2, 2);
        assert_eq!(b1, b2);
    }
}
