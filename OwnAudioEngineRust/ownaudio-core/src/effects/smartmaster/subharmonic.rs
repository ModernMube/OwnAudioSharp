//! Subharmonic synthesizer — two octave-divider bands, added in parallel.
//!
//! The old version band-passed 40–120 Hz, ran it through a soft clipper and
//! *crossfaded* the result over the program (`dry·(1−mix) + shaped·mix`).  That
//! did two wrong things at once: a waveshaper makes harmonics, not
//! subharmonics, and at mix 0.4 the whole mix dropped 4.4 dB.
//!
//! What actually generates an octave below is a divider.  Each band isolates its
//! source octave, runs a Schmitt trigger whose edges toggle a flip-flop (that is
//! the divide-by-two), multiplies the resulting square by the source band's
//! envelope so it tracks dynamics, and band-passes it down to a near-sine in the
//! target band.  Both bands are summed and *added* to the dry signal.
//!
//! | band | source    | output   |
//! |------|-----------|----------|
//! | low  | 48–72 Hz  | 24–36 Hz |
//! | high | 72–112 Hz | 36–56 Hz |
//!
//! Generation runs on the mono sum — low bass is mono in practice, and the same
//! synthesized signal going to every channel keeps it phase-coherent.

use super::biquad::{Coeffs, State};

/// Level below which the divider is muted, so it doesn't chatter on noise.
const GATE: f32 = 1.0e-4;

/// Headroom scale on the synthesized sub at mix 1.0. The divided square's
/// fundamental is 4/π of its amplitude, so this lands it near the source level.
const SUB_TRIM: f32 = 0.55;

/// One octave-divider band: isolate the source octave, halve it, shape it.
struct DividerBand {
    src: Coeffs,
    out: Coeffs,
    src_state: [State; 2],
    out_state: [State; 2],

    env: f32,
    env_attack: f32,
    env_release: f32,

    /// Flip-flop output, ±1 — toggled once per source cycle.
    flip: f32,
    /// Schmitt trigger state, true while the source is above the upper edge.
    armed: bool,

    level: f32,
}

impl DividerBand {
    fn new(sample_rate: f32, src_lo: f32, src_hi: f32, out_lo: f32, out_hi: f32) -> Self {
        let src_centre = (src_lo * src_hi).sqrt();
        let out_centre = (out_lo * out_hi).sqrt();

        Self {
            src: Coeffs::bandpass(sample_rate, src_centre, src_centre / (src_hi - src_lo)),
            out: Coeffs::bandpass(sample_rate, out_centre, out_centre / (out_hi - out_lo)),
            src_state: [State::default(); 2],
            out_state: [State::default(); 2],
            env: 0.0,
            env_attack: time_coeff(8.0, sample_rate),
            env_release: time_coeff(120.0, sample_rate),
            flip: 1.0,
            armed: false,
            level: 1.0,
        }
    }

    #[inline]
    fn tick(&mut self, x: f32) -> f32 {
        let mut s = x;
        for st in self.src_state.iter_mut() {
            s = st.tick(&self.src, s);
        }

        let a = s.abs();
        let c = if a > self.env {
            self.env_attack
        } else {
            self.env_release
        };
        self.env = c * self.env + (1.0 - c) * a;

        if self.env < GATE {
            // Still run the output filters so their tail decays smoothly.
            let mut y = 0.0;
            for st in self.out_state.iter_mut() {
                y = st.tick(&self.out, y);
            }
            return y;
        }

        // Schmitt trigger scaled to the current level, so it works at any gain;
        // one toggle per full source cycle is the divide-by-two.
        let hyst = 0.25 * self.env;
        if self.armed {
            if s < -hyst {
                self.armed = false;
                self.flip = -self.flip;
            }
        } else if s > hyst {
            self.armed = true;
        }

        let mut y = self.flip * self.env;
        for st in self.out_state.iter_mut() {
            y = st.tick(&self.out, y);
        }
        y * self.level
    }

    fn reset(&mut self) {
        for st in self.src_state.iter_mut() {
            st.clear();
        }
        for st in self.out_state.iter_mut() {
            st.clear();
        }
        self.env = 0.0;
        self.flip = 1.0;
        self.armed = false;
    }
}

/// One-pole coefficient for a time constant in ms.
fn time_coeff(ms: f32, sample_rate: f32) -> f32 {
    (-1.0 / (ms * 0.001 * sample_rate)).exp()
}

/// dbx-style subharmonic synthesizer: two divider bands mixed in parallel with
/// the dry signal.
pub struct SubharmonicSynth {
    enabled: bool,
    mix: f32,
    low: DividerBand,
    high: DividerBand,
}

impl SubharmonicSynth {
    /// Off, at zero mix — the reference config defaults.
    pub fn new(sample_rate: f32) -> Self {
        Self {
            enabled: false,
            mix: 0.0,
            low: DividerBand::new(sample_rate, 48.0, 72.0, 24.0, 36.0),
            high: DividerBand::new(sample_rate, 72.0, 112.0, 36.0, 56.0),
        }
    }

    pub fn set_enabled(&mut self, enabled: bool) {
        self.enabled = enabled;
    }

    pub fn enabled(&self) -> bool {
        self.enabled
    }

    /// Master level of the synthesized sub, `0` off … `1` full.
    pub fn set_mix(&mut self, mix: f32) {
        self.mix = mix.clamp(0.0, 1.0);
    }

    pub fn mix(&self) -> f32 {
        self.mix
    }

    /// Level of the 24–36 Hz band.
    pub fn set_low_level(&mut self, level: f32) {
        self.low.level = level.clamp(0.0, 1.0);
    }

    pub fn low_level(&self) -> f32 {
        self.low.level
    }

    /// Level of the 36–56 Hz band.
    pub fn set_high_level(&mut self, level: f32) {
        self.high.level = level.clamp(0.0, 1.0);
    }

    pub fn high_level(&self) -> f32 {
        self.high.level
    }

    /// Adds the synthesized sub to the interleaved `buffer` in place.
    pub fn process(&mut self, buffer: &mut [f32], channels: u16) {
        if !self.enabled || self.mix <= 0.0 || channels == 0 {
            return;
        }
        let ch = channels as usize;
        let amount = self.mix * SUB_TRIM;
        let inv_ch = 1.0 / ch as f32;

        for frame in buffer.chunks_mut(ch) {
            let mono = frame.iter().sum::<f32>() * inv_ch;
            let sub = (self.low.tick(mono) + self.high.tick(mono)) * amount;

            for s in frame.iter_mut() {
                *s = (*s + sub).clamp(-1.5, 1.5);
            }
        }
    }

    pub fn reset(&mut self) {
        self.low.reset();
        self.high.reset();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const SR: f32 = 48_000.0;

    fn tone(freq: f32, amp: f32, frames: usize) -> Vec<f32> {
        (0..frames)
            .map(|i| amp * (2.0 * std::f32::consts::PI * freq * i as f32 / SR).sin())
            .collect()
    }

    /// Goertzel magnitude at `freq`, skipping the settling transient.
    fn magnitude_at(signal: &[f32], freq: f32) -> f32 {
        let skip = signal.len() / 2;
        let s = &signal[skip..];
        let w = 2.0 * std::f32::consts::PI * freq / SR;
        let (mut re, mut im) = (0.0f64, 0.0f64);
        for (i, &x) in s.iter().enumerate() {
            re += (x as f64) * (w as f64 * i as f64).cos();
            im += (x as f64) * (w as f64 * i as f64).sin();
        }
        (2.0 * (re * re + im * im).sqrt() / s.len() as f64) as f32
    }

    #[test]
    fn disabled_is_passthrough() {
        let mut s = SubharmonicSynth::new(SR);
        s.set_mix(1.0);
        let input = tone(60.0, 0.5, 512);
        let mut buf = input.clone();
        s.process(&mut buf, 1);
        assert_eq!(buf, input);
    }

    #[test]
    fn zero_mix_is_passthrough() {
        let mut s = SubharmonicSynth::new(SR);
        s.set_enabled(true);
        let input = tone(60.0, 0.5, 512);
        let mut buf = input.clone();
        s.process(&mut buf, 1);
        assert_eq!(buf, input);
    }

    #[test]
    fn generates_an_octave_below_the_source() {
        let mut s = SubharmonicSynth::new(SR);
        s.set_enabled(true);
        s.set_mix(1.0);

        // 60 Hz sits in the low band's source octave, so we expect 30 Hz out.
        let mut buf = tone(60.0, 0.5, 96_000);
        s.process(&mut buf, 1);

        let sub = magnitude_at(&buf, 30.0);
        assert!(sub > 0.05, "no octave-down content, got {sub}");
    }

    #[test]
    fn dry_signal_is_kept_not_faded() {
        let mut s = SubharmonicSynth::new(SR);
        s.set_enabled(true);
        s.set_mix(1.0);

        // 800 Hz is far outside both bands: it must come through untouched.
        let mut buf = tone(800.0, 0.5, 48_000);
        s.process(&mut buf, 1);

        let dry = magnitude_at(&buf, 800.0);
        assert!(dry > 0.45, "dry signal was attenuated, got {dry}");
    }

    #[test]
    fn silence_stays_silent() {
        let mut s = SubharmonicSynth::new(SR);
        s.set_enabled(true);
        s.set_mix(1.0);
        let mut buf = vec![0.0f32; 24_000];
        s.process(&mut buf, 1);
        assert!(buf.iter().all(|&x| x.abs() < 1.0e-6));
    }

    #[test]
    fn reset_restores_reproducibility() {
        let mut s = SubharmonicSynth::new(SR);
        s.set_enabled(true);
        s.set_mix(0.8);
        let input = tone(70.0, 0.5, 4_096);

        let mut a = input.clone();
        s.process(&mut a, 1);
        s.reset();
        let mut b = input.clone();
        s.process(&mut b, 1);
        assert_eq!(a, b);
    }
}
