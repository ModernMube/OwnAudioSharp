//! SmartMaster — composite intelligent mastering / PA chain, hosted as one
//! native effect.
//!
//! The layout follows a dbx DriveRack style processor: everything that shapes
//! the program runs before the crossover, everything that protects the drivers
//! runs after it, per band.
//!
//! ```text
//! in ─► subsonic HPF ─► 30-band GEQ ─► 8-band PEQ ─► [subharmonic] ─► [comp]
//!                                                                       │
//!     ┌──────────────────────────────────────────────────────────────────┘
//!     ▼  (only when the crossover is engaged)
//!   split ─┬─ main L/R (highs) ─► gain ─► delay/polarity ─► main limiter ─┐
//!          └─ mono sub (lows)  ─► gain ─► delay/polarity ─► sub limiter  ─┴─► sum
//!                                                                            │
//!                                                       output limiter ◄─────┘
//! ```
//!
//! The stages that already exist as standalone effects ([`Equalizer30`],
//! [`Compressor`], [`Limiter`]) delegate to those, so the composite stays
//! numerically identical to running them individually.  The C#
//! `SmartMasterEffect` remains the parameter model and preset owner and mirrors
//! its config onto the parameters below.
//!
//! # Parameter map
//!
//! | id       | meaning                                    | units        |
//! |----------|--------------------------------------------|--------------|
//! | 0        | enabled                                    | 0/1          |
//! | 1        | mix (accepted, inert)                      | 0..1         |
//! | 2..=31   | graphic-EQ band gains (30 bands)           | dB           |
//! | 32       | subharmonic enabled                        | 0/1          |
//! | 33       | subharmonic mix                            | 0..1         |
//! | 34       | compressor enabled                         | 0/1          |
//! | 35       | compressor threshold                       | linear 0..1  |
//! | 36       | compressor ratio                           | N:1          |
//! | 37       | compressor attack                          | ms           |
//! | 38       | compressor release                         | ms           |
//! | 39       | crossover frequency                        | Hz           |
//! | 40..=42  | alignment delay main-L / main-R / sub      | ms           |
//! | 43..=45  | polarity main-L / main-R / sub             | 0/1          |
//! | 46       | output limiter threshold                   | dBFS         |
//! | 47       | output limiter ceiling                     | dBFS         |
//! | 48       | output limiter release                     | ms           |
//! | 49       | subsonic filter enabled                    | 0/1          |
//! | 50       | subsonic frequency                         | Hz           |
//! | 51       | compressor knee (OverEasy)                 | dB           |
//! | 52       | subharmonic low-band level (24–36 Hz)      | 0..1         |
//! | 53       | subharmonic high-band level (36–56 Hz)     | 0..1         |
//! | 54       | crossover engaged                          | 0/1          |
//! | 55..=57  | output gain main-L / main-R / sub          | dB           |
//! | 58       | main-band limiter threshold                | dBFS         |
//! | 59       | sub-band limiter threshold                 | dBFS         |
//! | 60..=91  | PEQ band b: 60+4b shape, +1 freq, +2 Q, +3 gain |         |

mod biquad;
mod crossover;
mod peq;
mod phase_alignment;
mod subharmonic;
mod subsonic;

use crossover::Crossover;
use peq::{BandShape, ParametricEq};
use phase_alignment::PhaseAlignment;
use subharmonic::SubharmonicSynth;
use subsonic::Subsonic;

use super::compressor::{self, Compressor};
use super::equalizer30::{Equalizer30, PARAM_BAND_0, PARAM_BAND_29};
use super::limiter::{self, Limiter};
use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};

const PARAM_SUB_ENABLED: u32 = 32;
const PARAM_SUB_MIX: u32 = 33;
const PARAM_COMP_ENABLED: u32 = 34;
const PARAM_COMP_THRESHOLD: u32 = 35;
const PARAM_COMP_RATIO: u32 = 36;
const PARAM_COMP_ATTACK: u32 = 37;
const PARAM_COMP_RELEASE: u32 = 38;
const PARAM_CROSSOVER_FREQ: u32 = 39;
const PARAM_DELAY_L: u32 = 40;
const PARAM_DELAY_SUB: u32 = 42;
const PARAM_INVERT_L: u32 = 43;
const PARAM_INVERT_SUB: u32 = 45;
const PARAM_LIMIT_THRESHOLD: u32 = 46;
const PARAM_LIMIT_CEILING: u32 = 47;
const PARAM_LIMIT_RELEASE: u32 = 48;
const PARAM_SUBSONIC_ENABLED: u32 = 49;
const PARAM_SUBSONIC_FREQ: u32 = 50;
const PARAM_COMP_KNEE: u32 = 51;
const PARAM_SUB_LOW_LEVEL: u32 = 52;
const PARAM_SUB_HIGH_LEVEL: u32 = 53;
const PARAM_CROSSOVER_ENABLED: u32 = 54;
const PARAM_OUT_GAIN_L: u32 = 55;
const PARAM_OUT_GAIN_SUB: u32 = 57;
const PARAM_MAIN_LIMIT_THRESHOLD: u32 = 58;
const PARAM_SUB_LIMIT_THRESHOLD: u32 = 59;
const PARAM_PEQ_BASE: u32 = 60;
const PARAM_PEQ_END: u32 = PARAM_PEQ_BASE + (peq::BANDS as u32) * 4 - 1;

/// Alignment channels: main L, main R, sub.
const ALIGN: usize = 3;

/// Initial crossover-scratch capacity in frames, grown off the hot path.
const INITIAL_SCRATCH_FRAMES: usize = 4_096;

/// Linear amplitude (0..1) to dB, matching the compressor's threshold conversion.
#[inline]
fn lin_to_db(lin: f32) -> f32 {
    20.0 * lin.max(1.0e-6).log10()
}

#[inline]
fn db_to_lin(db: f32) -> f32 {
    10.0f32.powf(db / 20.0)
}

/// Composite SmartMaster mastering effect.
pub struct SmartMaster {
    enabled: bool,
    /// Accepted for API symmetry but inert — the chain always runs fully wet.
    mix: f32,

    subsonic: Subsonic,
    eq: Equalizer30,
    peq: ParametricEq,
    subharmonic: SubharmonicSynth,
    compressor: Compressor,

    crossover: Crossover,
    crossover_engaged: bool,
    phase: PhaseAlignment,
    /// Per-band trim, linear: main L, main R, sub.
    out_gain: [f32; ALIGN],
    out_gain_db: [f32; ALIGN],
    main_limiter: Limiter,
    sub_limiter: Limiter,
    limiter: Limiter,

    // Shadow values so get_param reports back exactly what was set.
    comp_threshold_lin: f32,
    comp_ratio: f32,
    crossover_freq: f32,
    delays_ms: [f32; ALIGN],
    invert: [bool; ALIGN],

    temp_l: Vec<f32>,
    temp_r: Vec<f32>,
    sub_l: Vec<f32>,
    sub_r: Vec<f32>,
    mono_sub: Vec<f32>,
    /// Interleaved scratch the main-band limiter runs over.
    band_scratch: Vec<f32>,
}

impl SmartMaster {
    /// Builds the chain at `sample_rate`, matching the C# `SmartMasterConfig`
    /// defaults. The control plane overwrites every parameter on its first sync.
    pub fn new(sample_rate: f32) -> Self {
        let mut compressor = Compressor::new(sample_rate);
        compressor.set_enabled(false);
        compressor.set_param(compressor::PARAM_THRESHOLD, lin_to_db(0.5));
        compressor.set_param(compressor::PARAM_RATIO, 4.0);
        compressor.set_param(compressor::PARAM_ATTACK, 10.0);
        compressor.set_param(compressor::PARAM_RELEASE, 100.0);

        let mut limiter = Limiter::new(sample_rate);
        limiter.set_param(limiter::PARAM_THRESHOLD, -0.1);
        limiter.set_param(limiter::PARAM_CEILING, -0.1);
        limiter.set_param(limiter::PARAM_RELEASE, 50.0);

        // The band limiters protect drivers, not the bus: they sit wide open
        // until a preset pulls them down, and never clamp on their own.
        let band_limiter = || {
            let mut l = Limiter::new(sample_rate);
            l.set_param(limiter::PARAM_THRESHOLD, 0.0);
            l.set_param(limiter::PARAM_CEILING, 0.0);
            l.set_param(limiter::PARAM_RELEASE, 80.0);
            l
        };

        let mut sm = Self {
            enabled: true,
            mix: 1.0,
            subsonic: Subsonic::new(sample_rate, 35.0),
            eq: Equalizer30::new(sample_rate),
            peq: ParametricEq::new(sample_rate),
            subharmonic: SubharmonicSynth::new(sample_rate),
            compressor,
            crossover: Crossover::new(sample_rate, 80.0),
            crossover_engaged: false,
            phase: PhaseAlignment::new(sample_rate),
            out_gain: [1.0; ALIGN],
            out_gain_db: [0.0; ALIGN],
            main_limiter: band_limiter(),
            sub_limiter: band_limiter(),
            limiter,
            comp_threshold_lin: 0.5,
            comp_ratio: 4.0,
            crossover_freq: 80.0,
            delays_ms: [0.0; ALIGN],
            invert: [false; ALIGN],
            temp_l: vec![0.0; INITIAL_SCRATCH_FRAMES],
            temp_r: vec![0.0; INITIAL_SCRATCH_FRAMES],
            sub_l: vec![0.0; INITIAL_SCRATCH_FRAMES],
            sub_r: vec![0.0; INITIAL_SCRATCH_FRAMES],
            mono_sub: vec![0.0; INITIAL_SCRATCH_FRAMES],
            band_scratch: vec![0.0; INITIAL_SCRATCH_FRAMES * 2],
        };

        sm.sync_comp_makeup();
        sm
    }

    /// The crossover section runs when it is switched on, or when an alignment
    /// delay / polarity flip needs it (that was the old implicit trigger).
    fn crossover_active(&self) -> bool {
        self.crossover_engaged
            || self.delays_ms.iter().any(|d| d.abs() > 0.001)
            || self.invert.iter().any(|&b| b)
    }

    fn ensure_scratch(&mut self, frames: usize) {
        if self.temp_l.len() < frames {
            self.temp_l.resize(frames, 0.0);
            self.temp_r.resize(frames, 0.0);
            self.sub_l.resize(frames, 0.0);
            self.sub_r.resize(frames, 0.0);
            self.mono_sub.resize(frames, 0.0);
            self.band_scratch.resize(frames * 2, 0.0);
        }
    }

    /// Splits into a main (high) and a mono sub (low) band, runs each through its
    /// own trim, alignment and limiter, then sums them back.
    fn process_crossover_chain(&mut self, buffer: &mut [f32], ch: usize, frames: usize) {
        self.ensure_scratch(frames);

        for f in 0..frames {
            self.temp_l[f] = buffer[f * ch];
            self.temp_r[f] = if ch > 1 {
                buffer[f * ch + 1]
            } else {
                self.temp_l[f]
            };
        }

        self.crossover
            .process_channel(&mut self.temp_l[..frames], &mut self.sub_l[..frames], 0);
        self.crossover
            .process_channel(&mut self.temp_r[..frames], &mut self.sub_r[..frames], 1);

        // Trim, then align, then limit — the order a driver sees it.
        for f in 0..frames {
            self.mono_sub[f] = (self.sub_l[f] + self.sub_r[f]) * 0.5 * self.out_gain[2];
            self.temp_l[f] *= self.out_gain[0];
            self.temp_r[f] *= self.out_gain[1];
        }

        self.phase.process_channel(&mut self.temp_l[..frames], 0);
        self.phase.process_channel(&mut self.temp_r[..frames], 1);
        self.phase.process_channel(&mut self.mono_sub[..frames], 2);

        // The main band goes through as a stereo pair so its limiter stays linked.
        for f in 0..frames {
            self.band_scratch[f * 2] = self.temp_l[f];
            self.band_scratch[f * 2 + 1] = self.temp_r[f];
        }
        self.main_limiter
            .process(&mut self.band_scratch[..frames * 2], 2);
        self.sub_limiter.process(&mut self.mono_sub[..frames], 1);

        for f in 0..frames {
            let sub = self.mono_sub[f];
            buffer[f * ch] = self.band_scratch[f * 2] + sub;
            if ch > 1 {
                buffer[f * ch + 1] = self.band_scratch[f * 2 + 1] + sub;
            }
        }
    }

    /// Mirrors the C# `CompressorEffect._autoMakeupGain`: guess a makeup from
    /// threshold and ratio assuming a -12 dBFS average, and only give back 80 %
    /// of the reduction so the dynamics stay alive. The composite has no makeup
    /// parameter of its own, and leaving the compressor's own default in place
    /// applied a flat +1.6 dB to every preset that nobody asked for — which both
    /// lifted the whole chain and erased a real difference between presets.
    fn sync_comp_makeup(&mut self) {
        const TYPICAL_INPUT_DB: f32 = -12.0;

        let threshold_db = lin_to_db(self.comp_threshold_lin);
        let makeup_db = if TYPICAL_INPUT_DB < threshold_db {
            0.0
        } else {
            let slope = 1.0 / self.comp_ratio - 1.0;
            let gr_db = slope * (TYPICAL_INPUT_DB - threshold_db) * 0.5;
            -gr_db * 0.8
        };

        self.compressor
            .set_param(compressor::PARAM_MAKEUP, makeup_db);
    }

    fn peq_param(&mut self, param_id: u32, value: f32) -> bool {
        let offset = (param_id - PARAM_PEQ_BASE) as usize;
        let (band, field) = (offset / 4, offset % 4);

        match field {
            0 => self.peq.set_shape(band, BandShape::from_param(value)),
            1 => self.peq.set_frequency(band, value),
            2 => self.peq.set_q(band, value),
            _ => self.peq.set_gain_db(band, value),
        }
        true
    }

    fn peq_value(&self, param_id: u32) -> Option<f32> {
        let offset = (param_id - PARAM_PEQ_BASE) as usize;
        let (band, field) = (offset / 4, offset % 4);

        match field {
            0 => self.peq.shape(band).map(BandShape::to_param),
            1 => self.peq.frequency(band),
            2 => self.peq.q(band),
            _ => self.peq.gain_db(band),
        }
    }
}

impl Effect for SmartMaster {
    fn effect_type(&self) -> EffectType {
        EffectType::SmartMaster
    }

    fn process(&mut self, buffer: &mut [f32], channels: u16) {
        if !self.enabled || channels == 0 {
            return;
        }
        let ch = channels as usize;
        let frames = buffer.len() / ch;
        if frames == 0 {
            return;
        }

        self.subsonic.process(buffer, channels);
        self.eq.process(buffer, channels);
        self.peq.process(buffer, channels);

        if self.subharmonic.enabled() && self.subharmonic.mix() > 0.0 {
            self.subharmonic.process(buffer, channels);
        }

        self.compressor.process(buffer, channels);

        if self.crossover_active() {
            self.process_crossover_chain(buffer, ch, frames);
        }
        self.limiter.process(buffer, channels);
    }

    fn set_param(&mut self, param_id: u32, value: f32) -> bool {
        match param_id {
            PARAM_ENABLED => {
                self.enabled = value >= 0.5;
                true
            }
            PARAM_MIX => {
                self.mix = value.clamp(0.0, 1.0);
                true
            }
            PARAM_BAND_0..=PARAM_BAND_29 => self.eq.set_param(param_id, value),
            PARAM_SUB_ENABLED => {
                self.subharmonic.set_enabled(value >= 0.5);
                true
            }
            PARAM_SUB_MIX => {
                self.subharmonic.set_mix(value);
                true
            }
            PARAM_COMP_ENABLED => {
                self.compressor.set_enabled(value >= 0.5);
                true
            }
            PARAM_COMP_THRESHOLD => {
                self.comp_threshold_lin = value.clamp(0.0, 1.0);
                self.compressor.set_param(
                    compressor::PARAM_THRESHOLD,
                    lin_to_db(self.comp_threshold_lin),
                );
                self.sync_comp_makeup();
                true
            }
            PARAM_COMP_RATIO => {
                self.comp_ratio = value.max(1.0);
                let ok = self.compressor.set_param(compressor::PARAM_RATIO, value);
                self.sync_comp_makeup();
                ok
            }
            PARAM_COMP_ATTACK => self.compressor.set_param(compressor::PARAM_ATTACK, value),
            PARAM_COMP_RELEASE => self.compressor.set_param(compressor::PARAM_RELEASE, value),
            PARAM_COMP_KNEE => self.compressor.set_param(compressor::PARAM_KNEE, value),
            PARAM_CROSSOVER_FREQ => {
                self.crossover_freq = value.max(1.0);
                self.crossover.set_frequency(self.crossover_freq);
                true
            }
            PARAM_CROSSOVER_ENABLED => {
                self.crossover_engaged = value >= 0.5;
                true
            }
            PARAM_DELAY_L..=PARAM_DELAY_SUB => {
                let ch = (param_id - PARAM_DELAY_L) as usize;
                self.delays_ms[ch] = value;
                self.phase.set_delay_ms(ch, value);
                true
            }
            PARAM_INVERT_L..=PARAM_INVERT_SUB => {
                let ch = (param_id - PARAM_INVERT_L) as usize;
                self.invert[ch] = value >= 0.5;
                self.phase.set_invert(ch, self.invert[ch]);
                true
            }
            PARAM_OUT_GAIN_L..=PARAM_OUT_GAIN_SUB => {
                let ch = (param_id - PARAM_OUT_GAIN_L) as usize;
                self.out_gain_db[ch] = value.clamp(-24.0, 12.0);
                self.out_gain[ch] = db_to_lin(self.out_gain_db[ch]);
                true
            }
            PARAM_MAIN_LIMIT_THRESHOLD => {
                self.main_limiter.set_param(limiter::PARAM_THRESHOLD, value)
            }
            PARAM_SUB_LIMIT_THRESHOLD => {
                self.sub_limiter.set_param(limiter::PARAM_THRESHOLD, value)
            }
            PARAM_LIMIT_THRESHOLD => self.limiter.set_param(limiter::PARAM_THRESHOLD, value),
            PARAM_LIMIT_CEILING => self.limiter.set_param(limiter::PARAM_CEILING, value),
            PARAM_LIMIT_RELEASE => self.limiter.set_param(limiter::PARAM_RELEASE, value),
            PARAM_SUBSONIC_ENABLED => {
                self.subsonic.set_enabled(value >= 0.5);
                true
            }
            PARAM_SUBSONIC_FREQ => {
                self.subsonic.set_frequency(value);
                true
            }
            PARAM_SUB_LOW_LEVEL => {
                self.subharmonic.set_low_level(value);
                true
            }
            PARAM_SUB_HIGH_LEVEL => {
                self.subharmonic.set_high_level(value);
                true
            }
            PARAM_PEQ_BASE..=PARAM_PEQ_END => self.peq_param(param_id, value),
            _ => false,
        }
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        match param_id {
            PARAM_ENABLED => Some(if self.enabled { 1.0 } else { 0.0 }),
            PARAM_MIX => Some(self.mix),
            PARAM_BAND_0..=PARAM_BAND_29 => self.eq.get_param(param_id),
            PARAM_SUB_ENABLED => Some(if self.subharmonic.enabled() { 1.0 } else { 0.0 }),
            PARAM_SUB_MIX => Some(self.subharmonic.mix()),
            PARAM_COMP_ENABLED => self.compressor.get_param(PARAM_ENABLED),
            PARAM_COMP_THRESHOLD => Some(self.comp_threshold_lin),
            PARAM_COMP_RATIO => self.compressor.get_param(compressor::PARAM_RATIO),
            PARAM_COMP_ATTACK => self.compressor.get_param(compressor::PARAM_ATTACK),
            PARAM_COMP_RELEASE => self.compressor.get_param(compressor::PARAM_RELEASE),
            PARAM_COMP_KNEE => self.compressor.get_param(compressor::PARAM_KNEE),
            PARAM_CROSSOVER_FREQ => Some(self.crossover_freq),
            PARAM_CROSSOVER_ENABLED => Some(if self.crossover_engaged { 1.0 } else { 0.0 }),
            PARAM_DELAY_L..=PARAM_DELAY_SUB => {
                Some(self.delays_ms[(param_id - PARAM_DELAY_L) as usize])
            }
            PARAM_INVERT_L..=PARAM_INVERT_SUB => {
                Some(if self.invert[(param_id - PARAM_INVERT_L) as usize] {
                    1.0
                } else {
                    0.0
                })
            }
            PARAM_OUT_GAIN_L..=PARAM_OUT_GAIN_SUB => {
                Some(self.out_gain_db[(param_id - PARAM_OUT_GAIN_L) as usize])
            }
            PARAM_MAIN_LIMIT_THRESHOLD => self.main_limiter.get_param(limiter::PARAM_THRESHOLD),
            PARAM_SUB_LIMIT_THRESHOLD => self.sub_limiter.get_param(limiter::PARAM_THRESHOLD),
            PARAM_LIMIT_THRESHOLD => self.limiter.get_param(limiter::PARAM_THRESHOLD),
            PARAM_LIMIT_CEILING => self.limiter.get_param(limiter::PARAM_CEILING),
            PARAM_LIMIT_RELEASE => self.limiter.get_param(limiter::PARAM_RELEASE),
            PARAM_SUBSONIC_ENABLED => Some(if self.subsonic.enabled() { 1.0 } else { 0.0 }),
            PARAM_SUBSONIC_FREQ => Some(self.subsonic.frequency()),
            PARAM_SUB_LOW_LEVEL => Some(self.subharmonic.low_level()),
            PARAM_SUB_HIGH_LEVEL => Some(self.subharmonic.high_level()),
            PARAM_PEQ_BASE..=PARAM_PEQ_END => self.peq_value(param_id),
            _ => None,
        }
    }

    fn reset(&mut self) {
        self.subsonic.reset();
        self.eq.reset();
        self.peq.reset();
        self.subharmonic.reset();
        self.compressor.reset();
        self.crossover.reset();
        self.phase.reset();
        self.main_limiter.reset();
        self.sub_limiter.reset();
        self.limiter.reset();

        for buf in [
            &mut self.temp_l,
            &mut self.temp_r,
            &mut self.sub_l,
            &mut self.sub_r,
            &mut self.mono_sub,
            &mut self.band_scratch,
        ] {
            buf.iter_mut().for_each(|s| *s = 0.0);
        }
    }

    fn is_enabled(&self) -> bool {
        self.enabled
    }

    fn set_enabled(&mut self, enabled: bool) {
        self.enabled = enabled;
    }

    fn latency_samples(&self) -> u32 {
        // Look-ahead limiters are the only latency source; with the crossover
        // running, the band limiter sits in series with the output one.
        let band = if self.crossover_active() {
            self.main_limiter.latency_samples()
        } else {
            0
        };
        band + self.limiter.latency_samples()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const SR: f32 = 48_000.0;

    fn stereo_tone(freq: f32, amp: f32, frames: usize) -> Vec<f32> {
        let mut out = Vec::with_capacity(frames * 2);
        for i in 0..frames {
            let v = amp * (2.0 * std::f32::consts::PI * freq * i as f32 / SR).sin();
            out.push(v);
            out.push(v);
        }
        out
    }

    /// RMS of the second half, past any look-ahead ramp-in.
    fn tail_rms(buf: &[f32]) -> f32 {
        let tail = &buf[buf.len() / 2..];
        (tail.iter().map(|x| x * x).sum::<f32>() / tail.len() as f32).sqrt()
    }

    #[test]
    fn effect_type_is_smartmaster() {
        let sm = SmartMaster::new(SR);
        assert_eq!(sm.effect_type(), EffectType::SmartMaster);
    }

    #[test]
    fn disabled_passes_signal_through() {
        let mut sm = SmartMaster::new(SR);
        sm.set_param(PARAM_ENABLED, 0.0);
        let input = stereo_tone(200.0, 0.3, 512);
        let mut buf = input.clone();
        sm.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn default_chain_limits_but_keeps_output_finite() {
        let mut sm = SmartMaster::new(SR);
        let mut buf = stereo_tone(220.0, 0.9, 8_192);
        sm.process(&mut buf, 2);
        assert!(buf.iter().all(|s| s.is_finite()));
        let ceiling = 10.0f32.powf(-0.1 / 20.0) + 1.0e-4;
        assert!(buf.iter().all(|&s| s.abs() <= ceiling));
    }

    /// The composite exposes no makeup parameter, so the inner compressor used
    /// to keep its own +1.6 dB default and apply it to every preset. The managed
    /// `CompressorEffect` derives one from threshold and ratio instead; these are
    /// the values that formula gives for the factory presets.
    #[test]
    fn compressor_makeup_matches_the_managed_formula() {
        for (threshold, ratio, want_db) in [
            (0.158f32, 2.0f32, 0.806f32),
            (0.200, 1.8, 0.352),
            (0.126, 1.5, 0.800),
            (0.251, 3.0, 0.000),
            (0.200, 2.5, 0.475),
        ] {
            let mut sm = SmartMaster::new(SR);
            sm.set_param(PARAM_COMP_ENABLED, 1.0);
            sm.set_param(PARAM_COMP_THRESHOLD, threshold);
            sm.set_param(PARAM_COMP_RATIO, ratio);
            sm.set_param(PARAM_LIMIT_THRESHOLD, 0.0);
            sm.set_param(PARAM_LIMIT_CEILING, 0.0);

            // Well under the threshold, so only the makeup shows up.
            let amp = 0.02;
            let mut buf = stereo_tone(1_000.0, amp, 24_000);
            sm.process(&mut buf, 2);

            let peak = buf[buf.len() / 2..]
                .iter()
                .fold(0.0f32, |m, s| m.max(s.abs()));
            let got_db = 20.0 * (peak / amp).log10();
            assert!(
                (got_db - want_db).abs() < 0.05,
                "threshold {threshold} ratio {ratio}: {got_db} dB, want {want_db}"
            );
        }
    }

    #[test]
    fn limiter_no_longer_ducks_under_the_threshold() {
        // A tone 6 dB over threshold should land *at* the threshold, not 6 dB
        // under it the way the squared gain law used to leave it.
        let mut sm = SmartMaster::new(SR);
        sm.set_param(PARAM_LIMIT_THRESHOLD, -6.0);
        sm.set_param(PARAM_LIMIT_CEILING, 0.0);

        let mut buf = stereo_tone(400.0, 1.0, 48_000);
        sm.process(&mut buf, 2);

        let peak = buf[buf.len() / 2..]
            .iter()
            .fold(0.0f32, |m, s| m.max(s.abs()));
        let peak_db = 20.0 * peak.log10();
        assert!(
            (peak_db + 6.0).abs() < 1.5,
            "expected about -6 dBFS, got {peak_db}"
        );
    }

    #[test]
    fn subsonic_filter_removes_rumble() {
        let mut sm = SmartMaster::new(SR);
        sm.set_param(PARAM_SUBSONIC_ENABLED, 1.0);
        sm.set_param(PARAM_SUBSONIC_FREQ, 60.0);

        let mut buf = stereo_tone(15.0, 0.5, 48_000);
        sm.process(&mut buf, 2);
        assert!(tail_rms(&buf) < 0.02, "subsonic content survived");
    }

    #[test]
    fn peq_band_cuts_where_it_is_pointed() {
        let mut sm = SmartMaster::new(SR);
        sm.set_param(PARAM_PEQ_BASE + 1, 1_000.0);
        sm.set_param(PARAM_PEQ_BASE + 2, 2.0);
        sm.set_param(PARAM_PEQ_BASE + 3, -12.0);

        let mut cut = stereo_tone(1_000.0, 0.2, 48_000);
        sm.process(&mut cut, 2);
        sm.reset();
        let mut away = stereo_tone(100.0, 0.2, 48_000);
        sm.process(&mut away, 2);

        assert!(
            tail_rms(&cut) < tail_rms(&away) * 0.4,
            "the PEQ band did not cut"
        );
    }

    #[test]
    fn output_trim_scales_the_main_band() {
        let mut sm = SmartMaster::new(SR);
        sm.set_param(PARAM_CROSSOVER_ENABLED, 1.0);
        sm.set_param(PARAM_CROSSOVER_FREQ, 100.0);
        sm.set_param(PARAM_LIMIT_THRESHOLD, 0.0);

        let mut unity = stereo_tone(1_000.0, 0.2, 24_000);
        sm.process(&mut unity, 2);

        sm.reset();
        sm.set_param(PARAM_OUT_GAIN_L, -6.0);
        sm.set_param(PARAM_OUT_GAIN_L + 1, -6.0);
        let mut trimmed = stereo_tone(1_000.0, 0.2, 24_000);
        sm.process(&mut trimmed, 2);

        let ratio = tail_rms(&trimmed) / tail_rms(&unity);
        assert!((ratio - 0.5).abs() < 0.08, "trim ratio {ratio}");
    }

    #[test]
    fn param_roundtrip_across_all_ids() {
        let mut sm = SmartMaster::new(SR);
        sm.set_param(PARAM_MIX, 0.7);
        sm.set_param(PARAM_BAND_0, 6.0);
        sm.set_param(PARAM_BAND_29, -4.0);
        sm.set_param(PARAM_SUB_ENABLED, 1.0);
        sm.set_param(PARAM_SUB_MIX, 0.5);
        sm.set_param(PARAM_COMP_ENABLED, 1.0);
        sm.set_param(PARAM_COMP_THRESHOLD, 0.25);
        sm.set_param(PARAM_COMP_RATIO, 6.0);
        sm.set_param(PARAM_COMP_ATTACK, 12.0);
        sm.set_param(PARAM_COMP_RELEASE, 150.0);
        sm.set_param(PARAM_COMP_KNEE, 10.0);
        sm.set_param(PARAM_CROSSOVER_FREQ, 90.0);
        sm.set_param(PARAM_CROSSOVER_ENABLED, 1.0);
        sm.set_param(PARAM_DELAY_L, 1.5);
        sm.set_param(PARAM_INVERT_SUB, 1.0);
        sm.set_param(PARAM_OUT_GAIN_SUB, -3.0);
        sm.set_param(PARAM_MAIN_LIMIT_THRESHOLD, -2.0);
        sm.set_param(PARAM_SUB_LIMIT_THRESHOLD, -4.0);
        sm.set_param(PARAM_LIMIT_THRESHOLD, -1.0);
        sm.set_param(PARAM_LIMIT_CEILING, -0.3);
        sm.set_param(PARAM_LIMIT_RELEASE, 80.0);
        sm.set_param(PARAM_SUBSONIC_ENABLED, 1.0);
        sm.set_param(PARAM_SUBSONIC_FREQ, 45.0);
        sm.set_param(PARAM_SUB_LOW_LEVEL, 0.6);
        sm.set_param(PARAM_SUB_HIGH_LEVEL, 0.4);
        sm.set_param(PARAM_PEQ_BASE, 2.0);
        sm.set_param(PARAM_PEQ_BASE + 1, 8_000.0);
        sm.set_param(PARAM_PEQ_BASE + 2, 0.9);
        sm.set_param(PARAM_PEQ_BASE + 3, 3.5);

        assert_eq!(sm.get_param(PARAM_MIX), Some(0.7));
        assert_eq!(sm.get_param(PARAM_BAND_0), Some(6.0));
        assert_eq!(sm.get_param(PARAM_BAND_29), Some(-4.0));
        assert_eq!(sm.get_param(PARAM_SUB_ENABLED), Some(1.0));
        assert_eq!(sm.get_param(PARAM_SUB_MIX), Some(0.5));
        assert_eq!(sm.get_param(PARAM_COMP_ENABLED), Some(1.0));
        assert_eq!(sm.get_param(PARAM_COMP_THRESHOLD), Some(0.25));
        assert_eq!(sm.get_param(PARAM_COMP_RATIO), Some(6.0));
        assert_eq!(sm.get_param(PARAM_COMP_ATTACK), Some(12.0));
        assert_eq!(sm.get_param(PARAM_COMP_RELEASE), Some(150.0));
        assert_eq!(sm.get_param(PARAM_COMP_KNEE), Some(10.0));
        assert_eq!(sm.get_param(PARAM_CROSSOVER_FREQ), Some(90.0));
        assert_eq!(sm.get_param(PARAM_CROSSOVER_ENABLED), Some(1.0));
        assert_eq!(sm.get_param(PARAM_DELAY_L), Some(1.5));
        assert_eq!(sm.get_param(PARAM_INVERT_SUB), Some(1.0));
        assert_eq!(sm.get_param(PARAM_OUT_GAIN_SUB), Some(-3.0));
        assert_eq!(sm.get_param(PARAM_MAIN_LIMIT_THRESHOLD), Some(-2.0));
        assert_eq!(sm.get_param(PARAM_SUB_LIMIT_THRESHOLD), Some(-4.0));
        assert_eq!(sm.get_param(PARAM_LIMIT_THRESHOLD), Some(-1.0));
        assert_eq!(sm.get_param(PARAM_LIMIT_CEILING), Some(-0.3));
        assert_eq!(sm.get_param(PARAM_LIMIT_RELEASE), Some(80.0));
        assert_eq!(sm.get_param(PARAM_SUBSONIC_ENABLED), Some(1.0));
        assert_eq!(sm.get_param(PARAM_SUBSONIC_FREQ), Some(45.0));
        assert_eq!(sm.get_param(PARAM_SUB_LOW_LEVEL), Some(0.6));
        assert_eq!(sm.get_param(PARAM_SUB_HIGH_LEVEL), Some(0.4));
        assert_eq!(sm.get_param(PARAM_PEQ_BASE), Some(2.0));
        assert_eq!(sm.get_param(PARAM_PEQ_BASE + 1), Some(8_000.0));
        assert_eq!(sm.get_param(PARAM_PEQ_BASE + 2), Some(0.9));
        assert_eq!(sm.get_param(PARAM_PEQ_BASE + 3), Some(3.5));
    }

    #[test]
    fn every_param_id_is_within_the_declared_window() {
        let sm = SmartMaster::new(SR);
        for id in 0..=PARAM_PEQ_END {
            let _ = sm.get_param(id);
        }
        assert!(sm.get_param(PARAM_PEQ_END).is_some());
        assert_eq!(sm.get_param(PARAM_PEQ_END + 1), None);
    }

    #[test]
    fn phase_alignment_engages_and_stays_finite() {
        let mut sm = SmartMaster::new(SR);
        sm.set_param(PARAM_DELAY_L, 0.5);
        sm.set_param(PARAM_INVERT_SUB, 1.0);
        let mut buf = stereo_tone(120.0, 0.6, 4_096);
        sm.process(&mut buf, 2);
        assert!(buf.iter().all(|s| s.is_finite()));
    }

    #[test]
    fn unknown_param_is_rejected() {
        let mut sm = SmartMaster::new(SR);
        assert!(!sm.set_param(999, 1.0));
        assert_eq!(sm.get_param(999), None);
    }

    #[test]
    fn reset_restores_reproducibility() {
        let mut sm = SmartMaster::new(SR);
        sm.set_param(PARAM_BAND_0 + 10, 8.0);
        sm.set_param(PARAM_COMP_ENABLED, 1.0);
        let input = stereo_tone(300.0, 0.5, 2_048);

        let mut first = input.clone();
        sm.process(&mut first, 2);
        sm.reset();
        let mut second = input.clone();
        sm.process(&mut second, 2);
        assert_eq!(first, second);
    }
}
