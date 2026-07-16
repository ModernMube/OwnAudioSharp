//! SmartMaster — composite intelligent mastering chain, hosted as one native
//! effect.
//!
//! Faithful Rust port of the reference C# `SmartMasterEffect` /
//! `SmartMasterAudioChain`.  The chain runs, in order:
//!
//! 1. 30-band graphic EQ ([`Equalizer30`], reused from the effects crate);
//! 2. optional subharmonic synthesizer ([`SubharmonicSynth`]);
//! 3. optional dynamic-range compressor ([`Compressor`], reused);
//! 4. optional crossover → phase-alignment → recombination stage
//!    ([`Crossover`] + [`PhaseAlignment`]);
//! 5. brick-wall limiter ([`Limiter`], reused).
//!
//! Steps 1, 3 and 5 delegate to the crate's existing built-in effects, so the
//! composite is numerically equivalent to running those effects individually;
//! steps 2 and 4 are ported here.  The whole composite is exposed to the C#
//! control plane as a single native effect (see [`EffectType::SmartMaster`]),
//! with the managed `SmartMasterEffect` remaining the parameter model / preset
//! owner and mirroring its config onto the parameters below.
//!
//! The crossover stage only engages when phase alignment is actually requested
//! (any non-zero channel delay or any polarity flip), exactly like the
//! reference `_needsPhaseAlignment` fast path; otherwise the summed signal goes
//! straight from the compressor into the limiter.
//!
//! # Parameter map
//!
//! | id      | meaning                                   | units          |
//! |---------|-------------------------------------------|----------------|
//! | 0       | enabled                                    | 0/1            |
//! | 1       | mix (accepted, inert — reference ignores it) | 0..1         |
//! | 2..=31  | graphic-EQ band gains (30 bands)          | dB             |
//! | 32      | subharmonic enabled                       | 0/1            |
//! | 33      | subharmonic mix                           | 0..1           |
//! | 34      | compressor enabled                        | 0/1            |
//! | 35      | compressor threshold                      | linear 0..1    |
//! | 36      | compressor ratio                          | N:1            |
//! | 37      | compressor attack                         | ms             |
//! | 38      | compressor release                        | ms             |
//! | 39      | crossover frequency                       | Hz             |
//! | 40..=42 | phase-alignment delay L / R / Sub         | ms             |
//! | 43..=45 | phase-alignment invert L / R / Sub        | 0/1            |
//! | 46      | limiter threshold                         | dBFS           |
//! | 47      | limiter ceiling                           | dBFS           |
//! | 48      | limiter release                           | ms             |

mod crossover;
mod phase_alignment;
mod subharmonic;

use crossover::Crossover;
use phase_alignment::PhaseAlignment;
use subharmonic::SubharmonicSynth;

use super::compressor::{self, Compressor};
use super::equalizer30::{Equalizer30, PARAM_BAND_0, PARAM_BAND_29};
use super::limiter::{self, Limiter};
use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};

// -- Composite-specific parameter ids (see the module table) ----------------
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

/// A delay whose magnitude (ms) exceeds this is treated as active, matching the
/// reference `_needsPhaseAlignment` threshold.
const PHASE_DELAY_EPS_MS: f32 = 0.001;

/// Initial crossover-scratch capacity, in frames; grown once off the hot path if
/// a larger block ever arrives.
const INITIAL_SCRATCH_FRAMES: usize = 4_096;

/// Converts a linear amplitude (0..1) to dB, matching the reference compressor's
/// `20·log10(max(x, 1e-6))` threshold conversion.
#[inline]
fn lin_to_db(lin: f32) -> f32 {
    20.0 * lin.max(1.0e-6).log10()
}

/// Composite SmartMaster mastering effect.
pub struct SmartMaster {
    enabled: bool,
    /// Accepted for API symmetry but inert — the reference `SmartMasterEffect`
    /// does not apply its `Mix` (the chain always runs fully wet).
    mix: f32,

    eq: Equalizer30,
    subharmonic: SubharmonicSynth,
    compressor: Compressor,
    crossover: Crossover,
    phase: PhaseAlignment,
    limiter: Limiter,

    // Composite-facing shadow values kept so `get_param` reports exactly what was
    // set (the units differ from what the delegate effect stores).
    comp_threshold_lin: f32,
    crossover_freq: f32,
    delays_ms: [f32; 3],
    invert: [bool; 3],

    // Crossover-chain scratch (grown off the hot path).
    temp_l: Vec<f32>,
    temp_r: Vec<f32>,
    sub_l: Vec<f32>,
    sub_r: Vec<f32>,
    mono_sub: Vec<f32>,
}

impl SmartMaster {
    /// Creates a SmartMaster chain at the given sample rate, initialised to the
    /// reference `SmartMasterConfig` defaults (subharmonic and compressor off,
    /// 80 Hz crossover, flat EQ, no phase alignment). The managed control plane
    /// overwrites every parameter on its first control-rate sync.
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

        Self {
            enabled: true,
            mix: 1.0,
            eq: Equalizer30::new(sample_rate),
            subharmonic: SubharmonicSynth::new(sample_rate),
            compressor,
            crossover: Crossover::new(sample_rate, 80.0),
            phase: PhaseAlignment::new(sample_rate),
            limiter,
            comp_threshold_lin: 0.5,
            crossover_freq: 80.0,
            delays_ms: [0.0; 3],
            invert: [false; 3],
            temp_l: vec![0.0; INITIAL_SCRATCH_FRAMES],
            temp_r: vec![0.0; INITIAL_SCRATCH_FRAMES],
            sub_l: vec![0.0; INITIAL_SCRATCH_FRAMES],
            sub_r: vec![0.0; INITIAL_SCRATCH_FRAMES],
            mono_sub: vec![0.0; INITIAL_SCRATCH_FRAMES],
        }
    }

    /// Whether the crossover / phase-alignment stage engages this block.
    fn needs_phase_alignment(&self) -> bool {
        self.delays_ms.iter().any(|d| d.abs() > PHASE_DELAY_EPS_MS)
            || self.invert.iter().any(|&b| b)
    }

    /// Ensures the crossover scratch holds at least `frames` samples per channel.
    fn ensure_scratch(&mut self, frames: usize) {
        if self.temp_l.len() < frames {
            self.temp_l.resize(frames, 0.0);
            self.temp_r.resize(frames, 0.0);
            self.sub_l.resize(frames, 0.0);
            self.sub_r.resize(frames, 0.0);
            self.mono_sub.resize(frames, 0.0);
        }
    }

    /// Crossover → phase-alignment → recombination → limiter, mirroring the
    /// reference `ProcessCrossoverChain`. Splits each channel into a high (L/R)
    /// and a low (sub) band, sums the sub bands to mono, applies per-channel
    /// delay/inversion, recombines, then limits.
    fn process_crossover_chain(&mut self, buffer: &mut [f32], ch: usize, frames: usize) {
        self.ensure_scratch(frames);

        // Deinterleave the first (up to) two channels; a mono stream duplicates
        // its single channel into R, matching the reference.
        for f in 0..frames {
            self.temp_l[f] = buffer[f * ch];
            self.temp_r[f] = if ch > 1 {
                buffer[f * ch + 1]
            } else {
                self.temp_l[f]
            };
        }

        // Split each channel: high band overwrites temp_*, low band into sub_*.
        self.crossover
            .process_channel(&mut self.temp_l[..frames], &mut self.sub_l[..frames], 0);
        self.crossover
            .process_channel(&mut self.temp_r[..frames], &mut self.sub_r[..frames], 1);

        // Sum the two low bands to a mono sub.
        for f in 0..frames {
            self.mono_sub[f] = (self.sub_l[f] + self.sub_r[f]) * 0.5;
        }

        // Per-channel delay + inversion on L, R and the mono sub.
        self.phase.process_channel(&mut self.temp_l[..frames], 0);
        self.phase.process_channel(&mut self.temp_r[..frames], 1);
        self.phase.process_channel(&mut self.mono_sub[..frames], 2);

        // Recombine high + sub back into the interleaved buffer. Channels beyond
        // the first two are left untouched by the crossover stage (as in the
        // reference), then the limiter runs across all of them.
        for f in 0..frames {
            buffer[f * ch] = self.temp_l[f] + self.mono_sub[f];
            if ch > 1 {
                buffer[f * ch + 1] = self.temp_r[f] + self.mono_sub[f];
            }
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

        // 1. Graphic EQ.
        self.eq.process(buffer, channels);

        // 2. Subharmonic synthesis (self-gates on enabled/mix).
        if self.subharmonic.enabled() && self.subharmonic.mix() > 0.0 {
            self.subharmonic.process(buffer, channels);
        }

        // 3. Compressor (self-skips when disabled).
        self.compressor.process(buffer, channels);

        // 4. Crossover / phase alignment (only when requested) + limiter.
        if self.needs_phase_alignment() {
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
                // Inert: stored so get_param round-trips, but never applied.
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
                true
            }
            PARAM_COMP_RATIO => self.compressor.set_param(compressor::PARAM_RATIO, value),
            PARAM_COMP_ATTACK => self.compressor.set_param(compressor::PARAM_ATTACK, value),
            PARAM_COMP_RELEASE => self.compressor.set_param(compressor::PARAM_RELEASE, value),
            PARAM_CROSSOVER_FREQ => {
                self.crossover_freq = value.max(1.0);
                self.crossover.set_frequency(self.crossover_freq);
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
            PARAM_LIMIT_THRESHOLD => self.limiter.set_param(limiter::PARAM_THRESHOLD, value),
            PARAM_LIMIT_CEILING => self.limiter.set_param(limiter::PARAM_CEILING, value),
            PARAM_LIMIT_RELEASE => self.limiter.set_param(limiter::PARAM_RELEASE, value),
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
            PARAM_CROSSOVER_FREQ => Some(self.crossover_freq),
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
            PARAM_LIMIT_THRESHOLD => self.limiter.get_param(limiter::PARAM_THRESHOLD),
            PARAM_LIMIT_CEILING => self.limiter.get_param(limiter::PARAM_CEILING),
            PARAM_LIMIT_RELEASE => self.limiter.get_param(limiter::PARAM_RELEASE),
            _ => None,
        }
    }

    fn reset(&mut self) {
        self.eq.reset();
        self.subharmonic.reset();
        self.compressor.reset();
        self.crossover.reset();
        self.phase.reset();
        self.limiter.reset();
        self.temp_l.iter_mut().for_each(|s| *s = 0.0);
        self.temp_r.iter_mut().for_each(|s| *s = 0.0);
        self.sub_l.iter_mut().for_each(|s| *s = 0.0);
        self.sub_r.iter_mut().for_each(|s| *s = 0.0);
        self.mono_sub.iter_mut().for_each(|s| *s = 0.0);
    }

    fn is_enabled(&self) -> bool {
        self.enabled
    }

    fn set_enabled(&mut self, enabled: bool) {
        self.enabled = enabled;
    }

    fn latency_samples(&self) -> u32 {
        // The look-ahead limiter is the only latency source, matching the
        // reference `LatencySamples => LimiterLatencySamples`.
        self.limiter.latency_samples()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn stereo_tone(freq: f32, amp: f32, frames: usize) -> Vec<f32> {
        let mut out = Vec::with_capacity(frames * 2);
        for i in 0..frames {
            let v = amp * (2.0 * std::f32::consts::PI * freq * i as f32 / 48_000.0).sin();
            out.push(v);
            out.push(v);
        }
        out
    }

    #[test]
    fn effect_type_is_smartmaster() {
        let sm = SmartMaster::new(48_000.0);
        assert_eq!(sm.effect_type(), EffectType::SmartMaster);
    }

    #[test]
    fn disabled_passes_signal_through() {
        let mut sm = SmartMaster::new(48_000.0);
        sm.set_param(PARAM_ENABLED, 0.0);
        let input = stereo_tone(200.0, 0.3, 512);
        let mut buf = input.clone();
        sm.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn default_chain_limits_but_keeps_output_finite() {
        // With EQ flat, subharmonic + compressor off and no phase alignment, the
        // signal still passes through the limiter; output stays finite and within
        // the ceiling.
        let mut sm = SmartMaster::new(48_000.0);
        let mut buf = stereo_tone(220.0, 0.9, 8_192);
        sm.process(&mut buf, 2);
        assert!(buf.iter().all(|s| s.is_finite()));
        // Default ceiling -0.1 dBFS ≈ 0.9886 linear.
        let ceiling = 10.0f32.powf(-0.1 / 20.0) + 1.0e-4;
        assert!(buf.iter().all(|&s| s.abs() <= ceiling));
    }

    #[test]
    fn param_roundtrip_across_all_ids() {
        let mut sm = SmartMaster::new(48_000.0);
        // A representative value on every id, then read it back.
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
        sm.set_param(PARAM_CROSSOVER_FREQ, 90.0);
        sm.set_param(PARAM_DELAY_L, 1.5);
        sm.set_param(PARAM_INVERT_SUB, 1.0);
        sm.set_param(PARAM_LIMIT_THRESHOLD, -1.0);
        sm.set_param(PARAM_LIMIT_CEILING, -0.3);
        sm.set_param(PARAM_LIMIT_RELEASE, 80.0);

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
        assert_eq!(sm.get_param(PARAM_CROSSOVER_FREQ), Some(90.0));
        assert_eq!(sm.get_param(PARAM_DELAY_L), Some(1.5));
        assert_eq!(sm.get_param(PARAM_INVERT_SUB), Some(1.0));
        assert_eq!(sm.get_param(PARAM_LIMIT_THRESHOLD), Some(-1.0));
        assert_eq!(sm.get_param(PARAM_LIMIT_CEILING), Some(-0.3));
        assert_eq!(sm.get_param(PARAM_LIMIT_RELEASE), Some(80.0));
    }

    #[test]
    fn every_seeded_param_is_below_probe_bound() {
        // The control-side shadow probes ids 0..64; every SmartMaster parameter
        // must fall in that window or it could not be set/read from C#.
        let sm = SmartMaster::new(48_000.0);
        for id in 0..64u32 {
            let _ = sm.get_param(id); // must not panic
        }
        assert!(sm.get_param(PARAM_LIMIT_RELEASE).is_some());
        assert_eq!(sm.get_param(49), None);
    }

    #[test]
    fn phase_alignment_engages_and_stays_finite() {
        let mut sm = SmartMaster::new(48_000.0);
        sm.set_param(PARAM_DELAY_L, 0.5); // engages the crossover chain
        sm.set_param(PARAM_INVERT_SUB, 1.0);
        let mut buf = stereo_tone(120.0, 0.6, 4_096);
        sm.process(&mut buf, 2);
        assert!(buf.iter().all(|s| s.is_finite()));
    }

    #[test]
    fn unknown_param_is_rejected() {
        let mut sm = SmartMaster::new(48_000.0);
        assert!(!sm.set_param(999, 1.0));
        assert_eq!(sm.get_param(999), None);
    }

    #[test]
    fn reset_restores_reproducibility() {
        let mut sm = SmartMaster::new(48_000.0);
        sm.set_param(PARAM_BAND_10, 8.0);
        sm.set_param(PARAM_COMP_ENABLED, 1.0);
        let input = stereo_tone(300.0, 0.5, 2_048);

        let mut first = input.clone();
        sm.process(&mut first, 2);
        sm.reset();
        let mut second = input.clone();
        sm.process(&mut second, 2);
        assert_eq!(first, second);
    }

    const PARAM_BAND_10: u32 = PARAM_BAND_0 + 10;
}
