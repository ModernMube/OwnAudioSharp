//! Real-time audio effects — Effect trait, EffectType, EffectChain.

pub mod autogain;
pub mod chorus;
pub mod compressor;
pub mod delay;
pub mod distortion;
pub mod enhancer;
pub mod equalizer;
pub mod equalizer30;
pub mod flanger;
pub mod gate;
pub mod limiter;
pub mod overdrive;
pub mod phaser;
pub mod dynamic_amp;
pub mod pitch_shift;
pub mod reverb;
pub mod rotary;

pub use autogain::AutoGain;
pub use chorus::Chorus;
pub use compressor::Compressor;
pub use delay::Delay;
pub use distortion::Distortion;
pub use enhancer::Enhancer;
pub use equalizer::Equalizer;
pub use equalizer30::Equalizer30;
pub use flanger::Flanger;
pub use gate::Gate;
pub use limiter::Limiter;
pub use overdrive::Overdrive;
pub use phaser::Phaser;
pub use dynamic_amp::DynamicAmp;
pub use pitch_shift::PitchShift;
pub use reverb::Reverb;
pub use rotary::Rotary;

// ---------------------------------------------------------------------------
// Effect type identifier
// ---------------------------------------------------------------------------

/// Identifies the variant of a native audio effect.
///
/// The numeric values are part of the C ABI — they must stay stable and must
/// mirror the `EffectType` enum on the C# side.
#[repr(u32)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EffectType {
    /// Algorithmic room reverb (Freeverb).
    Reverb = 0,
    /// 10-band parametric equalizer.
    Equalizer = 1,
    /// Dynamic range compressor with soft knee.
    Compressor = 2,
    /// Look-ahead brick-wall limiter.
    Limiter = 3,
    /// Stereo delay with ping-pong and damping.
    Delay = 4,
    /// Multi-voice chorus with LFO modulation.
    Chorus = 5,
    /// Soft-clipping distortion.
    Distortion = 6,
    /// Asymmetric tube overdrive.
    Overdrive = 7,
    /// Flanger with short modulated delay.
    Flanger = 8,
    /// Phaser with all-pass filter stages.
    Phaser = 9,
    /// Rotary/Leslie speaker simulator.
    Rotary = 10,
    /// Automatic gain control.
    AutoGain = 11,
    /// Harmonic enhancer.
    Enhancer = 12,
    /// Noise gate / dynamic amplifier.
    Gate = 13,
    /// Pitch shift (semitone-accurate, real-time).
    PitchShift = 14,
    /// Adaptive dynamic amplifier — dual-window RMS AGC with noise gate.
    DynamicAmp = 15,
    /// 30-band 1/3-octave parametric equalizer.
    Equalizer30 = 16,
}

impl TryFrom<u32> for EffectType {
    type Error = ();

    fn try_from(v: u32) -> Result<Self, Self::Error> {
        match v {
            0 => Ok(Self::Reverb),
            1 => Ok(Self::Equalizer),
            2 => Ok(Self::Compressor),
            3 => Ok(Self::Limiter),
            4 => Ok(Self::Delay),
            5 => Ok(Self::Chorus),
            6 => Ok(Self::Distortion),
            7 => Ok(Self::Overdrive),
            8 => Ok(Self::Flanger),
            9 => Ok(Self::Phaser),
            10 => Ok(Self::Rotary),
            11 => Ok(Self::AutoGain),
            12 => Ok(Self::Enhancer),
            13 => Ok(Self::Gate),
            14 => Ok(Self::PitchShift),
            15 => Ok(Self::DynamicAmp),
            16 => Ok(Self::Equalizer30),
            _ => Err(()),
        }
    }
}

// ---------------------------------------------------------------------------
// Effect trait
// ---------------------------------------------------------------------------

/// Contract for a real-time audio effect.
///
/// All implementations must be allocation-free inside `process` and must not
/// panic.  Internal state (delay lines, filter histories, LFO phases) must be
/// pre-allocated during construction.
pub trait Effect: Send {
    /// Returns the static type tag of this effect.
    fn effect_type(&self) -> EffectType;

    /// Processes `buffer` in-place.
    ///
    /// - `buffer` is interleaved f32 samples.
    /// - `channels` is the number of interleaved channels (1 = mono, 2 = stereo, …).
    ///
    /// Must not allocate heap memory.  Must not panic.
    fn process(&mut self, buffer: &mut [f32], channels: u16);

    /// Sets a parameter by numeric identifier.
    ///
    /// Returns `true` when the `param_id` is recognised; `false` otherwise.
    /// Values outside the documented range are clamped silently.
    fn set_param(&mut self, param_id: u32, value: f32) -> bool;

    /// Reads back the current value of a parameter.
    ///
    /// Returns `None` when `param_id` is unknown.
    fn get_param(&self, param_id: u32) -> Option<f32>;

    /// Resets all internal state (delay lines, envelopes, LFO phases) to
    /// zero without changing parameter values or reallocating.
    fn reset(&mut self);

    /// Returns `true` when the effect is active (not bypassed).
    fn is_enabled(&self) -> bool;

    /// Enables or disables the effect bypass.
    fn set_enabled(&mut self, enabled: bool);
}

// ---------------------------------------------------------------------------
// Common parameter IDs (shared across all effects)
// ---------------------------------------------------------------------------

/// Parameter ID 0 — enabled flag (0.0 = disabled, 1.0 = enabled).
pub const PARAM_ENABLED: u32 = 0;
/// Parameter ID 1 — dry/wet mix (0.0 = fully dry, 1.0 = fully wet).
pub const PARAM_MIX: u32 = 1;

// ---------------------------------------------------------------------------
// EffectChain
// ---------------------------------------------------------------------------

/// An ordered list of effects applied sequentially to an audio buffer.
///
/// The chain is sized only outside the audio thread (add/remove via command
/// queue); on the hot path only `process_all` is called, which iterates the
/// existing `Vec` without allocating.
pub struct EffectChain {
    effects: Vec<Box<dyn Effect>>,
}

impl EffectChain {
    /// Creates an empty chain.
    pub fn new() -> Self {
        Self { effects: Vec::new() }
    }

    /// Appends an effect to the end of the chain.
    ///
    /// Must only be called from outside the audio thread.
    pub fn push(&mut self, effect: Box<dyn Effect>) {
        self.effects.push(effect);
    }

    /// Removes and returns the effect at `index`, or `None` if out of range.
    ///
    /// Must only be called from outside the audio thread.
    pub fn remove(&mut self, index: usize) -> Option<Box<dyn Effect>> {
        if index < self.effects.len() {
            Some(self.effects.remove(index))
        } else {
            None
        }
    }

    /// Returns the number of effects in the chain.
    pub fn len(&self) -> usize {
        self.effects.len()
    }

    /// Returns `true` when the chain contains no effects.
    pub fn is_empty(&self) -> bool {
        self.effects.is_empty()
    }

    /// Applies every enabled effect in order to `buffer`.
    ///
    /// Safe to call from the audio thread; never allocates.
    #[inline]
    pub fn process_all(&mut self, buffer: &mut [f32], channels: u16) {
        for effect in &mut self.effects {
            if effect.is_enabled() {
                effect.process(buffer, channels);
            }
        }
    }

    /// Returns a mutable reference to the effect at `index`, or `None`.
    pub fn effect_mut<'a>(&'a mut self, index: usize) -> Option<&'a mut dyn Effect> {
        if index < self.effects.len() {
            Some(self.effects[index].as_mut())
        } else {
            None
        }
    }
}

impl Default for EffectChain {
    fn default() -> Self {
        Self::new()
    }
}
