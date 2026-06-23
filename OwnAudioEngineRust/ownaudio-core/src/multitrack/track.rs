//! Single audio track: an audio source, a shared atomic parameter block, and
//! an effect chain.

use std::sync::atomic::{AtomicBool, AtomicU32, AtomicU8, Ordering};
use std::sync::Arc;

use crate::effects::EffectChain;
use crate::ringbuffer::RingBufferReader;
use crate::smoothing::SmoothedParam;

/// One-pole time constant (ms) used to ramp the per-track gain, removing the
/// zipper noise / click an abrupt gain change would otherwise produce.
const GAIN_SMOOTH_MS: f32 = 5.0;

/// Per-track effect-chain capacity pre-allocated at construction, so adding
/// effects (even via a drained command on the audio thread) never reallocates
/// the chain up to this many effects.
pub const MAX_EFFECTS_PER_TRACK: usize = 32;

/// Playback state of a single track.
///
/// The numeric values are stable: they are stored in an [`AtomicU8`] inside
/// [`TrackShared`] and round-tripped through [`TrackState::from_u8`].
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum TrackState {
    /// Track is stopped.
    Stopped = 0,
    /// Track is playing.
    Playing = 1,
    /// Track is paused.
    Paused = 2,
}

impl TrackState {
    /// Reconstructs a [`TrackState`] from its `u8` discriminant.
    ///
    /// Any unrecognised value maps to [`TrackState::Stopped`], so a corrupt or
    /// out-of-range atomic read can never produce undefined behaviour.
    #[inline]
    pub fn from_u8(raw: u8) -> Self {
        match raw {
            1 => TrackState::Playing,
            2 => TrackState::Paused,
            _ => TrackState::Stopped,
        }
    }
}

/// Source of audio samples feeding a single [`Track`].
///
/// The audio thread calls [`TrackSource::read`] once per render block to pull
/// interleaved samples.  Implementations must be allocation-free and
/// non-blocking on the read path, and must be `Send` so the source can be owned
/// by the audio thread.
pub trait TrackSource: Send {
    /// Reads up to `out.len()` interleaved samples into `out`.
    ///
    /// Returns the number of samples actually written.  The caller silence-fills
    /// the remainder of `out` when fewer than `out.len()` samples are returned,
    /// so implementations need not zero the tail themselves.
    fn read(&mut self, out: &mut [f32]) -> usize;
}

impl TrackSource for RingBufferReader {
    #[inline]
    fn read(&mut self, out: &mut [f32]) -> usize {
        RingBufferReader::read(self, out)
    }
}

/// Lock-free, atomically-mutable parameter block for a single track.
///
/// Shared between the control thread (FFI / C#) and the audio thread via
/// `Arc<TrackShared>`.  Every field is atomic, so parameter changes never race
/// with the audio thread reading them in the render loop.  `f32` values are
/// stored as their raw bit pattern in an [`AtomicU32`].
///
/// All accesses use [`Ordering::Relaxed`]: these parameters do not publish
/// audio data (the ring buffer source handles that ordering), so no
/// happens-before relationship is required.
pub struct TrackShared {
    /// Current [`TrackState`] discriminant.
    state: AtomicU8,
    /// Gain as `f32` bits (linear amplitude, 1.0 = unity).
    gain_bits: AtomicU32,
    /// Mute flag — when `true` the track contributes nothing to the mix.
    muted: AtomicBool,
    /// Solo flag — when any track is soloed, non-soloed tracks are muted.
    soloed: AtomicBool,
    /// Tempo ratio as `f32` bits (1.0 = normal speed).
    tempo_ratio_bits: AtomicU32,
    /// Pitch shift in semitones, as `f32` bits.
    pitch_semitones_bits: AtomicU32,
}

impl TrackShared {
    /// Creates a parameter block at unity gain, stopped, unmuted, unsoloed.
    pub fn new() -> Self {
        Self {
            state: AtomicU8::new(TrackState::Stopped as u8),
            gain_bits: AtomicU32::new(1.0f32.to_bits()),
            muted: AtomicBool::new(false),
            soloed: AtomicBool::new(false),
            tempo_ratio_bits: AtomicU32::new(1.0f32.to_bits()),
            pitch_semitones_bits: AtomicU32::new(0.0f32.to_bits()),
        }
    }

    /// Returns the current playback state.
    #[inline]
    pub fn state(&self) -> TrackState {
        TrackState::from_u8(self.state.load(Ordering::Relaxed))
    }

    /// Sets the playback state.
    #[inline]
    pub fn set_state(&self, state: TrackState) {
        self.state.store(state as u8, Ordering::Relaxed);
    }

    /// Returns the linear gain.
    #[inline]
    pub fn gain(&self) -> f32 {
        f32::from_bits(self.gain_bits.load(Ordering::Relaxed))
    }

    /// Sets the linear gain (clamped to be non-negative).
    #[inline]
    pub fn set_gain(&self, gain: f32) {
        self.gain_bits
            .store(gain.max(0.0).to_bits(), Ordering::Relaxed);
    }

    /// Returns whether the track is muted.
    #[inline]
    pub fn muted(&self) -> bool {
        self.muted.load(Ordering::Relaxed)
    }

    /// Sets the mute flag.
    #[inline]
    pub fn set_muted(&self, muted: bool) {
        self.muted.store(muted, Ordering::Relaxed);
    }

    /// Returns whether the track is soloed.
    #[inline]
    pub fn soloed(&self) -> bool {
        self.soloed.load(Ordering::Relaxed)
    }

    /// Sets the solo flag.
    #[inline]
    pub fn set_soloed(&self, soloed: bool) {
        self.soloed.store(soloed, Ordering::Relaxed);
    }

    /// Returns the tempo ratio.
    #[inline]
    pub fn tempo_ratio(&self) -> f32 {
        f32::from_bits(self.tempo_ratio_bits.load(Ordering::Relaxed))
    }

    /// Sets the tempo ratio (clamped to `0.25..=4.0`).
    #[inline]
    pub fn set_tempo_ratio(&self, ratio: f32) {
        self.tempo_ratio_bits
            .store(ratio.clamp(0.25, 4.0).to_bits(), Ordering::Relaxed);
    }

    /// Returns the pitch shift in semitones.
    #[inline]
    pub fn pitch_semitones(&self) -> f32 {
        f32::from_bits(self.pitch_semitones_bits.load(Ordering::Relaxed))
    }

    /// Sets the pitch shift in semitones (clamped to `-24.0..=24.0`).
    #[inline]
    pub fn set_pitch_semitones(&self, semitones: f32) {
        self.pitch_semitones_bits
            .store(semitones.clamp(-24.0, 24.0).to_bits(), Ordering::Relaxed);
    }
}

impl Default for TrackShared {
    fn default() -> Self {
        Self::new()
    }
}

/// A single audio track within a [`super::MultiTrackMixer`].
///
/// Owned by the audio thread.  On every render block the mixer reads the
/// track's [`TrackSource`] into a private scratch buffer, applies the effect
/// chain, scales by gain, and sums the result additively into the shared output
/// (see [`super::MultiTrackMixer::mix`]).
///
/// Parameters live in [`Track::shared`] (`Arc<TrackShared>`) so the control
/// thread can mutate them lock-free while the audio thread reads them.
pub struct Track {
    /// Stable identifier, unique within the owning mixer for its lifetime.
    pub id: u64,
    /// Atomically-mutable parameters, shared with the control thread.
    pub shared: Arc<TrackShared>,
    /// Ordered list of effects applied to this track.
    pub effects: EffectChain,
    /// Audio source feeding this track; `None` produces silence.
    source: Option<Box<dyn TrackSource>>,
    /// Pre-allocated per-track scratch buffer (the track renders into this
    /// before being summed into the mix); grown only when a larger block than
    /// any seen before arrives.
    scratch: Vec<f32>,
    /// Per-frame gain smoother; ramps the atomic [`TrackShared::gain`] toward its
    /// latest value to suppress zipper noise on abrupt changes.
    gain_smoother: SmoothedParam,
}

impl Track {
    /// Creates a new idle track with the given id, sample rate (for gain
    /// smoothing), and pre-sized scratch buffer.
    pub fn new(id: u64, sample_rate: f32, max_buffer_size: usize) -> Self {
        let shared = Arc::new(TrackShared::new());
        let gain_smoother = SmoothedParam::new(shared.gain(), sample_rate, GAIN_SMOOTH_MS);
        Self {
            id,
            shared,
            effects: EffectChain::with_capacity(MAX_EFFECTS_PER_TRACK),
            source: None,
            scratch: vec![0.0f32; max_buffer_size],
            gain_smoother,
        }
    }

    /// Replaces the track's audio source, dropping any previous one in place.
    pub fn set_source(&mut self, source: Option<Box<dyn TrackSource>>) {
        self.source = source;
    }

    /// Replaces the track's audio source and returns the previous one without
    /// dropping it, so the caller (the audio thread, via a drained command) can
    /// hand the old source back to the control thread for deallocation instead
    /// of freeing heap memory on the real-time path.
    pub fn replace_source(
        &mut self,
        source: Option<Box<dyn TrackSource>>,
    ) -> Option<Box<dyn TrackSource>> {
        std::mem::replace(&mut self.source, source)
    }

    /// Returns `true` when this track contributes audio to the mix.
    ///
    /// `any_soloed` is `true` when at least one track in the mixer is soloed; in
    /// that case only soloed tracks are audible.
    #[inline]
    pub fn is_active(&self, any_soloed: bool) -> bool {
        self.shared.state() == TrackState::Playing
            && !self.shared.muted()
            && (!any_soloed || self.shared.soloed())
    }

    /// Renders this track and sums it additively into `output`.
    ///
    /// The track reads its source into a private scratch buffer (silence-padding
    /// any underrun), runs its effect chain, scales by a per-frame ramped gain,
    /// and adds the result to the corresponding samples of `output` — it never
    /// overwrites the contributions of other tracks (the additive-mix contract).
    /// `output` must not be longer than the block size the mixer was built for;
    /// the scratch grows once if a larger block than any seen before arrives.
    ///
    /// The gain is interpolated sample-by-sample toward [`TrackShared::gain`]
    /// rather than applied as a flat block multiplier, so a gain change between
    /// blocks fades in over a few milliseconds instead of clicking.
    #[inline]
    pub(crate) fn process_additive(&mut self, output: &mut [f32], channels: u16) {
        let frame_len = output.len();
        if self.scratch.len() < frame_len {
            self.scratch.resize(frame_len, 0.0);
        }
        let buf = &mut self.scratch[..frame_len];

        let read = match self.source.as_mut() {
            Some(src) => src.read(buf),
            None => 0,
        };
        for s in &mut buf[read..] {
            *s = 0.0;
        }

        self.effects.process_all(buf, channels);

        self.gain_smoother.set_target(self.shared.gain());
        let ch = channels.max(1) as usize;
        // Advance the gain once per frame so both channels of a stereo frame
        // share the same (smoothed) gain.
        for (out_frame, in_frame) in output.chunks_mut(ch).zip(buf.chunks(ch)) {
            let gain = self.gain_smoother.advance();
            for (o, &s) in out_frame.iter_mut().zip(in_frame.iter()) {
                *o += s * gain;
            }
        }
    }
}
