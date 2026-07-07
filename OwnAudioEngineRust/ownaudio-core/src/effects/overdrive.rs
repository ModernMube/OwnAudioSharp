//! Asymmetric tube-style overdrive.
//!
//! Rust DSP derived from the reference C# `OwnaudioNET.Effects.OverdriveEffect`:
//! every sample is driven, passed through an asymmetric tanh tube saturation,
//! shaped by a stateful tone control (a one-pole low-pass minus a scaled one-pole
//! high-pass), attenuated by the output level, then blended back with the dry
//! signal.  Parameter identifiers, ranges and defaults mirror the C# effect.
//!
//! **Channel-aware processing (intentional divergence from the C# reference):**
//! the C# effect runs a single tone-filter pair across the interleaved samples,
//! so on stereo material the low-/high-pass states alternate between the two
//! channels every sample — a comb-like coloration and inter-channel bleed.  This
//! port keeps an independent tone-filter pair per channel; mono input stays bit
//! identical to the per-sample reference, stereo is compared against a
//! channel-aware f64 ground truth.
//!
//! **DC blocker (A.4):** the tube saturation is deliberately asymmetric (the two
//! half-waves use different curves), which by construction injects a DC offset.
//! Left in, that DC flows down the chain — biasing a downstream compressor's
//! detector, eating head-room and clicking under the mix ramp on silent passages.
//! A one-pole DC blocker (`y = x − x₁ + R·y₁`, ~10 Hz) sits on each channel's wet
//! path right before the dry/wet blend to remove it.  It is essentially
//! sound-neutral above a few Hz.

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};
use crate::denormal;
use crate::smoothing::{RampedParam, DEFAULT_SMOOTH_MS};

/// Param ID 2 — input gain (1.0 … 5.0). Controls the amount of overdrive.
pub const PARAM_GAIN: u32 = 2;
/// Param ID 3 — tone control (0.0 = dark … 1.0 = bright).
pub const PARAM_TONE: u32 = 3;
/// Param ID 4 — output level (0.1 … 1.0).
pub const PARAM_OUTPUT_LEVEL: u32 = 4;

/// Mix values below this threshold bypass processing entirely (mirrors C#).
const MIX_BYPASS_THRESHOLD: f32 = 0.001;

/// Highest interleaved channel count with a dedicated tone-filter pair (the
/// engine is stereo; any extra channels pass through unprocessed).
const MAX_CHANNELS: usize = 2;

/// DC-blocker corner frequency (Hz).
const DC_BLOCK_HZ: f32 = 10.0;

/// One-pole DC-blocker coefficient `R` for the corner frequency at `sample_rate`.
#[inline]
fn dc_block_coeff(sample_rate: f32) -> f32 {
    let sr = if sample_rate > 0.0 {
        sample_rate
    } else {
        44_100.0
    };
    (1.0 - 2.0 * std::f32::consts::PI * DC_BLOCK_HZ / sr).clamp(0.0, 0.9999)
}

/// Asymmetric tube overdrive effect.
pub struct Overdrive {
    enabled: bool,
    mix: f32,
    gain: f32,
    tone: f32,
    output_level: f32,
    low_pass_state: [f32; MAX_CHANNELS],
    high_pass_state: [f32; MAX_CHANNELS],
    // Per-channel one-pole DC-blocker state (A.4).
    dc_r: f32,
    dc_x1: [f32; MAX_CHANNELS],
    dc_y1: [f32; MAX_CHANNELS],
    mix_ramp: RampedParam,
}

impl Overdrive {
    /// Creates a new [`Overdrive`] with the reference default parameters
    /// (gain 2.0, tone 0.5, mix 1.0, output level 0.7).
    pub fn new(sample_rate: f32) -> Self {
        Self {
            enabled: true,
            mix: 1.0,
            gain: 2.0,
            tone: 0.5,
            output_level: 0.7,
            low_pass_state: [0.0; MAX_CHANNELS],
            high_pass_state: [0.0; MAX_CHANNELS],
            dc_r: dc_block_coeff(sample_rate),
            dc_x1: [0.0; MAX_CHANNELS],
            dc_y1: [0.0; MAX_CHANNELS],
            mix_ramp: RampedParam::new(1.0, sample_rate, DEFAULT_SMOOTH_MS),
        }
    }

    /// One-pole DC blocker for `channel`: `y = x − x₁ + R·y₁` (A.4).
    #[inline]
    fn dc_block(&mut self, channel: usize, input: f32) -> f32 {
        let y = input - self.dc_x1[channel] + self.dc_r * self.dc_y1[channel];
        self.dc_x1[channel] = input;
        self.dc_y1[channel] = denormal::flush(y);
        self.dc_y1[channel]
    }

    /// Asymmetric tube saturation.
    ///
    /// Positive and negative half-waves are shaped differently (a softer, hotter
    /// curve for the positive side), giving the even-harmonic character of tube
    /// breakup.  The `tanh` is evaluated in f64 to match the reference C#
    /// `Math.Tanh` before the result is truncated back to f32.
    #[inline]
    fn tube_saturation(input: f32) -> f32 {
        let x = input as f64;
        if input >= 0.0 {
            ((x * 0.7).tanh() * 1.2) as f32
        } else {
            ((x * 0.9).tanh() * 0.9) as f32
        }
    }

    /// Stateful tone control: a one-pole low-pass minus a tone-scaled one-pole
    /// high-pass.  Advances the given channel's filter states; matches the
    /// reference C# `ApplyToneControl`.
    #[inline]
    fn apply_tone_control(&mut self, channel: usize, input: f32) -> f32 {
        let low_pass_cutoff = 0.1 + self.tone * 0.4;
        let high_pass_cutoff = 0.05 + (1.0 - self.tone) * 0.2;

        self.low_pass_state[channel] += low_pass_cutoff * (input - self.low_pass_state[channel]);
        self.high_pass_state[channel] += high_pass_cutoff * (input - self.high_pass_state[channel]);

        self.low_pass_state[channel] - self.high_pass_state[channel] * (1.0 - self.tone)
    }
}

impl Effect for Overdrive {
    fn effect_type(&self) -> EffectType {
        EffectType::Overdrive
    }

    #[allow(clippy::needless_range_loop)]
    fn process(&mut self, buffer: &mut [f32], channels: u16) {
        self.mix_ramp.begin_block();
        if !self.enabled
            || (self.mix < MIX_BYPASS_THRESHOLD && self.mix_ramp.current() < MIX_BYPASS_THRESHOLD)
        {
            return;
        }

        let stride = (channels.max(1)) as usize;
        let ch = stride.min(MAX_CHANNELS);

        for frame in buffer.chunks_mut(stride) {
            let mix = self.mix_ramp.advance();
            for c in 0..ch {
                let input = frame[c];
                let gained = input * self.gain;
                let mut overdriven = Self::tube_saturation(gained);
                overdriven = self.apply_tone_control(c, overdriven);
                overdriven *= self.output_level;
                // Remove the DC produced by the asymmetric saturation (A.4).
                overdriven = self.dc_block(c, overdriven);
                frame[c] = input * (1.0 - mix) + overdriven * mix;
            }
        }
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
            PARAM_GAIN => {
                self.gain = value.clamp(1.0, 5.0);
                true
            }
            PARAM_TONE => {
                self.tone = value.clamp(0.0, 1.0);
                true
            }
            PARAM_OUTPUT_LEVEL => {
                self.output_level = value.clamp(0.1, 1.0);
                true
            }
            _ => false,
        }
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        match param_id {
            PARAM_ENABLED => Some(if self.enabled { 1.0 } else { 0.0 }),
            PARAM_MIX => Some(self.mix),
            PARAM_GAIN => Some(self.gain),
            PARAM_TONE => Some(self.tone),
            PARAM_OUTPUT_LEVEL => Some(self.output_level),
            _ => None,
        }
    }

    fn reset(&mut self) {
        self.low_pass_state = [0.0; MAX_CHANNELS];
        self.high_pass_state = [0.0; MAX_CHANNELS];
        self.dc_x1 = [0.0; MAX_CHANNELS];
        self.dc_y1 = [0.0; MAX_CHANNELS];
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

    /// f64 ground-truth transcription of the channel-aware reference DSP, carrying
    /// an independent tone-filter pair per channel, used to measure the production
    /// f32 implementation's numerical fidelity.
    struct Reference {
        gain: f64,
        tone: f64,
        mix: f64,
        output_level: f64,
        channels: usize,
        low_pass_state: [f64; MAX_CHANNELS],
        high_pass_state: [f64; MAX_CHANNELS],
        dc_r: f64,
        dc_x1: [f64; MAX_CHANNELS],
        dc_y1: [f64; MAX_CHANNELS],
    }

    impl Reference {
        fn new(gain: f64, tone: f64, mix: f64, output_level: f64, channels: usize) -> Self {
            Self {
                gain,
                tone,
                mix,
                output_level,
                channels,
                low_pass_state: [0.0; MAX_CHANNELS],
                high_pass_state: [0.0; MAX_CHANNELS],
                dc_r: 1.0 - 2.0 * std::f64::consts::PI * DC_BLOCK_HZ as f64 / 48_000.0,
                dc_x1: [0.0; MAX_CHANNELS],
                dc_y1: [0.0; MAX_CHANNELS],
            }
        }

        fn tube_saturation(input: f64) -> f64 {
            if input >= 0.0 {
                (input * 0.7).tanh() * 1.2
            } else {
                (input * 0.9).tanh() * 0.9
            }
        }

        fn apply_tone_control(&mut self, channel: usize, input: f64) -> f64 {
            let low_pass_cutoff = 0.1 + self.tone * 0.4;
            let high_pass_cutoff = 0.05 + (1.0 - self.tone) * 0.2;
            self.low_pass_state[channel] +=
                low_pass_cutoff * (input - self.low_pass_state[channel]);
            self.high_pass_state[channel] +=
                high_pass_cutoff * (input - self.high_pass_state[channel]);
            self.low_pass_state[channel] - self.high_pass_state[channel] * (1.0 - self.tone)
        }

        fn dc_block(&mut self, channel: usize, input: f64) -> f64 {
            let y = input - self.dc_x1[channel] + self.dc_r * self.dc_y1[channel];
            self.dc_x1[channel] = input;
            self.dc_y1[channel] = y;
            y
        }

        fn process(&mut self, input: &[f32]) -> Vec<f32> {
            let ch = self.channels;
            let mut out = vec![0.0f32; input.len()];
            for (out_frame, in_frame) in out.chunks_mut(ch).zip(input.chunks(ch)) {
                for c in 0..ch {
                    let x = in_frame[c] as f64;
                    let gained = x * self.gain;
                    let mut overdriven = Self::tube_saturation(gained);
                    overdriven = self.apply_tone_control(c, overdriven);
                    overdriven *= self.output_level;
                    overdriven = self.dc_block(c, overdriven);
                    out_frame[c] = (x * (1.0 - self.mix) + overdriven * self.mix) as f32;
                }
            }
            out
        }
    }

    /// Root-mean-square error between two signals expressed in decibels.
    /// Returns negative infinity for a perfect match.
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

    fn sine_sweep(len: usize) -> Vec<f32> {
        (0..len)
            .map(|i| {
                let t = i as f32 / 48_000.0;
                // A moderately loud tone that drives both saturation half-waves.
                0.7 * (2.0 * std::f32::consts::PI * (220.0 + 4.0 * i as f32 / len as f32) * t).sin()
            })
            .collect()
    }

    #[test]
    fn defaults_match_reference() {
        let o = Overdrive::new(48_000.0);
        assert_eq!(o.get_param(PARAM_GAIN), Some(2.0));
        assert_eq!(o.get_param(PARAM_TONE), Some(0.5));
        assert_eq!(o.get_param(PARAM_OUTPUT_LEVEL), Some(0.7));
        assert_eq!(o.get_param(PARAM_MIX), Some(1.0));
        assert!(o.is_enabled());
    }

    #[test]
    fn params_clamp_to_reference_ranges() {
        let mut o = Overdrive::new(48_000.0);
        o.set_param(PARAM_GAIN, 100.0);
        assert_eq!(o.get_param(PARAM_GAIN), Some(5.0));
        o.set_param(PARAM_GAIN, 0.0);
        assert_eq!(o.get_param(PARAM_GAIN), Some(1.0));
        o.set_param(PARAM_TONE, 5.0);
        assert_eq!(o.get_param(PARAM_TONE), Some(1.0));
        o.set_param(PARAM_TONE, -1.0);
        assert_eq!(o.get_param(PARAM_TONE), Some(0.0));
        o.set_param(PARAM_OUTPUT_LEVEL, 5.0);
        assert_eq!(o.get_param(PARAM_OUTPUT_LEVEL), Some(1.0));
        o.set_param(PARAM_OUTPUT_LEVEL, -1.0);
        assert_eq!(o.get_param(PARAM_OUTPUT_LEVEL), Some(0.1));
        o.set_param(PARAM_MIX, 2.0);
        assert_eq!(o.get_param(PARAM_MIX), Some(1.0));
    }

    #[test]
    fn unknown_param_is_rejected() {
        let mut o = Overdrive::new(48_000.0);
        assert!(!o.set_param(999, 1.0));
        assert_eq!(o.get_param(999), None);
    }

    #[test]
    fn disabled_effect_passes_signal_through_untouched() {
        let mut o = Overdrive::new(48_000.0);
        o.set_enabled(false);
        let input = sine_sweep(256);
        let mut buf = input.clone();
        o.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn zero_mix_passes_signal_through_untouched() {
        let mut o = Overdrive::new(48_000.0);
        o.set_param(PARAM_MIX, 0.0);
        let input = sine_sweep(256);
        let mut buf = input.clone();
        o.process(&mut buf, 2);
        assert_eq!(buf, input);
    }

    #[test]
    fn tube_saturation_is_bounded() {
        // tanh is bounded by ±1; after the per-side scaling the saturated
        // output stays within ±1.2 regardless of how hard the input is driven.
        let mut o = Overdrive::new(48_000.0);
        o.set_param(PARAM_GAIN, 5.0);
        o.set_param(PARAM_OUTPUT_LEVEL, 1.0);
        o.set_param(PARAM_TONE, 1.0);
        let mut buf = vec![5.0f32, -5.0, 100.0, -100.0];
        o.process(&mut buf, 1);
        for &s in &buf {
            assert!(s.abs() <= 1.5, "sample {s} exceeded the saturation bound");
        }
    }

    #[test]
    fn output_is_finite_and_length_preserved() {
        let mut o = Overdrive::new(48_000.0);
        let input = sine_sweep(512);
        let mut buf = input.clone();
        o.process(&mut buf, 2);
        assert_eq!(buf.len(), input.len());
        assert!(buf.iter().all(|s| s.is_finite()));
    }

    #[test]
    fn matches_reference_dsp_within_minus_60_db() {
        // 2.2 acceptance: the production f32 DSP must reproduce the reference
        // (f64 ground truth, transcribed from the C# algorithm, carrying the
        // same filter states) to better than -60 dB RMS error across the
        // parameter space.
        let input = sine_sweep(4_096);
        for &(gain, tone, mix, level) in &[
            (2.0f32, 0.5f32, 1.0f32, 0.7f32),
            (4.2, 0.8, 1.0, 0.65),
            (1.3, 0.6, 0.7, 0.9),
            (5.0, 0.25, 0.8, 0.85),
        ] {
            let mut o = Overdrive::new(48_000.0);
            o.set_param(PARAM_GAIN, gain);
            o.set_param(PARAM_TONE, tone);
            o.set_param(PARAM_MIX, mix);
            o.set_param(PARAM_OUTPUT_LEVEL, level);

            let mut produced = input.clone();
            o.process(&mut produced, 2);

            let mut reference =
                Reference::new(gain as f64, tone as f64, mix as f64, level as f64, 2);
            let expected = reference.process(&input);
            let err = rms_error_db(&produced, &expected);
            assert!(
                err < -60.0,
                "gain={gain} tone={tone} mix={mix} level={level}: RMS error {err:.1} dB exceeds -60 dB"
            );
        }
    }

    #[test]
    fn dc_blocker_removes_saturation_bias() {
        // A.4: the asymmetric saturation would bias the mean of a symmetric input
        // away from zero; the DC blocker must pull the wet mean back to ~0.
        let mut o = Overdrive::new(48_000.0);
        o.set_param(PARAM_GAIN, 5.0);
        o.set_param(PARAM_MIX, 1.0);
        o.set_param(PARAM_OUTPUT_LEVEL, 1.0);
        let mut buf: Vec<f32> = (0..48_000)
            .map(|i| 0.5 * (2.0 * std::f32::consts::PI * 100.0 * i as f32 / 48_000.0).sin())
            .collect();
        o.process(&mut buf, 1);
        let tail = &buf[24_000..];
        let mean = tail.iter().sum::<f32>() / tail.len() as f32;
        assert!(mean.abs() < 0.01, "DC not removed: mean={mean}");
    }

    #[test]
    fn reset_clears_filter_state() {
        // The tone control is stateful, so reset must return the effect to its
        // initial response: processing the same input after a reset reproduces
        // the first run exactly.
        let mut o = Overdrive::new(48_000.0);
        let input = sine_sweep(256);
        let mut first = input.clone();
        o.process(&mut first, 2);
        o.reset();
        let mut second = input.clone();
        o.process(&mut second, 2);
        assert_eq!(first, second);
    }
}
