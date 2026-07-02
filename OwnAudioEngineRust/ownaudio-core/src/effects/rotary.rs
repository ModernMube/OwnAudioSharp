//! Rotary / Leslie-cabinet speaker simulator.
//!
//! Faithful Rust port of the reference C# `OwnaudioNET.Effects.RotaryEffect`: the
//! input is split by an 800 Hz one-pole crossover; the high band feeds a horn
//! delay line and the low band a rotor delay line, each read back at an integer
//! tap swept by its own LFO (horn ≈ 6 Hz, rotor ≈ 1 Hz) and amplitude-modulated
//! in sympathy to fake the rotating-speaker Doppler and tremolo.  A fast/slow
//! switch multiplies the horn speed by 3 and the rotor speed by 2.  A single set
//! of state is shared across the interleaved samples exactly as the C# effect
//! walks them.  Parameter identifiers, ranges, defaults and the per-sample DSP
//! mirror the C# effect so the two are numerically equivalent (the basis of the
//! 2.2 reference comparison).
//!
//! The crossover low-pass states are recursive, so they are denormal-flushed
//! (2.12): a decaying tail would otherwise park in the subnormal range and stall
//! the CPU on the audio thread.  The delay lines are pre-allocated during
//! construction and never reallocated on the audio thread.

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};
use crate::denormal;
use crate::smoothing::{RampedParam, DEFAULT_SMOOTH_MS};

/// Param ID 2 — horn rotation speed in Hz (2.0 … 15.0).
pub const PARAM_HORN_SPEED: u32 = 2;
/// Param ID 3 — rotor rotation speed in Hz (0.5 … 5.0).
pub const PARAM_ROTOR_SPEED: u32 = 3;
/// Param ID 4 — effect intensity (0.0 … 1.0).
pub const PARAM_INTENSITY: u32 = 4;
/// Param ID 5 — fast/slow switch (0.0 = slow, 1.0 = fast).
pub const PARAM_IS_FAST: u32 = 5;

/// Crossover frequency between the rotor (low) and horn (high) bands.
const CROSSOVER_HZ: f32 = 800.0;

/// Mix values below this threshold bypass processing entirely (mirrors C#).
const MIX_BYPASS_THRESHOLD: f32 = 0.001;

/// One-pole low-pass with a leaky-integrator topology, matching the reference
/// `LowPassFilter` (`state += cutoff·(input − state)`); denormal-flushed.
#[derive(Clone, Copy)]
struct OnePoleLowPass {
    cutoff: f32,
    state: f32,
}

impl OnePoleLowPass {
    fn new(cutoff_hz: f32, sample_rate: f32) -> Self {
        Self {
            cutoff: 2.0 * std::f32::consts::PI * cutoff_hz / sample_rate,
            state: 0.0,
        }
    }

    #[inline]
    fn process(&mut self, input: f32) -> f32 {
        self.state = denormal::flush(self.state + self.cutoff * (input - self.state));
        self.state
    }

    fn reset(&mut self) {
        self.state = 0.0;
    }
}

/// Rotary/Leslie cabinet simulator.
pub struct Rotary {
    enabled: bool,
    mix: f32,
    horn_speed: f32,
    rotor_speed: f32,
    intensity: f32,
    is_fast: bool,
    sample_rate: f32,

    horn_delay_buffer: Vec<f32>,
    rotor_delay_buffer: Vec<f32>,
    horn_buffer_index: usize,
    rotor_buffer_index: usize,
    horn_phase: f32,
    rotor_phase: f32,

    low_pass: OnePoleLowPass,
    high_pass_low: OnePoleLowPass,
    mix_ramp: RampedParam,
}

impl Rotary {
    /// Creates a new [`Rotary`] sized for `sample_rate`, with the reference
    /// default parameters (horn 6 Hz, rotor 1 Hz, intensity 0.7, mix 1.0, slow).
    pub fn new(sample_rate: f32) -> Self {
        let sample_rate = if sample_rate > 0.0 { sample_rate } else { 44_100.0 };
        // 10 ms delay line per rotor, covering the modulated taps.
        let max_delay = (0.01 * sample_rate) as usize;
        Self {
            enabled: true,
            mix: 1.0,
            horn_speed: 6.0,
            rotor_speed: 1.0,
            intensity: 0.7,
            is_fast: false,
            sample_rate,
            horn_delay_buffer: vec![0.0; max_delay.max(2)],
            rotor_delay_buffer: vec![0.0; max_delay.max(2)],
            horn_buffer_index: 0,
            rotor_buffer_index: 0,
            horn_phase: 0.0,
            rotor_phase: 0.0,
            low_pass: OnePoleLowPass::new(CROSSOVER_HZ, sample_rate),
            high_pass_low: OnePoleLowPass::new(CROSSOVER_HZ, sample_rate),
            mix_ramp: RampedParam::new(1.0, sample_rate, DEFAULT_SMOOTH_MS),
        }
    }
}

impl Effect for Rotary {
    fn effect_type(&self) -> EffectType {
        EffectType::Rotary
    }

    fn process(&mut self, buffer: &mut [f32], _channels: u16) {
        self.mix_ramp.begin_block();
        if !self.enabled
            || (self.mix < MIX_BYPASS_THRESHOLD && self.mix_ramp.current() < MIX_BYPASS_THRESHOLD)
        {
            return;
        }

        let two_pi = std::f32::consts::PI * 2.0;
        let intensity = self.intensity;

        let current_horn_speed = if self.is_fast { self.horn_speed * 3.0 } else { self.horn_speed };
        let current_rotor_speed = if self.is_fast { self.rotor_speed * 2.0 } else { self.rotor_speed };
        let horn_increment = two_pi * current_horn_speed / self.sample_rate;
        let rotor_increment = two_pi * current_rotor_speed / self.sample_rate;

        let horn_len = self.horn_delay_buffer.len();
        let rotor_len = self.rotor_delay_buffer.len();

        let mut horn_phase = self.horn_phase;
        let mut rotor_phase = self.rotor_phase;

        for sample in buffer.iter_mut() {
            let mix = self.mix_ramp.advance();
            let input = *sample;

            let low_freq = self.low_pass.process(input);
            let high_freq = input - self.high_pass_low.process(input);

            let horn_lfo = horn_phase.sin();
            let horn_delay = 0.001 + 0.003 * horn_lfo * intensity;
            let horn_delay_samples =
                ((horn_delay * self.sample_rate) as i64).clamp(1, horn_len as i64 - 1) as usize;
            let horn_read_index = (self.horn_buffer_index + horn_len - horn_delay_samples) % horn_len;
            let horn_output = self.horn_delay_buffer[horn_read_index] * (0.8 + 0.2 * horn_lfo * intensity);
            self.horn_delay_buffer[self.horn_buffer_index] = high_freq;

            let rotor_lfo = rotor_phase.sin();
            let rotor_delay = 0.002 + 0.004 * rotor_lfo * intensity;
            let rotor_delay_samples =
                ((rotor_delay * self.sample_rate) as i64).clamp(1, rotor_len as i64 - 1) as usize;
            let rotor_read_index = (self.rotor_buffer_index + rotor_len - rotor_delay_samples) % rotor_len;
            let rotor_output = self.rotor_delay_buffer[rotor_read_index] * (0.9 + 0.1 * rotor_lfo * intensity);
            self.rotor_delay_buffer[self.rotor_buffer_index] = low_freq;

            let processed = horn_output + rotor_output;
            *sample = input * (1.0 - mix) + processed * mix;

            self.horn_buffer_index = (self.horn_buffer_index + 1) % horn_len;
            self.rotor_buffer_index = (self.rotor_buffer_index + 1) % rotor_len;

            horn_phase += horn_increment;
            rotor_phase += rotor_increment;
            if horn_phase >= two_pi {
                horn_phase -= two_pi;
            }
            if rotor_phase >= two_pi {
                rotor_phase -= two_pi;
            }
        }

        self.horn_phase = horn_phase;
        self.rotor_phase = rotor_phase;
    }

    fn set_param(&mut self, param_id: u32, value: f32) -> bool {
        match param_id {
            PARAM_ENABLED => {
                self.enabled = value >= 0.5;
                true
            }
            PARAM_MIX => {
                self.mix = value.clamp(0.0, 1.0);
                self.mix_ramp.set(self.mix);
                true
            }
            PARAM_HORN_SPEED => {
                self.horn_speed = value.clamp(2.0, 15.0);
                true
            }
            PARAM_ROTOR_SPEED => {
                self.rotor_speed = value.clamp(0.5, 5.0);
                true
            }
            PARAM_INTENSITY => {
                self.intensity = value.clamp(0.0, 1.0);
                true
            }
            PARAM_IS_FAST => {
                self.is_fast = value >= 0.5;
                true
            }
            _ => false,
        }
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        match param_id {
            PARAM_ENABLED => Some(if self.enabled { 1.0 } else { 0.0 }),
            PARAM_MIX => Some(self.mix),
            PARAM_HORN_SPEED => Some(self.horn_speed),
            PARAM_ROTOR_SPEED => Some(self.rotor_speed),
            PARAM_INTENSITY => Some(self.intensity),
            PARAM_IS_FAST => Some(if self.is_fast { 1.0 } else { 0.0 }),
            _ => None,
        }
    }

    fn reset(&mut self) {
        self.horn_delay_buffer.iter_mut().for_each(|s| *s = 0.0);
        self.rotor_delay_buffer.iter_mut().for_each(|s| *s = 0.0);
        self.horn_buffer_index = 0;
        self.rotor_buffer_index = 0;
        self.horn_phase = 0.0;
        self.rotor_phase = 0.0;
        self.low_pass.reset();
        self.high_pass_low.reset();
        self.mix_ramp.reset(self.mix);
    }

    fn is_enabled(&self) -> bool {
        self.enabled
    }

    fn set_enabled(&mut self, enabled: bool) {
        self.enabled = enabled;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Ground-truth transcription of the reference C# rotary.
    ///
    /// As in the `Flanger`, the two delay taps are *discrete* integer decisions
    /// (`(delay·sample_rate) as usize`), so the LFO phases that select them are
    /// carried in f32 bit-identically to production; the crossover filtering,
    /// amplitude modulation and mix — the smooth quantities the -60 dB bound
    /// measures — are elevated to f64.
    struct Reference {
        sample_rate: f32,
        intensity: f32,
        mix: f64,
        horn_speed: f32,
        rotor_speed: f32,
        is_fast: bool,
        horn_buf: Vec<f64>,
        rotor_buf: Vec<f64>,
        horn_index: usize,
        rotor_index: usize,
        horn_phase: f32,
        rotor_phase: f32,
        lp_cutoff: f64,
        lp_state: f64,
        hp_lp_cutoff: f64,
        hp_lp_state: f64,
    }

    impl Reference {
        fn new(sample_rate: f32, horn_speed: f32, rotor_speed: f32, intensity: f32, mix: f64, is_fast: bool) -> Self {
            let max_delay = (0.01 * sample_rate) as usize;
            let cutoff = 2.0 * std::f64::consts::PI * CROSSOVER_HZ as f64 / sample_rate as f64;
            Self {
                sample_rate,
                intensity,
                mix,
                horn_speed,
                rotor_speed,
                is_fast,
                horn_buf: vec![0.0; max_delay.max(2)],
                rotor_buf: vec![0.0; max_delay.max(2)],
                horn_index: 0,
                rotor_index: 0,
                horn_phase: 0.0,
                rotor_phase: 0.0,
                lp_cutoff: cutoff,
                lp_state: 0.0,
                hp_lp_cutoff: cutoff,
                hp_lp_state: 0.0,
            }
        }

        fn process(&mut self, buffer: &[f32]) -> Vec<f32> {
            let mut out = vec![0.0f32; buffer.len()];
            let two_pi_f32 = std::f32::consts::PI * 2.0;
            let intensity = self.intensity as f64;

            let horn_speed = if self.is_fast { self.horn_speed * 3.0 } else { self.horn_speed };
            let rotor_speed = if self.is_fast { self.rotor_speed * 2.0 } else { self.rotor_speed };
            let horn_increment = two_pi_f32 * horn_speed / self.sample_rate;
            let rotor_increment = two_pi_f32 * rotor_speed / self.sample_rate;

            let horn_len = self.horn_buf.len();
            let rotor_len = self.rotor_buf.len();

            for (o, &x) in out.iter_mut().zip(buffer.iter()) {
                let input = x as f64;

                self.lp_state += self.lp_cutoff * (input - self.lp_state);
                let low_freq = self.lp_state;
                self.hp_lp_state += self.hp_lp_cutoff * (input - self.hp_lp_state);
                let high_freq = input - self.hp_lp_state;

                // Discrete tap decisions — identical f32 path to production.
                let horn_lfo_f32 = self.horn_phase.sin();
                let horn_delay_f32 = 0.001 + 0.003 * horn_lfo_f32 * self.intensity;
                let horn_delay_samples =
                    ((horn_delay_f32 * self.sample_rate) as i64).clamp(1, horn_len as i64 - 1) as usize;
                let rotor_lfo_f32 = self.rotor_phase.sin();
                let rotor_delay_f32 = 0.002 + 0.004 * rotor_lfo_f32 * self.intensity;
                let rotor_delay_samples =
                    ((rotor_delay_f32 * self.sample_rate) as i64).clamp(1, rotor_len as i64 - 1) as usize;

                let horn_read = (self.horn_index + horn_len - horn_delay_samples) % horn_len;
                let horn_output = self.horn_buf[horn_read] * (0.8 + 0.2 * horn_lfo_f32 as f64 * intensity);
                self.horn_buf[self.horn_index] = high_freq;

                let rotor_read = (self.rotor_index + rotor_len - rotor_delay_samples) % rotor_len;
                let rotor_output = self.rotor_buf[rotor_read] * (0.9 + 0.1 * rotor_lfo_f32 as f64 * intensity);
                self.rotor_buf[self.rotor_index] = low_freq;

                let processed = horn_output + rotor_output;
                *o = (input * (1.0 - self.mix) + processed * self.mix) as f32;

                self.horn_index = (self.horn_index + 1) % horn_len;
                self.rotor_index = (self.rotor_index + 1) % rotor_len;
                self.horn_phase += horn_increment;
                self.rotor_phase += rotor_increment;
                if self.horn_phase >= two_pi_f32 {
                    self.horn_phase -= two_pi_f32;
                }
                if self.rotor_phase >= two_pi_f32 {
                    self.rotor_phase -= two_pi_f32;
                }
            }
            out
        }
    }

    fn rms_error_db(a: &[f32], b: &[f32]) -> f64 {
        assert_eq!(a.len(), b.len());
        let mut sum_sq = 0.0f64;
        for (x, y) in a.iter().zip(b.iter()) {
            let d = *x as f64 - *y as f64;
            sum_sq += d * d;
        }
        let rms = (sum_sq / a.len() as f64).sqrt();
        if rms == 0.0 {
            f64::NEG_INFINITY
        } else {
            20.0 * rms.log10()
        }
    }

    fn stereo_pluck(frames: usize) -> Vec<f32> {
        let mut v = vec![0.0f32; frames * 2];
        for f in 0..frames {
            let t = f as f32 / 48_000.0;
            let env = (-2.0 * t).exp();
            let s = 0.6 * env * (2.0 * std::f32::consts::PI * 330.0 * t).sin();
            v[f * 2] = s;
            v[f * 2 + 1] = s * 0.8;
        }
        v
    }

    #[test]
    fn defaults_match_reference() {
        let r = Rotary::new(48_000.0);
        assert_eq!(r.get_param(PARAM_HORN_SPEED), Some(6.0));
        assert_eq!(r.get_param(PARAM_ROTOR_SPEED), Some(1.0));
        assert_eq!(r.get_param(PARAM_INTENSITY), Some(0.7));
        assert_eq!(r.get_param(PARAM_MIX), Some(1.0));
        assert_eq!(r.get_param(PARAM_IS_FAST), Some(0.0));
        assert!(r.is_enabled());
    }

    #[test]
    fn params_clamp_to_reference_ranges() {
        let mut r = Rotary::new(48_000.0);
        r.set_param(PARAM_HORN_SPEED, 100.0);
        assert_eq!(r.get_param(PARAM_HORN_SPEED), Some(15.0));
        r.set_param(PARAM_HORN_SPEED, 0.0);
        assert_eq!(r.get_param(PARAM_HORN_SPEED), Some(2.0));
        r.set_param(PARAM_ROTOR_SPEED, 100.0);
        assert_eq!(r.get_param(PARAM_ROTOR_SPEED), Some(5.0));
        r.set_param(PARAM_ROTOR_SPEED, 0.0);
        assert_eq!(r.get_param(PARAM_ROTOR_SPEED), Some(0.5));
        r.set_param(PARAM_INTENSITY, 5.0);
        assert_eq!(r.get_param(PARAM_INTENSITY), Some(1.0));
        r.set_param(PARAM_MIX, 2.0);
        assert_eq!(r.get_param(PARAM_MIX), Some(1.0));
    }

    #[test]
    fn unknown_param_is_rejected() {
        let mut r = Rotary::new(48_000.0);
        assert!(!r.set_param(999, 1.0));
        assert_eq!(r.get_param(999), None);
    }

    #[test]
    fn disabled_effect_passes_signal_through_untouched() {
        let mut r = Rotary::new(48_000.0);
        r.set_enabled(false);
        let input = stereo_pluck(256);
        let mut buf = input.clone();
        r.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn zero_mix_passes_signal_through_untouched() {
        let mut r = Rotary::new(48_000.0);
        r.set_param(PARAM_MIX, 0.0);
        let input = stereo_pluck(256);
        let mut buf = input.clone();
        r.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn output_is_finite_and_length_preserved() {
        let mut r = Rotary::new(48_000.0);
        let input = stereo_pluck(2_048);
        let mut buf = input.clone();
        r.process(&mut buf, 2);
        assert_eq!(buf.len(), input.len());
        assert!(buf.iter().all(|s| s.is_finite()));
    }

    #[test]
    fn matches_reference_dsp_within_minus_60_db() {
        // 2.2 acceptance: the production f32 rotary must reproduce the ground
        // truth (transcribed from the C# algorithm, carrying the full delay lines
        // and crossover state) to better than -60 dB RMS error across the
        // parameter space.  The discrete integer taps are selected by an
        // f32-identical path; production also flushes the crossover state out of
        // the subnormal range, which differs from the reference only at subnormal
        // magnitudes, far below the -60 dB bound.
        let input = stereo_pluck(8_192);
        for &(horn, rotor, intensity, mix, fast) in &[
            (6.0f64, 1.0f64, 0.7f64, 1.0f64, false),
            (6.7, 1.2, 0.75, 1.0, false),
            (5.0, 1.5, 1.0, 1.0, true),
            (4.0, 0.7, 0.40, 0.60, false),
        ] {
            let mut r = Rotary::new(48_000.0);
            r.set_param(PARAM_HORN_SPEED, horn as f32);
            r.set_param(PARAM_ROTOR_SPEED, rotor as f32);
            r.set_param(PARAM_INTENSITY, intensity as f32);
            r.set_param(PARAM_MIX, mix as f32);
            r.set_param(PARAM_IS_FAST, if fast { 1.0 } else { 0.0 });

            let mut produced = input.clone();
            r.process(&mut produced, 2);

            let mut reference =
                Reference::new(48_000.0, horn as f32, rotor as f32, intensity as f32, mix, fast);
            let expected = reference.process(&input);
            let err = rms_error_db(&produced, &expected);
            assert!(
                err < -60.0,
                "horn={horn} rotor={rotor} intensity={intensity} mix={mix} fast={fast}: RMS error {err:.1} dB exceeds -60 dB"
            );
        }
    }

    #[test]
    fn reset_clears_state() {
        let mut r = Rotary::new(48_000.0);
        let input = stereo_pluck(512);
        let mut first = input.clone();
        r.process(&mut first, 2);
        r.reset();
        let mut second = input.clone();
        r.process(&mut second, 2);
        assert_eq!(first, second);
    }

    #[test]
    fn decaying_tail_does_not_produce_subnormals() {
        // A single impulse then a long silent tail: the denormal flush on the
        // crossover low-pass states must keep the recursive state either normal
        // or exactly zero.
        let mut r = Rotary::new(48_000.0);
        r.set_param(PARAM_MIX, 1.0);
        let mut impulse = vec![0.0f32; 2];
        impulse[0] = 1.0;
        impulse[1] = 1.0;
        r.process(&mut impulse, 2);
        let mut silence = vec![0.0f32; 2 * 200_000];
        r.process(&mut silence, 2);
        assert!(
            r.low_pass.state == 0.0 || r.low_pass.state.abs() >= f32::MIN_POSITIVE,
            "subnormal in low-pass state: {:e}",
            r.low_pass.state
        );
        assert!(
            r.high_pass_low.state == 0.0 || r.high_pass_low.state.abs() >= f32::MIN_POSITIVE,
            "subnormal in high-pass state: {:e}",
            r.high_pass_low.state
        );
    }
}
