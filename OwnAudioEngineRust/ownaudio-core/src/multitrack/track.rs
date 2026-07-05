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
    /// Set by the control thread (on a seek) to ask the audio thread to clear the per-track
    /// SoundTouch FIFO before the next block, so no pre-seek audio buffered in the stretch
    /// latency leaks out after the jump. The audio thread consumes (clears) the flag.
    stretch_flush_pending: AtomicBool,
    /// When `true`, the track routes through the SoundTouch stretch stage on every block even
    /// at unity tempo/pitch, keeping the FIFO continuously primed so a live tempo/pitch change
    /// does not click from a cold start. Latched on the first non-unity tempo/pitch (see
    /// [`TrackShared::set_tempo_ratio`]) and never cleared, so a track that is never stretched
    /// (e.g. a metronome, whose tempo is baked into its audio) stays a clean bypass — no added
    /// latency, no stale FIFO across stop/restart — while a stretched track never re-cold-starts.
    stretch_always_on: AtomicBool,
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
            stretch_flush_pending: AtomicBool::new(false),
            stretch_always_on: AtomicBool::new(false),
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
    ///
    /// The first time the ratio departs from unity the track latches into always-on
    /// stretching (see `stretch_always_on`), so once a track has been time-stretched every
    /// later change lands on a warm SoundTouch FIFO and cannot click — while a track that is
    /// never stretched (e.g. a metronome click, whose tempo is baked into its audio) stays a
    /// clean bypass.
    #[inline]
    pub fn set_tempo_ratio(&self, ratio: f32) {
        let clamped = ratio.clamp(0.25, 4.0);
        self.tempo_ratio_bits.store(clamped.to_bits(), Ordering::Relaxed);
        if (clamped - 1.0).abs() > 1e-4 {
            self.stretch_always_on.store(true, Ordering::Relaxed);
        }
    }

    /// Returns the pitch shift in semitones.
    #[inline]
    pub fn pitch_semitones(&self) -> f32 {
        f32::from_bits(self.pitch_semitones_bits.load(Ordering::Relaxed))
    }

    /// Sets the pitch shift in semitones (clamped to `-24.0..=24.0`).
    ///
    /// Latches always-on stretching the first time the shift departs from zero, for the same
    /// warm-FIFO reason as [`set_tempo_ratio`].
    #[inline]
    pub fn set_pitch_semitones(&self, semitones: f32) {
        let clamped = semitones.clamp(-24.0, 24.0);
        self.pitch_semitones_bits.store(clamped.to_bits(), Ordering::Relaxed);
        if clamped.abs() > 1e-4 {
            self.stretch_always_on.store(true, Ordering::Relaxed);
        }
    }

    /// Requests that the audio thread clear the per-track stretch FIFO before the next block
    /// (called from the control thread on a seek).
    #[inline]
    pub fn request_stretch_flush(&self) {
        self.stretch_flush_pending.store(true, Ordering::Relaxed);
    }

    /// Atomically consumes a pending stretch-flush request, returning whether one was set.
    /// Called on the audio thread at the top of the render block.
    #[inline]
    pub fn take_stretch_flush(&self) -> bool {
        self.stretch_flush_pending.swap(false, Ordering::Relaxed)
    }

    /// Returns whether the stretch stage runs unconditionally (see `stretch_always_on`).
    #[inline]
    pub fn stretch_always_on(&self) -> bool {
        self.stretch_always_on.load(Ordering::Relaxed)
    }

    /// Forces always-on stretching on or off. Normally latched automatically by the first
    /// non-unity tempo/pitch; exposed for explicit control and testing.
    #[inline]
    pub fn set_stretch_always_on(&self, on: bool) {
        self.stretch_always_on.store(on, Ordering::Relaxed);
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
/// Per-track plugin delay compensation (PDC): a delay line applied to the track's
/// output so tracks with lower-latency effect chains line up sample-accurately with
/// the track that has the highest latency.
///
/// The mixer computes each track's compensation as `max_chain_latency − this_chain_latency`
/// and pushes it here via [`Track::set_pdc_delay`]. A zero delay is a pure passthrough and
/// touches no memory. On a delay change the ring is resized (once, like the render scratch)
/// and cleared — this happens only when an effect is added or removed, never per block.
struct PdcDelay {
    /// Interleaved ring buffer, `capacity_frames * channels` samples; empty at zero delay.
    ring: Vec<f32>,
    /// Ring capacity in frames (`delay_frames + 1`).
    capacity_frames: usize,
    /// Channel count the ring is currently laid out for.
    channels: usize,
    /// Next frame slot to write.
    write_frame: usize,
    /// Active delay in frames.
    delay_frames: usize,
}

impl PdcDelay {
    fn new() -> Self {
        Self {
            ring: Vec::new(),
            capacity_frames: 0,
            channels: 0,
            write_frame: 0,
            delay_frames: 0,
        }
    }

    /// Sets the compensation delay in frames. Clears the delay line on a change,
    /// since the buffered history belongs to the old delay length.
    fn set_delay(&mut self, frames: usize) {
        if frames != self.delay_frames {
            self.delay_frames = frames;
            self.write_frame = 0;
            self.ring.iter_mut().for_each(|s| *s = 0.0);
        }
    }

    /// Clears the buffered history without changing the delay length.
    fn reset(&mut self) {
        self.write_frame = 0;
        self.ring.iter_mut().for_each(|s| *s = 0.0);
    }

    /// Delays `buf` in place by `delay_frames`. Zero delay is a passthrough.
    fn process(&mut self, buf: &mut [f32], channels: u16) {
        let d = self.delay_frames;
        if d == 0 {
            return;
        }
        let ch = (channels as usize).max(1);
        let cap = d + 1;
        // Size the ring the first time (or when the delay/channel layout changes).
        // This is the same one-time, off-the-steady-path growth the render scratch
        // uses — it only happens when an effect is added or removed.
        if self.ring.len() != cap * ch || self.channels != ch || self.capacity_frames != cap {
            self.ring = vec![0.0f32; cap * ch];
            self.capacity_frames = cap;
            self.channels = ch;
            self.write_frame = 0;
        }

        let frames = buf.len() / ch;
        for f in 0..frames {
            let read_frame = (self.write_frame + cap - d) % cap;
            for c in 0..ch {
                let delayed = self.ring[read_frame * ch + c];
                self.ring[self.write_frame * ch + c] = buf[f * ch + c];
                buf[f * ch + c] = delayed;
            }
            self.write_frame = (self.write_frame + 1) % cap;
        }
    }
}

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
    /// Plugin delay compensation applied to this track's output so it aligns
    /// sample-accurately with the highest-latency track in the mixer.
    pdc: PdcDelay,
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
            pdc: PdcDelay::new(),
        }
    }

    /// Sets this track's plugin delay compensation in frames (the mixer computes
    /// `max_chain_latency − this_chain_latency`). Zero disables compensation.
    pub(crate) fn set_pdc_delay(&mut self, frames: u32) {
        self.pdc.set_delay(frames as usize);
    }

    /// Replaces the track's audio source, dropping any previous one in place.
    ///
    /// Resets the rendered-frame position: a new source restarts playback from
    /// its own frame zero.
    pub fn set_source(&mut self, source: Option<Box<dyn TrackSource>>) {
        self.source = source;
        self.shared.reset_rendered_frames();
        self.stretch.clear();
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
        self.stretch.clear();
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

        // Honor a pending seek: drop the stretch FIFO's pre-seek tail before rendering so the
        // jump is clean.
        if self.shared.take_stretch_flush() {
            self.stretch.clear();
            // Drop the compensation delay's pre-seek tail too, so the jump is clean
            // and no stale audio leaks out after a seek.
            self.pdc.reset();
        }

        // Route a live source through the SoundTouch stage when the tempo/pitch is engaged, or
        // whenever the track is marked always-on (file-backed tracks): keeping the FIFO warm
        // means a mid-playback tempo/pitch change lands on a primed processor instead of a cold
        // one, so it does not click from the latency refill a cold start would incur. Generic
        // mixer tracks at unity bypass the stage and stay bit-exact passthrough.
        let tempo = self.shared.tempo_ratio();
        let pitch = self.shared.pitch_semitones();
        let needs_stretch = self.shared.stretch_always_on()
            || (tempo - 1.0).abs() > 1e-4
            || pitch.abs() > 1e-4;
        let read = if self.source.is_none() {
            0
        } else if needs_stretch {
            self.stretch
                .fill(&mut self.source, buf, channels, tempo, pitch)
        } else {
            self.source.as_mut().unwrap().read(buf)
        };
        for s in &mut buf[read..] {
            *s = 0.0;
        }

        self.effects.process_all(buf, channels);

        // Plugin delay compensation: delay this track's output so it lines up
        // sample-accurately with the highest-latency track. A zero delay (the
        // common case) is a passthrough.
        self.pdc.process(buf, channels);

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

        // Advance the rendered position by the number of *output* frames produced this block
        // (wall-clock time). The multi-track master clock is driven from this position and must
        // advance at the real playback rate regardless of per-track tempo — otherwise a stretched
        // track would run the shared clock at its content rate and desync every other track
        // (e.g. a unity metronome) against it.
        self.shared.add_rendered_frames((frame_len / ch) as u64);
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
    fn always_on_routes_unity_through_stretch_but_default_bypasses() {
        let block = 1024usize;

        // Default (always-on off) at unity: bit-exact passthrough of the source.
        let mut bypass = Track::new(30, 48_000.0, block);
        bypass.set_source(Some(Box::new(ConstSource(0.5))));
        bypass.shared.set_state(TrackState::Playing);
        let mut out = vec![0.0f32; block];
        bypass.process_additive(&mut out, 2);
        assert!(
            out.iter().all(|&v| (v - 0.5).abs() < 1e-6),
            "generic unity track must stay bit-exact passthrough"
        );

        // always-on at unity: routes through the (warming) SoundTouch FIFO, so the first block
        // is not the flat passthrough a bypass would produce.
        let mut always = Track::new(31, 48_000.0, block);
        always.set_source(Some(Box::new(ConstSource(0.5))));
        always.shared.set_state(TrackState::Playing);
        always.shared.set_stretch_always_on(true);
        let mut out2 = vec![0.0f32; block];
        always.process_additive(&mut out2, 2);
        assert!(
            !out2.iter().all(|&v| (v - 0.5).abs() < 1e-6),
            "always-on unity track must route through the stretch, not passthrough"
        );
    }

    #[test]
    fn rendered_position_is_wall_clock_regardless_of_tempo() {
        // The rendered position must advance by the OUTPUT frame count (wall-clock), not the
        // content rate, so a stretched track cannot drag the shared master clock off the real
        // playback rate and desync every other track (e.g. a unity metronome) against it.
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

        let expected = (block as u64 / 2) * blocks; // output frames, independent of tempo
        assert_eq!(
            track.shared.rendered_frames(),
            expected,
            "rendered position must be wall-clock (output frames), not tempo-scaled"
        );
    }

    #[test]
    fn tempo_pitch_latch_always_on_but_unity_stays_bypass() {
        let s = TrackShared::new();
        assert!(!s.stretch_always_on());

        // Unity tempo/pitch (e.g. a metronome whose tempo is baked into its audio) must never
        // latch always-on, so the track stays a clean bypass.
        s.set_tempo_ratio(1.0);
        s.set_pitch_semitones(0.0);
        assert!(
            !s.stretch_always_on(),
            "unity tempo/pitch must not latch always-on (metronome stays bypass)"
        );

        // The first non-unity value latches always-on...
        s.set_pitch_semitones(3.0);
        assert!(s.stretch_always_on());

        // ...and it stays latched even after returning to unity, so a later change lands on a
        // warm FIFO instead of cold-starting.
        s.set_pitch_semitones(0.0);
        assert!(
            s.stretch_always_on(),
            "always-on must persist once latched so later changes stay warm"
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
