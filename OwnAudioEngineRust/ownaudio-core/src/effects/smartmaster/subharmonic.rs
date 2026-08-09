//! Subharmonic synthesizer — two octave-divider bands, added in parallel.
//!
//! Each band isolates its source octave, halves it with a Schmitt trigger
//! driving a flip-flop (that is the divide-by-two), multiplies the resulting
//! square by the band's envelope so it tracks dynamics, and filters it down to
//! the target band. Both bands are summed and *added* to the dry signal.
//!
//! | band | source    | output   |
//! |------|-----------|----------|
//! | low  | 48–72 Hz  | 24–36 Hz |
//! | high | 72–112 Hz | 36–56 Hz |
//!
//! Two earlier attempts are worth recording, because both failed in ways that
//! are easy to reintroduce:
//!
//! * The first band-passed 40–120 Hz through a soft clipper and *crossfaded* the
//!   result over the program. A waveshaper makes harmonics, not subharmonics,
//!   and the crossfade dropped the whole mix by several dB.
//! * The second was this divider without a retrigger lockout, and with a narrow
//!   resonant band-pass on the output. Missing the lockout let a ripply
//!   waveform toggle the flip-flop twice inside one cycle, so the divider slid
//!   into period-3 or period-4 and dropped inharmonic tones at f/3, 2f/3, f/4
//!   under the bass (measured at 8–24 % of the sub's own level). The narrow
//!   output filter then swung the sub level by ~37 dB depending on where the
//!   note fell inside the band.
//!
//! The fix is to keep the divider — it needs no pitch estimate, and each band's
//! output filter bounds where a mis-trigger can land — and address the two
//! failures directly: a retrigger lockout on the Schmitt trigger, and a flat
//! high-pass / low-pass pair on the output instead of a resonant peak.
//!
//! Generation runs on the mono sum — low bass is mono in practice, and the same
//! synthesized signal going to every channel keeps it phase-coherent.

use super::biquad::{Coeffs, State};

/// Level below which the divider is muted, so it doesn't chatter on noise.
const GATE: f32 = 1.0e-4;

/// Headroom scale on the synthesized sub at mix 1.0. A square's fundamental is
/// 4/π of its amplitude and the output filters are flat in the passband, so this
/// lands the sub around half the source band's level.
const SUB_TRIM: f32 = 0.4;

/// One octave-divider band: isolate the source octave, halve it, shape it.
struct DividerBand {
    src: Coeffs,
    src_state: [State; 2],

    /// Flat high-pass / low-pass pair bounding the output to the target band.
    /// A resonant band-pass here is what used to make the level note-dependent.
    out_hp: Coeffs,
    out_lp: Coeffs,
    out_hp_state: State,
    out_lp_state: State,

    env: f32,
    env_attack: f32,
    env_release: f32,

    /// Flip-flop output, ±1 — toggled once per source cycle.
    flip: f32,
    /// Schmitt trigger state, true while the source is above the upper edge.
    armed: bool,
    /// Samples since the last toggle, and the minimum that must pass before the
    /// next one. Without this the divider slips to period-3 / period-4.
    since_flip: u32,
    lockout: u32,

    level: f32,
}

impl DividerBand {
    fn new(sample_rate: f32, src_lo: f32, src_hi: f32, out_lo: f32, out_hi: f32) -> Self {
        let src_centre = (src_lo * src_hi).sqrt();

        Self {
            src: Coeffs::bandpass(sample_rate, src_centre, src_centre / (src_hi - src_lo)),
            src_state: [State::default(); 2],
            out_hp: Coeffs::highpass(sample_rate, out_lo, 0.707),
            out_lp: Coeffs::lowpass(sample_rate, out_hi, 0.707),
            out_hp_state: State::default(),
            out_lp_state: State::default(),
            env: 0.0,
            env_attack: time_coeff(8.0, sample_rate),
            env_release: time_coeff(120.0, sample_rate),
            flip: 1.0,
            armed: false,
            since_flip: 0,
            // Three quarters of the shortest cycle the band can carry: long
            // enough to swallow ripple, short enough to never block a real edge.
            lockout: (0.75 * sample_rate / src_hi) as u32,
            level: 1.0,
        }
    }

    #[inline]
    fn shape(&mut self, x: f32) -> f32 {
        let y = self.out_hp_state.tick(&self.out_hp, x);
        self.out_lp_state.tick(&self.out_lp, y)
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

        self.since_flip = self.since_flip.saturating_add(1);

        if self.env < GATE {
            // Still run the output filters so their tail decays smoothly.
            return self.shape(0.0);
        }

        // Schmitt trigger scaled to the current level, so it works at any gain;
        // one toggle per full source cycle is the divide-by-two.
        let hyst = 0.25 * self.env;
        if self.armed {
            if s < -hyst && self.since_flip >= self.lockout {
                self.armed = false;
                self.flip = -self.flip;
                self.since_flip = 0;
            }
        } else if s > hyst {
            self.armed = true;
        }

        let y = self.shape(self.flip * self.env);
        y * self.level
    }

    fn reset(&mut self) {
        for st in self.src_state.iter_mut() {
            st.clear();
        }
        self.out_hp_state.clear();
        self.out_lp_state.clear();
        self.env = 0.0;
        self.flip = 1.0;
        self.armed = false;
        self.since_flip = 0;
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
    /// Crossfade between the two bands, 0 = low, 1 = high. A note near 72 Hz
    /// excites both source bands, and their two squares are phase-independent,
    /// so summing them partially cancels — the sub dropped ~9 dB right at the
    /// handover. Letting the louder band win instead keeps one clean generator
    /// running; the glide gives the switch hysteresis so it can't flutter.
    band_sel: f32,
    sel_glide: f32,
}

impl SubharmonicSynth {
    /// Off, at zero mix — the reference config defaults.
    pub fn new(sample_rate: f32) -> Self {
        Self {
            enabled: false,
            mix: 0.0,
            low: DividerBand::new(sample_rate, 48.0, 72.0, 24.0, 36.0),
            high: DividerBand::new(sample_rate, 72.0, 112.0, 36.0, 56.0),
            band_sel: 0.0,
            sel_glide: time_coeff(50.0, sample_rate),
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

            let lo = self.low.tick(mono);
            let hi = self.high.tick(mono);

            let want = if self.high.env > self.low.env {
                1.0
            } else {
                0.0
            };
            self.band_sel = self.sel_glide * self.band_sel + (1.0 - self.sel_glide) * want;

            let sub = (lo * (1.0 - self.band_sel) + hi * self.band_sel) * amount;

            for s in frame.iter_mut() {
                *s = (*s + sub).clamp(-1.5, 1.5);
            }
        }
    }

    pub fn reset(&mut self) {
        self.low.reset();
        self.high.reset();
        self.band_sel = 0.0;
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

    /// Hann-windowed magnitude at `freq`, skipping the settling transient. The
    /// window matters: an unwindowed bin next to a loud fundamental picks up
    /// several percent of pure leakage, which reads as distortion that isn't
    /// there.
    fn magnitude_at(signal: &[f32], freq: f32) -> f32 {
        let skip = signal.len() / 2;
        let s = &signal[skip..];
        let n = s.len() as f64;
        let w = 2.0 * std::f64::consts::PI * freq as f64 / SR as f64;
        let (mut re, mut im) = (0.0f64, 0.0f64);
        for (i, &x) in s.iter().enumerate() {
            let win = 0.5 - 0.5 * (2.0 * std::f64::consts::PI * i as f64 / n).cos();
            let v = x as f64 * win;
            re += v * (w * i as f64).cos();
            im += v * (w * i as f64).sin();
        }
        // Hann coherent gain is 0.5, so the usual 2/N becomes 4/N.
        (4.0 * (re * re + im * im).sqrt() / n) as f32
    }

    fn run(freq: f32, amp: f32, frames: usize) -> Vec<f32> {
        let mut s = SubharmonicSynth::new(SR);
        s.set_enabled(true);
        s.set_mix(1.0);
        let mut buf = tone(freq, amp, frames);
        s.process(&mut buf, 1);
        buf
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
        // 60 Hz sits in the low band's source octave, so we expect 30 Hz out.
        let buf = run(60.0, 0.5, 96_000);
        let sub = magnitude_at(&buf, 30.0);
        assert!(sub > 0.05, "no octave-down content, got {sub}");
    }

    #[test]
    fn dry_signal_is_kept_not_faded() {
        // 800 Hz is far outside both bands: it must come through untouched.
        let buf = run(800.0, 0.5, 48_000);
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

    /// The lockout's reason for existing: without it the divider slid into
    /// period-3 / period-4 between the band centres.
    #[test]
    fn no_inharmonic_subdivisions() {
        for f0 in [50.0f32, 55.0, 65.0, 70.0, 75.0, 85.0, 100.0, 110.0] {
            let buf = run(f0, 0.25, 96_000);
            let sub = magnitude_at(&buf, f0 / 2.0);
            let junk = magnitude_at(&buf, f0 / 3.0)
                .max(magnitude_at(&buf, 2.0 * f0 / 3.0))
                .max(magnitude_at(&buf, f0 / 4.0))
                .max(magnitude_at(&buf, 3.0 * f0 / 4.0));
            assert!(
                junk < 0.05 * sub,
                "{f0} Hz: inharmonic {junk} against sub {sub}"
            );
        }
    }

    /// The resonant output filter used to swing the level ~37 dB across the
    /// range; the flat HP/LP pair has to keep it in a usable window.
    #[test]
    fn sub_level_is_even_across_the_band() {
        let levels: Vec<f32> = [50.0f32, 60.0, 70.0, 80.0, 90.0, 100.0, 110.0]
            .iter()
            .map(|&f0| magnitude_at(&run(f0, 0.25, 96_000), f0 / 2.0))
            .collect();

        let lo = levels.iter().cloned().fold(f32::MAX, f32::min);
        let hi = levels.iter().cloned().fold(0.0f32, f32::max);
        let spread_db = 20.0 * (hi / lo).log10();
        assert!(
            spread_db < 8.0,
            "sub level spread {spread_db} dB: {levels:?}"
        );
    }

    /// The sub is a seasoning, not a layer. At full mix it must still sit below
    /// the source it was derived from — an earlier build made it as loud as the
    /// whole program.
    #[test]
    fn sub_stays_below_the_source_level() {
        for f0 in [55.0f32, 60.0, 80.0, 100.0] {
            let dry = tone(f0, 0.25, 96_000);
            let wet = run(f0, 0.25, 96_000);
            let sub = magnitude_at(&wet, f0 / 2.0);
            assert!(
                sub < 0.25,
                "{f0} Hz: sub {sub} is not below the 0.25 source"
            );
        }
        // And it is actually doing something at all.
        assert!(magnitude_at(&run(60.0, 0.25, 96_000), 30.0) > 0.02);
    }
}
