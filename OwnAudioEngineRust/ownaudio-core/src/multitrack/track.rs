//! Single audio track: an audio source, a shared atomic parameter block, and
//! an effect chain.

use std::sync::atomic::{AtomicBool, AtomicU32, AtomicU64, AtomicU8, Ordering};
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
    /// Number of output frames this track has actually rendered into the mix
    /// since the last position reset (seek or source swap).
    ///
    /// Advanced on the audio thread by [`Track::process_additive`] with the block's
    /// output frame count; read from the control thread as the track's authoritative
    /// playback position. This is the *rendered* position, which lags the *fed*
    /// position by the ring-buffer depth — it is what an accurate `Position` must use.
    rendered_frames: AtomicU64,
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
            rendered_frames: AtomicU64::new(0),
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

    /// Returns the number of output frames rendered since the last position reset.
    #[inline]
    pub fn rendered_frames(&self) -> u64 {
        self.rendered_frames.load(Ordering::Relaxed)
    }

    /// Adds `frames` to the rendered-frame counter (called on the audio thread).
    #[inline]
    pub fn add_rendered_frames(&self, frames: u64) {
        self.rendered_frames.fetch_add(frames, Ordering::Relaxed);
    }

    /// Resets the rendered-frame counter to zero (on seek or source swap).
    #[inline]
    pub fn reset_rendered_frames(&self) {
        self.rendered_frames.store(0, Ordering::Relaxed);
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
    /// Per-track time-stretch / pitch-shift stage; applies the track's tempo and pitch to the
    /// source audio via SoundTouch, or bypasses transparently at unity.
    stretch: super::stretch::TrackStretch,
    /// Sub-frame remainder of the tempo-scaled content-position advance, carried between
    /// blocks so the integer [`TrackShared::rendered_frames`] does not drift. Mirrors the
    /// legacy `FileSource._fractionalFrameAccumulator` so the reported position stays
    /// content-time (a tempo of `r` advances the position by `r` content frames per output
    /// frame), matching the pre-refactor behavior.
    content_frame_accumulator: f64,
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
            stretch: super::stretch::TrackStretch::new(sample_rate, max_buffer_size),
            content_frame_accumulator: 0.0,
        }
    }

    /// Replaces the track's audio source, dropping any previous one in place.
    ///
    /// Resets the rendered-frame position: a new source restarts playback from
    /// its own frame zero.
    pub fn set_source(&mut self, source: Option<Box<dyn TrackSource>>) {
        self.source = source;
        self.shared.reset_rendered_frames();
        self.content_frame_accumulator = 0.0;
    }

    /// Replaces the track's audio source and returns the previous one without
    /// dropping it, so the caller (the audio thread, via a drained command) can
    /// hand the old source back to the control thread for deallocation instead
    /// of freeing heap memory on the real-time path.
    ///
    /// Resets the rendered-frame position: a new source restarts playback from
    /// its own frame zero.
    pub fn replace_source(
        &mut self,
        source: Option<Box<dyn TrackSource>>,
    ) -> Option<Box<dyn TrackSource>> {
        self.shared.reset_rendered_frames();
        self.content_frame_accumulator = 0.0;
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

        // Apply the track's tempo/pitch via SoundTouch when either deviates from unity;
        // otherwise read the source straight through (and clear any stale stretch latency).
        let tempo = self.shared.tempo_ratio();
        let pitch = self.shared.pitch_semitones();
        let read = if self.stretch.is_active(tempo, pitch) {
            self.stretch
                .fill(&mut self.source, buf, channels, tempo, pitch)
        } else {
            self.stretch.deactivate();
            match self.source.as_mut() {
                Some(src) => src.read(buf),
                None => 0,
            }
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

        // Advance the rendered position by the number of *content* frames this block
        // represents: at tempo `r`, one output frame consumes `r` content frames, so the
        // reported position stays content-time (matching the legacy SoundTouch path). A
        // sub-frame accumulator carries the fractional remainder so the integer counter does
        // not drift. At unity tempo this is exactly the output frame count.
        let out_frames = (frame_len / ch) as f64;
        self.content_frame_accumulator += out_frames * tempo.max(0.0) as f64;
        let content_frames = self.content_frame_accumulator.floor();
        self.content_frame_accumulator -= content_frames;
        self.shared.add_rendered_frames(content_frames as u64);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Minimal source that always fills its output with a constant sample value.
    struct ConstSource(f32);

    impl TrackSource for ConstSource {
        fn read(&mut self, out: &mut [f32]) -> usize {
            for s in out.iter_mut() {
                *s = self.0;
            }
            out.len()
        }
    }

    #[test]
    fn rendered_frames_advances_by_output_frame_count() {
        let mut track = Track::new(1, 48_000.0, 64);
        track.set_source(Some(Box::new(ConstSource(0.5))));
        track.shared.set_state(TrackState::Playing);

        let mut out = vec![0.0f32; 8]; // 4 stereo frames
        track.process_additive(&mut out, 2);
        assert_eq!(track.shared.rendered_frames(), 4);

        track.process_additive(&mut out, 2);
        assert_eq!(track.shared.rendered_frames(), 8);
    }

    #[test]
    fn rendered_frames_advances_even_on_underrun_silence() {
        // A playing track with no source still advances its rendered position
        // (it renders silence), matching the legacy silence-fill behavior.
        let mut track = Track::new(2, 48_000.0, 64);
        track.shared.set_state(TrackState::Playing);

        let mut out = vec![0.0f32; 8];
        track.process_additive(&mut out, 2);
        assert_eq!(track.shared.rendered_frames(), 4);
    }

    #[test]
    fn source_swap_resets_rendered_frames() {
        let mut track = Track::new(3, 48_000.0, 64);
        track.set_source(Some(Box::new(ConstSource(0.5))));
        track.shared.set_state(TrackState::Playing);

        let mut out = vec![0.0f32; 8];
        track.process_additive(&mut out, 2);
        assert_eq!(track.shared.rendered_frames(), 4);

        // Swapping the source restarts the rendered position at zero.
        let _prev = track.replace_source(Some(Box::new(ConstSource(0.25))));
        assert_eq!(track.shared.rendered_frames(), 0);

        track.set_source(None);
        assert_eq!(track.shared.rendered_frames(), 0);
    }

    /// Source that reports how many interleaved samples it has been asked for, so a test can
    /// prove tempo drives the source-consumption rate through the full render path.
    struct CountingSource {
        read_samples: Arc<AtomicU64>,
    }

    impl TrackSource for CountingSource {
        fn read(&mut self, out: &mut [f32]) -> usize {
            self.read_samples
                .fetch_add(out.len() as u64, Ordering::Relaxed);
            for (i, s) in out.iter_mut().enumerate() {
                *s = ((i % 32) as f32 / 32.0) - 0.5;
            }
            out.len()
        }
    }

    #[test]
    fn tempo_change_alters_source_consumption_through_full_path() {
        // Full-path proof that tempo is applied: at 2.0x tempo the render loop must pull
        // roughly twice as much source audio per output block as at unity. Before the stretch
        // stage was wired in, tempo was dead data and both counts were identical.
        let block = 1024usize; // 512 stereo frames
        let sr = 48_000.0;

        let unity = Arc::new(AtomicU64::new(0));
        let mut t_unity = Track::new(10, sr, block);
        t_unity.set_source(Some(Box::new(CountingSource { read_samples: unity.clone() })));
        t_unity.shared.set_state(TrackState::Playing);
        t_unity.shared.set_tempo_ratio(1.0);

        let fast = Arc::new(AtomicU64::new(0));
        let mut t_fast = Track::new(11, sr, block);
        t_fast.set_source(Some(Box::new(CountingSource { read_samples: fast.clone() })));
        t_fast.shared.set_state(TrackState::Playing);
        t_fast.shared.set_tempo_ratio(2.0);

        let mut out = vec![0.0f32; block];
        for _ in 0..16 {
            out.iter_mut().for_each(|s| *s = 0.0);
            t_unity.process_additive(&mut out, 2);
            out.iter_mut().for_each(|s| *s = 0.0);
            t_fast.process_additive(&mut out, 2);
        }

        // 2.0x tempo consumes markedly more input than unity for the same rendered output.
        let fast_read = fast.load(Ordering::Relaxed);
        let unity_read = unity.load(Ordering::Relaxed);
        assert!(
            fast_read as f64 > unity_read as f64 * 1.5,
            "tempo 2.0 should pull far more source than unity (fast={fast_read}, unity={unity_read})"
        );
    }

    #[test]
    fn rendered_position_is_content_time_under_tempo() {
        // At tempo 2.0 the reported position must advance at twice the output-frame rate
        // (content time), matching the legacy `framesRead * _tempo` accounting.
        let block = 1024usize; // 512 stereo frames per block
        let mut track = Track::new(20, 48_000.0, block);
        track.set_source(Some(Box::new(ConstSource(0.3))));
        track.shared.set_state(TrackState::Playing);
        track.shared.set_tempo_ratio(2.0);

        let mut out = vec![0.0f32; block];
        let blocks = 10u64;
        for _ in 0..blocks {
            out.iter_mut().for_each(|s| *s = 0.0);
            track.process_additive(&mut out, 2);
        }

        let out_frames = (block as u64 / 2) * blocks;
        let expected = out_frames * 2; // tempo 2.0
        let actual = track.shared.rendered_frames();
        // Allow at most one frame of accumulator rounding slack.
        assert!(
            actual.abs_diff(expected) <= 1,
            "content position should be ~2x output frames (expected {expected}, got {actual})"
        );
    }

    #[test]
    fn reset_rendered_frames_zeroes_the_counter() {
        let shared = TrackShared::new();
        shared.add_rendered_frames(100);
        assert_eq!(shared.rendered_frames(), 100);
        shared.reset_rendered_frames();
        assert_eq!(shared.rendered_frames(), 0);
    }
}
