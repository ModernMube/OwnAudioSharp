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
//!
//! ## Latency reporting and dry alignment (A.5 / A.6)
//!
//! The WSOLA pipeline delays the wet signal by a fixed number of frames.  The
//! effect reports that latency through [`Effect::latency_samples`] so the mixer's
//! plugin delay compensation keeps a pitched track aligned with the others.  The
//! reported latency is a *constant* (computed once from the sample rate at unity
//! pitch), because the [`Effect`] contract requires the value to stay fixed while
//! the effect is in a chain — including while bypassed.  To make that constant
//! truthful in every state, the output is **always** delayed by the same number
//! of frames through a dry look-behind line: an active pitch shift blends the
//! latency-aligned dry with the wet (so `mix < 1` no longer comb-filters two
//! time-misaligned copies), while a bypassed or unity-pitch effect passes the dry
//! signal through the same delay.  A short equal-power-free linear crossfade
//! covers the active↔bypass transition so toggling pitch on or off does not click.

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};
use ownaudio_soundtouch::{SettingId, SoundTouchProcessor};

/// Parameter ID 2 — pitch shift in semitones (-12.0 … +12.0).
pub const PARAM_SEMITONES: u32 = 2;

/// Below this absolute semitone amount the effect is a transparent (but still
/// latency-delayed) bypass, avoiding the cost of running SoundTouch at unity.
const BYPASS_THRESHOLD: f32 = 0.01;

/// Conservative up-front scratch capacity, in samples, matching the mixer's
/// default block convention (a 4096-frame stereo callback).  Pre-sizing here
/// keeps the very first `process` call allocation-free on the audio thread; a
/// larger-than-anticipated block grows it once (amortised, never in steady
/// state).
const PREALLOC_SCRATCH: usize = 4096 * 2;

/// Duration of the active↔bypass crossfade, in seconds.
const CROSSFADE_SECONDS: f32 = 0.01;

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

    /// Fixed reported latency in frames, computed once from the sample rate.
    latency_frames: usize,
    /// Interleaved dry look-behind line of `latency_frames * channels` samples,
    /// allocated when the channel count is first known.
    dry_delay: Vec<f32>,
    /// Frame write cursor into `dry_delay` (0 … latency_frames).
    dry_write: usize,
    /// Current wet/bypass crossfade position (1.0 = fully wet, 0.0 = bypass).
    wet_fade: f32,
    /// Per-frame crossfade increment.
    fade_inc: f32,
}

impl PitchShift {
    /// Creates a new [`PitchShift`] with no shift (0 semitones), fully wet.
    pub fn new(sample_rate: f32) -> Self {
        let latency_frames = Self::compute_latency(sample_rate);
        let fade_inc = if sample_rate > 0.0 {
            (1.0 / (CROSSFADE_SECONDS * sample_rate)).clamp(1.0e-4, 1.0)
        } else {
            1.0
        };
        Self {
            enabled: true,
            mix: 1.0,
            semitones: 0.0,
            sample_rate,
            channels: 0,
            processor: None,
            scratch: vec![0.0; PREALLOC_SCRATCH],
            latency_frames,
            dry_delay: Vec::new(),
            dry_write: 0,
            wet_fade: 0.0,
            fade_inc,
        }
    }

    /// Measures the SoundTouch pipeline's initial latency (frames) at unity pitch
    /// for `sample_rate`.  The value depends only on the sample rate and the fixed
    /// sequence/seek/overlap settings, so it is a stable constant for the effect's
    /// lifetime; returns 0 when the sample rate is invalid.
    fn compute_latency(sample_rate: f32) -> usize {
        if sample_rate <= 0.0 {
            return 0;
        }
        let mut proc = SoundTouchProcessor::new();
        if proc.set_sample_rate(sample_rate as u32).is_err() {
            return 0;
        }
        if proc.set_channels(2).is_err() {
            return 0;
        }
        proc.set_setting(SettingId::UseQuickSeek, 0);
        proc.set_setting(SettingId::UseAntiAliasFilter, 1);
        proc.set_pitch_semitones(0.0);
        proc.get_setting(SettingId::InitialLatency).max(0) as usize
    }

    /// Lazily (re)builds the SoundTouch processor and the dry look-behind line for
    /// the given channel count.  Returns `false` if it could not be configured
    /// (invalid sample rate / channel count), in which case the effect bypasses.
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

        // (Re)allocate the dry look-behind line for the new channel count and
        // reset its cursor; the reported latency itself is channel-independent.
        let needed = self.latency_frames * channels as usize;
        self.dry_delay.clear();
        self.dry_delay.resize(needed, 0.0);
        self.dry_write = 0;
        true
    }
}

impl Effect for PitchShift {
    fn effect_type(&self) -> EffectType {
        EffectType::PitchShift
    }

    fn process(&mut self, buffer: &mut [f32], channels: u16) {
        if channels == 0 {
            return;
        }
        if !self.ensure_configured(channels) {
            return;
        }

        let ch = channels as usize;
        let frames = buffer.len() / ch;
        if frames == 0 {
            return;
        }

        let active = self.enabled && self.semitones.abs() >= BYPASS_THRESHOLD;

        // 1. Produce the wet block (only when active); zero-pad while the initial
        //    latency fills.
        if active {
            if self.scratch.len() < buffer.len() {
                self.scratch.resize(buffer.len(), 0.0);
            }
            if let Some(proc) = self.processor.as_mut() {
                let scratch = &mut self.scratch[..buffer.len()];
                let _ = proc.put_samples(buffer, frames);
                let got = proc.receive_samples(scratch, frames);
                let valid = got * ch;
                for s in scratch[valid..].iter_mut() {
                    *s = 0.0;
                }
            }
        }

        // 2. Without a latency line (invalid sample rate) fall back to the legacy
        //    in-place blend — there is no delay to honour.
        if self.latency_frames == 0 || self.dry_delay.is_empty() {
            if active {
                if self.mix >= 0.999 {
                    buffer.copy_from_slice(&self.scratch[..buffer.len()]);
                } else {
                    let wet = self.mix;
                    let dry = 1.0 - wet;
                    for (b, &w) in buffer.iter_mut().zip(self.scratch.iter()) {
                        *b = (*b * dry) + (w * wet);
                    }
                }
            }
            return;
        }

        // 3. Delay every channel by the reported latency and crossfade between the
        //    bypass path (delayed dry) and the active path (wet blended with the
        //    latency-aligned dry).
        let l = self.latency_frames;
        let mix = self.mix;
        let target = if active { 1.0 } else { 0.0 };
        let inc = self.fade_inc;
        let mut wr = self.dry_write;
        let mut fade = self.wet_fade;

        for f in 0..frames {
            if fade < target {
                fade = (fade + inc).min(target);
            } else if fade > target {
                fade = (fade - inc).max(target);
            }

            for c in 0..ch {
                let idx = f * ch + c;
                let input = buffer[idx];

                let di = wr * ch + c;
                let delayed = self.dry_delay[di];
                self.dry_delay[di] = input;

                let wet = if active { self.scratch[idx] } else { 0.0 };
                let active_out = wet * mix + delayed * (1.0 - mix);
                buffer[idx] = delayed + (active_out - delayed) * fade;
            }

            wr += 1;
            if wr >= l {
                wr = 0;
            }
        }

        self.dry_write = wr;
        self.wet_fade = fade;
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
        self.dry_delay.iter_mut().for_each(|s| *s = 0.0);
        self.dry_write = 0;
        self.wet_fade = 0.0;
    }

    fn is_enabled(&self) -> bool {
        self.enabled
    }

    fn set_enabled(&mut self, enabled: bool) {
        self.enabled = enabled;
    }

    fn latency_samples(&self) -> u32 {
        // Constant across the effect's lifetime and independent of the pitch
        // amount or bypass state, because the output is always delayed by this
        // many frames (see the module-level A.5/A.6 note).
        self.latency_frames as u32
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
    fn reports_nonzero_constant_latency() {
        let p = PitchShift::new(44100.0);
        let l = p.latency_samples();
        assert!(
            l > 0,
            "SoundTouch pitch shift must report its WSOLA latency"
        );
        // The report is a fixed constant — unaffected by pitch or bypass.
        let mut p2 = PitchShift::new(44100.0);
        p2.set_param(PARAM_SEMITONES, 7.0);
        let mut buf = vec![0.1f32; 4096 * 2];
        p2.process(&mut buf, 2);
        assert_eq!(p2.latency_samples(), l);
        p2.set_param(PARAM_SEMITONES, 0.0);
        p2.process(&mut buf, 2);
        assert_eq!(p2.latency_samples(), l);
    }

    #[test]
    fn zero_semitones_is_pure_latency_delay() {
        // At unity pitch the effect is a transparent bypass, but it still delays
        // the signal by its reported latency so the value it advertises to the
        // plugin delay compensation is truthful.  The delayed output must
        // reproduce the input exactly, shifted by `latency` frames.
        let mut p = PitchShift::new(44100.0);
        let l = p.latency_samples() as usize;
        let ch = 2usize;
        let frames = l + 2048;
        let mut input = vec![0.0f32; frames * ch];
        for f in 0..frames {
            let s = ((f as f32) * 0.05).sin() * 0.5;
            input[f * ch] = s;
            input[f * ch + 1] = s * 0.8;
        }
        let mut buf = input.clone();
        p.process(&mut buf, 2);
        // Output frame n (n >= l) equals input frame n - l.
        for f in l..frames {
            for c in 0..ch {
                let got = buf[f * ch + c];
                let want = input[(f - l) * ch + c];
                assert!(
                    (got - want).abs() < 1.0e-6,
                    "frame {f} ch {c}: {got} != delayed input {want}"
                );
            }
        }
    }

    #[test]
    fn disabled_is_pure_latency_delay() {
        // A disabled effect keeps its constant latency (the contract forbids a
        // bypass toggle from changing the alignment), so it too delays the dry
        // signal rather than passing it through untouched.
        let mut p = PitchShift::new(44100.0);
        p.set_param(PARAM_SEMITONES, 5.0);
        p.set_enabled(false);
        let l = p.latency_samples() as usize;
        let ch = 2usize;
        let frames = l + 1024;
        let mut input = vec![0.0f32; frames * ch];
        for f in 0..frames {
            let s = ((f as f32) * 0.07).sin() * 0.4;
            input[f * ch] = s;
            input[f * ch + 1] = s;
        }
        let mut buf = input.clone();
        p.process(&mut buf, 2);
        for f in l..frames {
            let got = buf[f * ch];
            let want = input[(f - l) * ch];
            assert!((got - want).abs() < 1.0e-6, "frame {f}: {got} != {want}");
        }
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
