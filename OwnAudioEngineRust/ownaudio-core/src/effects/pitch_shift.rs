//! Real-time pitch shifter (semitone-accurate).
//!
//! Thin wrapper over [`ownaudio_soundtouch::SoundTouchProcessor`], mirroring the
//! managed `OwnaudioNET.Processing.SoundTouchProcessor` wrapper: it applies the
//! same mandatory settings (quick-seek **off**, anti-alias filter **on**) and
//! drives pitch via semitones while leaving tempo unchanged.
//!
//! ## Block-based adaptation
//!
//! SoundTouch is a latency-introducing FIFO: feeding `N` frames does not yield
//! `N` frames immediately.  The [`Effect`] contract, however, is block-in-place
//! (`N` in → `N` in the same buffer).  This wrapper bridges the two by relying
//! on SoundTouch's own internal output buffering: each block it pushes the dry
//! input, then pulls up to one block of processed output, zero-padding while the
//! initial latency fills.  Because pure pitch shifting has an input/output ratio
//! of exactly 1.0, the internal buffer stays bounded near the latency and the
//! pull rate matches the push rate in steady state — so after warm-up `process`
//! neither allocates nor drifts.

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};
use ownaudio_soundtouch::{SettingId, SoundTouchProcessor};

/// Parameter ID 2 — pitch shift in semitones (-12.0 … +12.0).
pub const PARAM_SEMITONES: u32 = 2;

/// Below this absolute semitone amount the effect is a transparent bypass,
/// avoiding the pointless latency of running SoundTouch at unity pitch.
const BYPASS_THRESHOLD: f32 = 0.01;

/// Conservative up-front scratch capacity, in samples, matching the mixer's
/// default block convention (a 4096-frame stereo callback).  Pre-sizing here
/// keeps the very first `process` call allocation-free on the audio thread; a
/// larger-than-anticipated block grows it once (amortised, never in steady
/// state).
const PREALLOC_SCRATCH: usize = 4096 * 2;

/// Real-time pitch shift effect backed by the WSOLA SoundTouch pipeline.
pub struct PitchShift {
    enabled: bool,
    mix: f32,
    semitones: f32,
    sample_rate: f32,
    /// Channel count the processor is currently configured for (0 = none yet).
    channels: u16,
    processor: Option<SoundTouchProcessor>,
    /// Reusable wet-signal scratch, pre-sized to [`PREALLOC_SCRATCH`] and grown
    /// at most once should a larger block ever arrive.
    scratch: Vec<f32>,
}

impl PitchShift {
    /// Creates a new [`PitchShift`] with no shift (0 semitones), fully wet.
    pub fn new(sample_rate: f32) -> Self {
        Self {
            enabled: true,
            mix: 1.0,
            semitones: 0.0,
            sample_rate,
            channels: 0,
            processor: None,
            scratch: vec![0.0; PREALLOC_SCRATCH],
        }
    }

    /// Lazily (re)builds the SoundTouch processor for the given channel count.
    /// Returns `false` if it could not be configured (invalid sample rate /
    /// channel count), in which case the effect bypasses.
    fn ensure_configured(&mut self, channels: u16) -> bool {
        if self.processor.is_some() && self.channels == channels {
            return true;
        }

        let mut proc = SoundTouchProcessor::new();
        if proc.set_sample_rate(self.sample_rate as u32).is_err() {
            self.processor = None;
            return false;
        }
        if proc.set_channels(channels as usize).is_err() {
            self.processor = None;
            return false;
        }
        // Mandatory settings, identical to the managed wrapper.
        proc.set_setting(SettingId::UseQuickSeek, 0);
        proc.set_setting(SettingId::UseAntiAliasFilter, 1);
        proc.set_pitch_semitones(self.semitones as f64);

        self.channels = channels;
        self.processor = Some(proc);
        true
    }
}

impl Effect for PitchShift {
    fn effect_type(&self) -> EffectType {
        EffectType::PitchShift
    }

    fn process(&mut self, buffer: &mut [f32], channels: u16) {
        if !self.enabled || channels == 0 || self.semitones.abs() < BYPASS_THRESHOLD {
            return;
        }
        if !self.ensure_configured(channels) {
            return;
        }

        let frames = buffer.len() / channels as usize;
        if frames == 0 {
            return;
        }
        if self.scratch.len() < buffer.len() {
            self.scratch.resize(buffer.len(), 0.0);
        }

        let Some(proc) = self.processor.as_mut() else {
            return;
        };
        let scratch = &mut self.scratch[..buffer.len()];

        // Push dry input (copied; `buffer` is left intact for the dry path),
        // then pull one block of processed output.
        let _ = proc.put_samples(buffer, frames);
        let got = proc.receive_samples(scratch, frames);
        let valid = got * channels as usize;
        for s in scratch[valid..].iter_mut() {
            *s = 0.0;
        }

        if self.mix >= 0.999 {
            buffer.copy_from_slice(scratch);
        } else {
            let wet = self.mix;
            let dry = 1.0 - wet;
            for (b, &w) in buffer.iter_mut().zip(scratch.iter()) {
                *b = (*b * dry) + (w * wet);
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
                true
            }
            PARAM_SEMITONES => {
                self.semitones = value.clamp(-12.0, 12.0);
                if let Some(proc) = self.processor.as_mut() {
                    proc.set_pitch_semitones(self.semitones as f64);
                }
                true
            }
            _ => false,
        }
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        match param_id {
            PARAM_ENABLED => Some(if self.enabled { 1.0 } else { 0.0 }),
            PARAM_MIX => Some(self.mix),
            PARAM_SEMITONES => Some(self.semitones),
            _ => None,
        }
    }

    fn reset(&mut self) {
        if let Some(proc) = self.processor.as_mut() {
            proc.clear();
        }
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

    fn process_seconds(pitch: &mut PitchShift, channels: u16, seconds: f32, sr: f32) -> Vec<f32> {
        let block = 1024usize;
        let mut phase = 0.0f32;
        let total = (seconds * sr) as usize;
        let mut out = Vec::new();
        let mut produced = 0;
        let mut buf = vec![0.0f32; block * channels as usize];
        while produced < total {
            for f in 0..block {
                let s = (phase).sin() * 0.5;
                phase += 0.05;
                for c in 0..channels as usize {
                    buf[f * channels as usize + c] = s;
                }
            }
            pitch.process(&mut buf, channels);
            out.extend_from_slice(&buf);
            produced += block;
        }
        out
    }

    #[test]
    fn zero_semitones_is_bypass() {
        let mut p = PitchShift::new(44100.0);
        let mut buf = vec![0.3f32; 512];
        let before = buf.clone();
        p.process(&mut buf, 2);
        assert_eq!(buf, before, "0 semitones must pass through untouched");
    }

    #[test]
    fn disabled_is_bypass() {
        let mut p = PitchShift::new(44100.0);
        p.set_param(PARAM_SEMITONES, 5.0);
        p.set_enabled(false);
        let mut buf = vec![0.3f32; 512];
        let before = buf.clone();
        p.process(&mut buf, 2);
        assert_eq!(buf, before);
    }

    #[test]
    fn shifted_output_is_finite_and_nonsilent() {
        let mut p = PitchShift::new(44100.0);
        assert!(p.set_param(PARAM_SEMITONES, 4.0));
        let out = process_seconds(&mut p, 2, 0.5, 44100.0);
        assert!(out.iter().all(|v| v.is_finite()));
        // After the initial latency the shifted signal must carry energy.
        let tail = &out[out.len() / 2..];
        let energy: f64 = tail.iter().map(|&v| (v as f64) * (v as f64)).sum();
        assert!(energy > 1.0, "pitch-shifted output unexpectedly silent");
    }

    #[test]
    fn param_roundtrip() {
        let mut p = PitchShift::new(48000.0);
        assert!(p.set_param(PARAM_SEMITONES, 20.0)); // clamps to 12
        assert_eq!(p.get_param(PARAM_SEMITONES), Some(12.0));
        assert!(p.set_param(PARAM_MIX, 0.4));
        assert_eq!(p.get_param(PARAM_MIX), Some(0.4));
        assert_eq!(p.get_param(99), None);
    }

    #[test]
    fn unknown_param_returns_false() {
        let mut p = PitchShift::new(44100.0);
        assert!(!p.set_param(99, 1.0));
    }
}
