//! Single audio track: an audio source, a shared atomic parameter block, and
//! an effect chain.

use std::sync::atomic::{AtomicBool, AtomicU32, AtomicU64, AtomicU8, AtomicUsize, Ordering};
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

/// Maximum number of source channels a per-track output-channel routing map can
/// cover. A map assigns each source channel `i` (`i < route_len`) to a physical
/// output channel; source channels beyond this cap are dropped. Sixteen is far
/// above any realistic per-track source width while keeping [`TrackShared`] a
/// fixed, allocation-free size.
pub const MAX_ROUTE_CHANNELS: usize = 16;

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

    /// Returns `true` only when the source has permanently reached end-of-stream
    /// (fully decoded and drained), as opposed to a transient underrun or an
    /// in-flight seek that also make [`TrackSource::read`] return zero.
    ///
    /// The stretch stage uses this to decide whether a zero read is a real EOF
    /// that should flush the SoundTouch FIFO tail, or a momentary dry spell that
    /// must not. The default returns `false`, so sources without a meaningful
    /// end-of-stream (e.g. a live ring-buffer feed) are never auto-flushed on an
    /// underrun.
    #[inline]
    fn is_eof(&self) -> bool {
        false
    }
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
    /// Stereo pan position as `f32` bits, in `[-1.0, +1.0]` (`0.0` = center,
    /// `-1.0` = hard left, `+1.0` = hard right). Applied per source channel by an
    /// equal-power law normalized to unity at center (see [`equal_power_pan`]), so
    /// a centered track passes through unchanged.
    pan_bits: AtomicU32,
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
    /// When `true`, the stretch stage is *pinned* on for this track's whole lifetime: it routes
    /// through SoundTouch from the first block and the opportunistic unity-silence release (see
    /// [`Track::process_additive`]) never drops it back to bypass. Set once by the owning source
    /// when it binds a tempo/pitch-capable track (a file source), so every tempo/pitch change —
    /// including the very first departure from unity — lands on a warm FIFO with a constant,
    /// PDC-aligned latency, instead of switching in from the zero-latency bypass path (which
    /// clicks, comb-filters against the bypass tail, and desyncs the track from the others).
    /// Distinct from [`TrackShared::stretch_always_on`], which auto-latches on a live change and
    /// may be released during silence.
    stretch_pinned: AtomicBool,
    /// Number of output frames this track has actually rendered into the mix
    /// since the last position reset (seek or source swap).
    ///
    /// Advanced on the audio thread by [`Track::process_additive`] with the block's
    /// output frame count; read from the control thread as the track's authoritative
    /// playback position. This is the *rendered* position, which lags the *fed*
    /// position by the ring-buffer depth — it is what an accurate `Position` must use.
    rendered_frames: AtomicU64,
    /// Content (source-timeline) frames the track has advanced through since the last
    /// position reset, as `f64` bits.
    ///
    /// Whereas [`TrackShared::rendered_frames`] counts *output* frames (wall-clock time,
    /// tempo-independent, and the right quantity to drive the shared master clock), this
    /// counts the *source content* consumed: each rendered block advances it by
    /// `output_frames × tempo_ratio`, integrating a live tempo change sample-accurately.
    /// It is the tempo-aware playback position a file source reports as its content-time
    /// `Position`, matching the legacy managed chain (where the position advanced by
    /// `frames_read × tempo`). Stored as `f64` bits because the per-block advance is
    /// fractional; written only by the audio thread, so the load/store round-trip needs
    /// no CAS.
    content_frames_bits: AtomicU64,
    /// Most recent left-channel output peak of this track's own contribution
    /// (post effect-chain and post-gain) as `f32` bits. Written by the audio
    /// thread every rendered block; read from the control thread for per-track
    /// metering / level indicators.
    peak_l_bits: AtomicU32,
    /// Most recent right-channel output peak of this track's own contribution as
    /// `f32` bits (equals the left peak on a mono track).
    peak_r_bits: AtomicU32,
    /// Number of output frames the track must emit as silence — without reading
    /// its source — before it begins contributing, so a track can be delayed
    /// against the shared clock to realise a positive per-track start offset.
    /// Set by the control thread; consumed once by the audio thread via the
    /// [`TrackShared::start_silence_pending`] latch.
    pending_start_silence: AtomicU64,
    /// Latch: `true` when the control thread has written a new
    /// [`TrackShared::pending_start_silence`] the audio thread has not yet taken.
    start_silence_pending: AtomicBool,
    /// Number of active entries in [`TrackShared::route_map`], i.e. how many
    /// source channels the per-track output routing covers. `0` means no routing:
    /// the track sums straight through, source channel `i` → output channel `i`
    /// (the identity fast path, bit-for-bit as before). A non-zero value routes
    /// source channel `i` (`i < route_len`) to output channel `route_map[i]`, and
    /// any output channel not named by the map receives no contribution from this
    /// track (silence), matching the managed mixer's selective channel routing.
    route_len: AtomicUsize,
    /// Per-source-channel destination output-channel indices, valid for the first
    /// [`TrackShared::route_len`] entries. Written by the control thread (map first,
    /// `route_len` last with `Release`) and read by the audio thread (`route_len`
    /// first with `Acquire`), so the audio thread never observes a half-written map.
    route_map: [AtomicU32; MAX_ROUTE_CHANNELS],
}

impl TrackShared {
    /// Creates a parameter block at unity gain, stopped, unmuted, unsoloed.
    pub fn new() -> Self {
        Self {
            state: AtomicU8::new(TrackState::Stopped as u8),
            gain_bits: AtomicU32::new(1.0f32.to_bits()),
            pan_bits: AtomicU32::new(0.0f32.to_bits()),
            muted: AtomicBool::new(false),
            soloed: AtomicBool::new(false),
            tempo_ratio_bits: AtomicU32::new(1.0f32.to_bits()),
            pitch_semitones_bits: AtomicU32::new(0.0f32.to_bits()),
            stretch_flush_pending: AtomicBool::new(false),
            stretch_always_on: AtomicBool::new(false),
            stretch_pinned: AtomicBool::new(false),
            rendered_frames: AtomicU64::new(0),
            content_frames_bits: AtomicU64::new(0.0f64.to_bits()),
            peak_l_bits: AtomicU32::new(0.0f32.to_bits()),
            peak_r_bits: AtomicU32::new(0.0f32.to_bits()),
            pending_start_silence: AtomicU64::new(0),
            start_silence_pending: AtomicBool::new(false),
            route_len: AtomicUsize::new(0),
            route_map: std::array::from_fn(|_| AtomicU32::new(0)),
        }
    }

    /// Installs a per-track output-channel routing map: source channel `i` is
    /// summed into output channel `map[i]` (for `i < map.len()`), and any output
    /// channel not named receives no contribution from this track. Passing an
    /// empty slice is equivalent to [`TrackShared::clear_output_channel_map`].
    ///
    /// Entries beyond [`MAX_ROUTE_CHANNELS`] are ignored. The map is disabled for
    /// the instant it is being rewritten (`route_len` dropped to `0` first, then
    /// restored last) so the audio thread only ever sees a fully-written map or
    /// none — never a torn one.
    pub fn set_output_channel_map(&self, map: &[u32]) {
        self.route_len.store(0, Ordering::Release);
        let n = map.len().min(MAX_ROUTE_CHANNELS);
        for (i, &v) in map.iter().take(n).enumerate() {
            self.route_map[i].store(v, Ordering::Relaxed);
        }
        self.route_len.store(n, Ordering::Release);
    }

    /// Clears any per-track output-channel routing, returning the track to the
    /// straight-through identity mix (source channel `i` → output channel `i`).
    pub fn clear_output_channel_map(&self) {
        self.route_len.store(0, Ordering::Release);
    }

    /// Copies the active routing map into `out` and returns its length (`0` when
    /// no routing is set). Called on the audio thread once per block.
    #[inline]
    pub fn load_output_channel_map(&self, out: &mut [u32; MAX_ROUTE_CHANNELS]) -> usize {
        let n = self
            .route_len
            .load(Ordering::Acquire)
            .min(MAX_ROUTE_CHANNELS);
        for (i, slot) in out.iter_mut().enumerate().take(n) {
            *slot = self.route_map[i].load(Ordering::Relaxed);
        }
        n
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

    /// Returns the stereo pan position, in `[-1.0, +1.0]` (`0.0` = center).
    #[inline]
    pub fn pan(&self) -> f32 {
        f32::from_bits(self.pan_bits.load(Ordering::Relaxed))
    }

    /// Sets the stereo pan position (clamped to `[-1.0, +1.0]`).
    #[inline]
    pub fn set_pan(&self, pan: f32) {
        self.pan_bits
            .store(pan.clamp(-1.0, 1.0).to_bits(), Ordering::Relaxed);
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
        self.tempo_ratio_bits
            .store(clamped.to_bits(), Ordering::Relaxed);
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
        self.pitch_semitones_bits
            .store(clamped.to_bits(), Ordering::Relaxed);
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

    /// Returns whether the stretch stage is pinned on for the track's lifetime (see
    /// `stretch_pinned`), i.e. it always routes through SoundTouch and is never released to bypass.
    #[inline]
    pub fn stretch_pinned(&self) -> bool {
        self.stretch_pinned.load(Ordering::Relaxed)
    }

    /// Pins the stretch stage on (or off) for the track's whole lifetime. Called by a
    /// tempo/pitch-capable source when it binds the track, so the first tempo/pitch change lands
    /// on a warm FIFO with constant latency instead of switching in from the zero-latency bypass
    /// path. Pinning also latches `stretch_always_on` so the stage is engaged from the first block.
    #[inline]
    pub fn set_stretch_pinned(&self, on: bool) {
        self.stretch_pinned.store(on, Ordering::Relaxed);
        if on {
            self.stretch_always_on.store(true, Ordering::Relaxed);
        }
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

    /// Returns the content (source-timeline) frames advanced since the last position reset.
    ///
    /// This is the tempo-integrated position (`Σ output_frames × tempo`), used for the
    /// tempo-aware content-time `Position`; see [`TrackShared::content_frames_bits`].
    #[inline]
    pub fn content_frames(&self) -> f64 {
        f64::from_bits(self.content_frames_bits.load(Ordering::Relaxed))
    }

    /// Advances the content-frame counter by `frames` (called on the audio thread with
    /// `output_frames × tempo_ratio`). Single-writer: a plain load/store is sufficient.
    #[inline]
    pub fn add_content_frames(&self, frames: f64) {
        let next = f64::from_bits(self.content_frames_bits.load(Ordering::Relaxed)) + frames;
        self.content_frames_bits
            .store(next.to_bits(), Ordering::Relaxed);
    }

    /// Resets the content-frame counter to zero (on seek or source swap), kept in lock-step
    /// with [`TrackShared::reset_rendered_frames`].
    #[inline]
    pub fn reset_content_frames(&self) {
        self.content_frames_bits
            .store(0.0f64.to_bits(), Ordering::Relaxed);
    }

    /// Returns the most recent left-channel output peak of this track.
    #[inline]
    pub fn peak_l(&self) -> f32 {
        f32::from_bits(self.peak_l_bits.load(Ordering::Relaxed))
    }

    /// Returns the most recent right-channel output peak of this track.
    #[inline]
    pub fn peak_r(&self) -> f32 {
        f32::from_bits(self.peak_r_bits.load(Ordering::Relaxed))
    }

    /// Stores this block's measured left/right output peaks (audio thread).
    #[inline]
    pub fn store_peaks(&self, left: f32, right: f32) {
        self.peak_l_bits.store(left.to_bits(), Ordering::Relaxed);
        self.peak_r_bits.store(right.to_bits(), Ordering::Relaxed);
    }

    /// Requests that the track emit `frames` output frames of silence — not reading
    /// its source — before it begins contributing (control thread). Realises a
    /// positive per-track start offset: the track is held silent against the shared
    /// clock, then enters from its source's current position.
    #[inline]
    pub fn request_start_silence(&self, frames: u64) {
        self.pending_start_silence.store(frames, Ordering::Relaxed);
        self.start_silence_pending.store(true, Ordering::Relaxed);
    }

    /// Atomically consumes a pending start-silence request, returning the requested
    /// frame count when one was set (audio thread, at the top of a render block).
    #[inline]
    pub fn take_start_silence(&self) -> Option<u64> {
        if self.start_silence_pending.swap(false, Ordering::Relaxed) {
            Some(self.pending_start_silence.load(Ordering::Relaxed))
        } else {
            None
        }
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
    /// Per-frame pan smoother; ramps the atomic [`TrackShared::pan`] position toward
    /// its latest value so a live pan move sweeps rather than clicks. The smoothed
    /// position is mapped to per-channel gains by [`equal_power_pan`].
    pan_smoother: SmoothedParam,
    /// Per-track time-stretch / pitch-shift stage; applies the track's tempo and pitch to the
    /// source audio via SoundTouch, or bypasses transparently at unity.
    stretch: super::stretch::TrackStretch,
    /// Plugin delay compensation applied to this track's output so it aligns
    /// sample-accurately with the highest-latency track in the mixer.
    pdc: PdcDelay,
    /// Remaining output frames of start-offset silence to emit before the track
    /// begins reading its source (audio-thread-owned). Loaded from
    /// [`TrackShared::take_start_silence`] and counted down per block; zero once the
    /// track has entered.
    start_silence_remaining: u64,
    /// Consecutive rendered frames the track has spent at unity tempo/pitch while
    /// `stretch_always_on` was latched (audio-thread-owned). Once this exceeds
    /// [`Track::unlatch_after_frames`] and the output is quiet, the latch is released so
    /// the track drops back to a cheaper, lower-latency bypass — see [`Track::process_additive`].
    unity_run_frames: u64,
    /// Frame count of sustained unity after which the always-on latch becomes eligible for
    /// release. Derived from the sample rate at construction (~1 second).
    unlatch_after_frames: u64,
}

/// Output peak below which a block counts as effectively silent, so releasing the always-on
/// stretch latch there drops only inaudible FIFO tail (no click). ≈ −80 dBFS.
const UNLATCH_SILENCE_EPS: f32 = 1.0e-4;

/// Tempo/pitch deadband within which the stretch is considered to be at unity, matching the
/// engage threshold used for `needs_stretch`.
const UNITY_EPS: f32 = 1.0e-4;

/// Tolerance within which the gain smoother is treated as settled at its target, enabling the
/// unity-gain fast path in the per-track mix loop.
const GAIN_SETTLE_EPS: f32 = 1.0e-6;

/// Tolerance within which the pan smoother is treated as settled at its target, so the per-frame
/// pan gains can be computed once per block instead of re-evaluating the trig each frame.
const PAN_SETTLE_EPS: f32 = 1.0e-6;

/// Half-width of the pan deadband around center within which the track is considered centered,
/// enabling the full-bypass fast path in the per-track mix loop.
const PAN_CENTER_EPS: f32 = 1.0e-6;

/// Maps a stereo pan position `p` in `[-1.0, +1.0]` to `(left, right)` channel gains under an
/// equal-power law normalized to unity at center: `p == 0` returns `(1.0, 1.0)` so an un-panned
/// track passes through unchanged, while `p` sweeps the image along a constant-power curve
/// (`left² + right² == 2` for all `p`) — full-left `p == -1` → `(√2, 0)`, full-right
/// `p == +1` → `(0, √2)`.
#[inline]
pub(crate) fn equal_power_pan(p: f32) -> (f32, f32) {
    let angle = (p.clamp(-1.0, 1.0) + 1.0) * std::f32::consts::FRAC_PI_4;
    (
        std::f32::consts::SQRT_2 * angle.cos(),
        std::f32::consts::SQRT_2 * angle.sin(),
    )
}

impl Track {
    /// Creates a new idle track with the given id, sample rate (for gain
    /// smoothing), interleaved channel count, and pre-sized scratch buffer.
    ///
    /// `channels` is the mixer's output channel count — the same count the mixer
    /// passes to [`Track::process_additive`] every block. Passing it here lets the
    /// per-track SoundTouch stretch stage build and warm up its processor on this
    /// (control) thread, so the audio thread never pays the FIR-build / allocation
    /// cost on the first tempo/pitch change.
    pub fn new(id: u64, sample_rate: f32, channels: u16, max_buffer_size: usize) -> Self {
        let shared = Arc::new(TrackShared::new());
        let gain_smoother = SmoothedParam::new(shared.gain(), sample_rate, GAIN_SMOOTH_MS);
        let pan_smoother = SmoothedParam::new(shared.pan(), sample_rate, GAIN_SMOOTH_MS);
        Self {
            id,
            shared,
            effects: EffectChain::with_capacity(MAX_EFFECTS_PER_TRACK),
            source: None,
            scratch: vec![0.0f32; max_buffer_size],
            gain_smoother,
            pan_smoother,
            stretch: super::stretch::TrackStretch::new(sample_rate, channels, max_buffer_size),
            pdc: PdcDelay::new(),
            start_silence_remaining: 0,
            unity_run_frames: 0,
            unlatch_after_frames: (sample_rate as u64).max(1),
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
        self.shared.reset_content_frames();
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
        self.shared.reset_content_frames();
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

        let ch = channels.max(1) as usize;
        let total_frames = frame_len / ch;

        // Consume a pending start-offset silence request (control thread) once, then
        // emit that many output frames of silence at the head of the block WITHOUT
        // reading the source. This delays the track against the shared clock to
        // realise a positive per-track start offset (the managed engine's
        // "masterTimestamp − startOffset < 0 ⇒ silence" behaviour), sample-accurately:
        // the silent frames get no contribution (other tracks' sums are preserved) and
        // the source is not advanced, so the track enters from its current position.
        if let Some(n) = self.shared.take_start_silence() {
            self.start_silence_remaining = n;
        }
        let sil = self.start_silence_remaining.min(total_frames as u64) as usize;
        if sil > 0 {
            self.start_silence_remaining -= sil as u64;
        }
        let active_frames = total_frames - sil;
        if active_frames == 0 {
            // The whole block is start-offset silence: nothing is rendered and the
            // rendered position stays frozen until the track enters.
            self.shared.store_peaks(0.0, 0.0);
            return;
        }

        let active_samples = active_frames * ch;
        let out_off = sil * ch;
        let buf = &mut self.scratch[..active_samples];

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
        let always_on = self.shared.stretch_always_on();
        let pinned = self.shared.stretch_pinned();
        let at_unity = (tempo - 1.0).abs() <= UNITY_EPS && pitch.abs() <= UNITY_EPS;
        let needs_stretch = pinned || always_on || !at_unity;
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
        self.pan_smoother.set_target(self.shared.pan());
        // Peak of this track's own (post-gain, post-pan) contribution, per channel,
        // for per-track metering. Measured on the scaled sample, not the raw source,
        // so a muted-down track reads a low level as a user would expect.
        let mut peak_l = 0.0f32;
        let mut peak_r = 0.0f32;
        // Advance the gain and pan once per frame so both channels of a stereo frame
        // share the same (smoothed) gain. The active region is written starting at
        // `out_off` so any start-offset silence prefix is left untouched.
        let out_active = &mut output[out_off..out_off + active_samples];

        // Per-track output-channel routing: an empty map (the common case) sums the
        // track straight through (source channel i → output channel i); a non-empty
        // map routes source channel `c` to output channel `route_buf[c]` and leaves
        // every unmapped output channel silent for this track — the native port of
        // the managed mixer's selective channel routing. The map is snapshotted once
        // per block onto the stack (allocation-free).
        let mut route_buf = [0u32; MAX_ROUTE_CHANNELS];
        let route_len = self.shared.load_output_channel_map(&mut route_buf);

        let settled_unity = self.gain_smoother.is_settled(GAIN_SETTLE_EPS)
            && (self.gain_smoother.target() - 1.0).abs() <= GAIN_SETTLE_EPS;
        // Pan is centered when the smoother has both settled and rests at 0.0; only then does the
        // full-bypass fast path apply (an un-panned, unity-gain track passes through bit-for-bit).
        let pan_centered = self.pan_smoother.is_settled(PAN_SETTLE_EPS)
            && self.pan_smoother.target().abs() <= PAN_CENTER_EPS;
        let bypass = settled_unity && pan_centered;
        // When the pan smoother is settled the per-channel pan gains are constant for the whole
        // block, so compute the trig once here; otherwise they are recomputed per frame as the
        // position ramps.
        let pan_settled = self.pan_smoother.is_settled(PAN_SETTLE_EPS);
        let (const_pan_l, const_pan_r) = equal_power_pan(self.pan_smoother.current());

        if route_len == 0 {
            // Fast path: once gain has settled at unity and pan is centered, every frame would
            // multiply by 1.0 and re-advance no-op ramps. Skip both with a flat additive copy the
            // compiler can vectorise cleanly, measuring the per-channel peak in a separate pass.
            if bypass {
                for (o, &s) in out_active.iter_mut().zip(buf.iter()) {
                    *o += s;
                }
                for frame in buf.chunks(ch) {
                    let l = frame[0].abs();
                    if l > peak_l {
                        peak_l = l;
                    }
                    if ch > 1 {
                        let r = frame[1].abs();
                        if r > peak_r {
                            peak_r = r;
                        }
                    }
                }
            } else {
                for (out_frame, in_frame) in out_active.chunks_mut(ch).zip(buf.chunks(ch)) {
                    let gain = self.gain_smoother.advance();
                    let p = self.pan_smoother.advance();
                    let (pan_l, pan_r) = if pan_settled {
                        (const_pan_l, const_pan_r)
                    } else {
                        equal_power_pan(p)
                    };
                    for (i, (o, &s)) in out_frame.iter_mut().zip(in_frame.iter()).enumerate() {
                        // Pan positions source channel 0 (left) and 1 (right); any further
                        // channel carries gain only (there is no meaningful stereo image for it).
                        let ch_pan = if i == 0 {
                            pan_l
                        } else if i == 1 {
                            pan_r
                        } else {
                            1.0
                        };
                        let scaled = s * gain * ch_pan;
                        *o += scaled;
                        let abs = scaled.abs();
                        if i == 0 {
                            if abs > peak_l {
                                peak_l = abs;
                            }
                        } else if i == 1 && abs > peak_r {
                            peak_r = abs;
                        }
                    }
                }
            }
        } else {
            // Selective routing. Only source channels the map covers are placed, and
            // only onto in-range output channels; the source width is `ch` (the track
            // decodes at the mixer's channel count), so cap the map at `ch`.
            let n = route_len.min(ch);
            if bypass {
                for (out_frame, in_frame) in out_active.chunks_mut(ch).zip(buf.chunks(ch)) {
                    for c in 0..n {
                        let dst = route_buf[c] as usize;
                        if dst < ch {
                            out_frame[dst] += in_frame[c];
                        }
                    }
                    let l = in_frame[0].abs();
                    if l > peak_l {
                        peak_l = l;
                    }
                    if ch > 1 {
                        let r = in_frame[1].abs();
                        if r > peak_r {
                            peak_r = r;
                        }
                    }
                }
            } else {
                for (out_frame, in_frame) in out_active.chunks_mut(ch).zip(buf.chunks(ch)) {
                    let gain = self.gain_smoother.advance();
                    let p = self.pan_smoother.advance();
                    let (pan_l, pan_r) = if pan_settled {
                        (const_pan_l, const_pan_r)
                    } else {
                        equal_power_pan(p)
                    };
                    for c in 0..n {
                        let dst = route_buf[c] as usize;
                        if dst < ch {
                            // Pan by source channel index, so a routed left/right source
                            // channel keeps its pan weight regardless of its destination.
                            let ch_pan = if c == 0 {
                                pan_l
                            } else if c == 1 {
                                pan_r
                            } else {
                                1.0
                            };
                            out_frame[dst] += in_frame[c] * gain * ch_pan;
                        }
                    }
                    let l = (in_frame[0] * gain * pan_l).abs();
                    if l > peak_l {
                        peak_l = l;
                    }
                    if ch > 1 {
                        let r = (in_frame[1] * gain * pan_r).abs();
                        if r > peak_r {
                            peak_r = r;
                        }
                    }
                }
            }
        }
        if ch == 1 {
            peak_r = peak_l;
        }
        self.shared.store_peaks(peak_l, peak_r);

        // Opportunistically release the always-on stretch latch once the track has run at unity
        // tempo/pitch for a sustained period AND the current output is effectively silent. The
        // latch exists so a live tempo/pitch change lands on a warm FIFO without clicking; but a
        // track that latched on a one-off change (or a transient network-sync tempo nudge) and
        // then sat at unity would otherwise run the full WSOLA path forever — added latency and
        // CPU for no benefit. Releasing only during a quiet moment means the dropped FIFO tail is
        // inaudible, so the return to the cheaper bypass path is click-free. A later non-unity
        // tempo/pitch simply re-latches it.
        if always_on && at_unity && !pinned {
            self.unity_run_frames = self.unity_run_frames.saturating_add(active_frames as u64);
            if self.unity_run_frames >= self.unlatch_after_frames
                && peak_l < UNLATCH_SILENCE_EPS
                && peak_r < UNLATCH_SILENCE_EPS
            {
                self.shared.set_stretch_always_on(false);
                self.stretch.clear();
                self.unity_run_frames = 0;
            }
        } else {
            self.unity_run_frames = 0;
        }

        // Advance the rendered position by the number of *output* frames actually produced
        // this block (wall-clock time), excluding any start-offset silence prefix. The
        // multi-track master clock is driven from this position and must advance at the real
        // playback rate regardless of per-track tempo — otherwise a stretched track would run
        // the shared clock at its content rate and desync every other track against it.
        self.shared.add_rendered_frames(active_frames as u64);

        // Advance the content (source-timeline) position by the same output frames scaled by
        // the tempo active for this block. At tempo `r` the stretch stage consumed ~`r` source
        // frames per output frame, so this integrates the live tempo into a content-time
        // position that tracks the audio actually heard — the tempo-aware quantity a file
        // source reports as its `Position`, matching the legacy chain. At unity (bypass) the
        // factor is 1.0, so it advances identically to the rendered position.
        self.shared
            .add_content_frames(active_frames as f64 * tempo as f64);
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
        let mut track = Track::new(1, 48_000.0, 2, 64);
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
        let mut track = Track::new(2, 48_000.0, 2, 64);
        track.shared.set_state(TrackState::Playing);

        let mut out = vec![0.0f32; 8];
        track.process_additive(&mut out, 2);
        assert_eq!(track.shared.rendered_frames(), 4);
    }

    #[test]
    fn source_swap_resets_rendered_frames() {
        let mut track = Track::new(3, 48_000.0, 2, 64);
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

    #[test]
    fn content_frames_track_rendered_at_unity_tempo() {
        // At unity tempo the content position advances identically to the rendered
        // (output) position: one content frame per output frame.
        let mut track = Track::new(10, 48_000.0, 2, 64);
        track.set_source(Some(Box::new(ConstSource(0.5))));
        track.shared.set_state(TrackState::Playing);

        let mut out = vec![0.0f32; 8]; // 4 stereo frames
        track.process_additive(&mut out, 2);
        assert_eq!(track.shared.rendered_frames(), 4);
        assert_eq!(track.shared.content_frames(), 4.0);

        track.process_additive(&mut out, 2);
        assert_eq!(track.shared.rendered_frames(), 8);
        assert_eq!(track.shared.content_frames(), 8.0);
    }

    #[test]
    fn content_frames_scale_with_tempo_while_rendered_stays_realtime() {
        // At tempo 1.5 the rendered (output/wall-clock) position still advances by the
        // output frame count, but the content position advances 1.5× as fast, integrating
        // the source content consumed by the time-stretch — the tempo-aware `Position`.
        let mut track = Track::new(11, 48_000.0, 2, 64);
        track.set_source(Some(Box::new(ConstSource(0.5))));
        track.shared.set_state(TrackState::Playing);
        track.shared.set_tempo_ratio(1.5);

        let mut out = vec![0.0f32; 8]; // 4 stereo frames
        track.process_additive(&mut out, 2);
        assert_eq!(track.shared.rendered_frames(), 4);
        assert!((track.shared.content_frames() - 6.0).abs() < 1e-9);

        // A live tempo change integrates piecewise: the next block adds 4 × 0.5 = 2.
        track.shared.set_tempo_ratio(0.5);
        track.process_additive(&mut out, 2);
        assert_eq!(track.shared.rendered_frames(), 8);
        assert!((track.shared.content_frames() - 8.0).abs() < 1e-9);
    }

    #[test]
    fn source_swap_and_reset_zero_content_frames() {
        let mut track = Track::new(12, 48_000.0, 2, 64);
        track.set_source(Some(Box::new(ConstSource(0.5))));
        track.shared.set_state(TrackState::Playing);
        track.shared.set_tempo_ratio(1.25);

        let mut out = vec![0.0f32; 8];
        track.process_additive(&mut out, 2);
        assert!(track.shared.content_frames() > 0.0);

        let _prev = track.replace_source(Some(Box::new(ConstSource(0.25))));
        assert_eq!(track.shared.content_frames(), 0.0);

        track.process_additive(&mut out, 2);
        assert!(track.shared.content_frames() > 0.0);
        track.shared.reset_content_frames();
        assert_eq!(track.shared.content_frames(), 0.0);
    }

    #[test]
    fn output_channel_map_routes_and_silences_unmapped() {
        // 4-channel output bus; route the source onto physical output channels 2 and 3.
        let mut track = Track::new(20, 48_000.0, 4, 64);
        track.set_source(Some(Box::new(ConstSource(0.5))));
        track.shared.set_state(TrackState::Playing);
        track.shared.set_output_channel_map(&[2, 3]);

        let mut out = vec![0.0f32; 8]; // 2 quad frames
        track.process_additive(&mut out, 4);

        // Unmapped channels (0, 1) receive nothing; mapped channels (2, 3) get the source.
        for frame in out.chunks(4) {
            assert_eq!(frame[0], 0.0);
            assert_eq!(frame[1], 0.0);
            assert_eq!(frame[2], 0.5);
            assert_eq!(frame[3], 0.5);
        }
    }

    #[test]
    fn output_channel_map_is_additive_across_tracks() {
        // Two tracks routed to disjoint output channels must not overwrite each other's
        // contribution — the additive-mix contract must hold through the routing path.
        let mut a = Track::new(22, 48_000.0, 4, 64);
        a.set_source(Some(Box::new(ConstSource(0.25))));
        a.shared.set_state(TrackState::Playing);
        a.shared.set_output_channel_map(&[0, 1]);

        let mut b = Track::new(23, 48_000.0, 4, 64);
        b.set_source(Some(Box::new(ConstSource(0.75))));
        b.shared.set_state(TrackState::Playing);
        b.shared.set_output_channel_map(&[2, 3]);

        let mut out = vec![0.0f32; 4]; // 1 quad frame
        a.process_additive(&mut out, 4);
        b.process_additive(&mut out, 4);

        assert_eq!(out, vec![0.25, 0.25, 0.75, 0.75]);
    }

    #[test]
    fn cleared_output_channel_map_restores_identity() {
        let mut track = Track::new(21, 48_000.0, 2, 64);
        track.set_source(Some(Box::new(ConstSource(0.5))));
        track.shared.set_state(TrackState::Playing);
        track.shared.set_output_channel_map(&[1, 0]);
        track.shared.clear_output_channel_map();

        let mut out = vec![0.0f32; 4];
        track.process_additive(&mut out, 2);
        // With routing cleared the track sums straight through: every channel gets the source.
        assert!(out.iter().all(|&s| (s - 0.5).abs() < 1e-6));
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
        let mut t_unity = Track::new(10, sr, 2, block);
        t_unity.set_source(Some(Box::new(CountingSource {
            read_samples: unity.clone(),
        })));
        t_unity.shared.set_state(TrackState::Playing);
        t_unity.shared.set_tempo_ratio(1.0);

        let fast = Arc::new(AtomicU64::new(0));
        let mut t_fast = Track::new(11, sr, 2, block);
        t_fast.set_source(Some(Box::new(CountingSource {
            read_samples: fast.clone(),
        })));
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
        let mut bypass = Track::new(30, 48_000.0, 2, block);
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
        let mut always = Track::new(31, 48_000.0, 2, block);
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
    fn always_on_unlatches_after_sustained_quiet_unity() {
        // A track that latched always-on (e.g. a one-off tempo change or a transient network-sync
        // nudge) but then sits at unity over a quiet passage must drop the latch, returning to the
        // cheaper bypass path. Releasing during silence keeps it click-free.
        let block = 1024usize; // 512 stereo frames per block
        let mut track = Track::new(60, 48_000.0, 2, block);
        track.set_source(Some(Box::new(ConstSource(0.0)))); // silent source
        track.shared.set_state(TrackState::Playing);
        track.shared.set_stretch_always_on(true);
        assert!(track.shared.stretch_always_on());

        let mut out = vec![0.0f32; block];
        // ~1.05 s of audio (48k / 512 ≈ 94 blocks); run more to clear the threshold with margin.
        for _ in 0..120 {
            out.iter_mut().for_each(|s| *s = 0.0);
            track.process_additive(&mut out, 2);
        }
        assert!(
            !track.shared.stretch_always_on(),
            "sustained quiet unity must release the always-on latch"
        );
    }

    #[test]
    fn always_on_stays_latched_while_output_is_audible() {
        // The latch must NOT release while the track is producing audible output, since dropping
        // the stretch FIFO tail there would click.
        let block = 1024usize;
        let mut track = Track::new(61, 48_000.0, 2, block);
        track.set_source(Some(Box::new(ConstSource(0.5)))); // audible source
        track.shared.set_state(TrackState::Playing);
        track.shared.set_stretch_always_on(true);

        let mut out = vec![0.0f32; block];
        for _ in 0..120 {
            out.iter_mut().for_each(|s| *s = 0.0);
            track.process_additive(&mut out, 2);
        }
        assert!(
            track.shared.stretch_always_on(),
            "an audible unity track must keep the latch (releasing would click)"
        );
    }

    #[test]
    fn pinned_stretch_survives_sustained_quiet_unity() {
        // A pinned track (a file source that binds the stretch stage for its lifetime) must keep
        // routing through SoundTouch even after a long quiet passage at unity — unlike the
        // auto-latch, the pin is never released. This is what stops the first tempo change from
        // switching in from the zero-latency bypass path and clicking/desyncing.
        let block = 1024usize;
        let mut track = Track::new(62, 48_000.0, 2, block);
        track.set_source(Some(Box::new(ConstSource(0.0)))); // silent source
        track.shared.set_state(TrackState::Playing);

        // Pinning latches always-on immediately so the stage is engaged from the first block.
        track.shared.set_stretch_pinned(true);
        assert!(track.shared.stretch_pinned());
        assert!(
            track.shared.stretch_always_on(),
            "pinning must latch always-on so the stage engages from block zero"
        );

        let mut out = vec![0.0f32; block];
        for _ in 0..120 {
            out.iter_mut().for_each(|s| *s = 0.0);
            track.process_additive(&mut out, 2);
        }
        assert!(
            track.shared.stretch_pinned(),
            "the pin must never be released"
        );
        assert!(
            track.shared.stretch_always_on(),
            "a pinned track must stay stretch-routed through sustained quiet unity"
        );
    }

    #[test]
    fn rendered_position_is_wall_clock_regardless_of_tempo() {
        // The rendered position must advance by the OUTPUT frame count (wall-clock), not the
        // content rate, so a stretched track cannot drag the shared master clock off the real
        // playback rate and desync every other track (e.g. a unity metronome) against it.
        let block = 1024usize; // 512 stereo frames per block
        let mut track = Track::new(20, 48_000.0, 2, block);
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
    fn per_track_peak_reflects_post_gain_contribution() {
        // A full-scale constant source at unity gain reports a peak near 1.0;
        // halving the gain halves the reported peak (post-gain metering).
        let block = 1024usize;
        let mut track = Track::new(40, 48_000.0, 2, block);
        track.set_source(Some(Box::new(ConstSource(1.0))));
        track.shared.set_state(TrackState::Playing);

        let mut out = vec![0.0f32; block];
        track.process_additive(&mut out, 2);
        assert!(
            (track.shared.peak_l() - 1.0).abs() < 1e-3,
            "peak_l {}",
            track.shared.peak_l()
        );
        assert!(
            (track.shared.peak_r() - 1.0).abs() < 1e-3,
            "peak_r {}",
            track.shared.peak_r()
        );

        // Settle the gain smoother at 0.5 over several blocks, then the peak
        // tracks the post-gain level.
        track.shared.set_gain(0.5);
        for _ in 0..8 {
            out.iter_mut().for_each(|s| *s = 0.0);
            track.process_additive(&mut out, 2);
        }
        assert!(
            (track.shared.peak_l() - 0.5).abs() < 1e-2,
            "peak_l {}",
            track.shared.peak_l()
        );
    }

    #[test]
    fn start_silence_delays_entry_then_plays_from_source_start() {
        // Request 4 frames of start-offset silence on a mono track rendering an
        // 8-frame block: the first 4 frames are silent, then the source plays from
        // its start, and only the non-silent frames advance the rendered position.
        let mut track = Track::new(50, 48_000.0, 1, 64);
        track.set_source(Some(Box::new(ConstSource(0.5))));
        track.shared.set_state(TrackState::Playing);
        track.shared.request_start_silence(4);

        let mut out = vec![0.0f32; 8];
        track.process_additive(&mut out, 1);
        assert_eq!(
            &out[..4],
            &[0.0, 0.0, 0.0, 0.0],
            "first 4 frames must be silent"
        );
        for &s in &out[4..] {
            assert!(
                (s - 0.5).abs() < 1e-6,
                "content must play after the silence, got {s}"
            );
        }
        assert_eq!(track.shared.rendered_frames(), 4);
    }

    #[test]
    fn start_silence_spanning_blocks_freezes_position_until_entry() {
        // 8 frames of start silence across two 4-frame blocks: both blocks are fully
        // silent and the rendered position stays frozen, then the third block enters.
        let mut track = Track::new(51, 48_000.0, 1, 64);
        track.set_source(Some(Box::new(ConstSource(1.0))));
        track.shared.set_state(TrackState::Playing);
        track.shared.request_start_silence(8);

        let mut out = vec![0.0f32; 4];
        track.process_additive(&mut out, 1);
        assert_eq!(out, [0.0, 0.0, 0.0, 0.0]);
        assert_eq!(track.shared.rendered_frames(), 0);

        out.iter_mut().for_each(|s| *s = 0.0);
        track.process_additive(&mut out, 1);
        assert_eq!(out, [0.0, 0.0, 0.0, 0.0]);
        assert_eq!(track.shared.rendered_frames(), 0);

        out.iter_mut().for_each(|s| *s = 0.0);
        track.process_additive(&mut out, 1);
        for &s in &out {
            assert!(
                (s - 1.0).abs() < 1e-6,
                "content must play once the silence is consumed"
            );
        }
        assert_eq!(track.shared.rendered_frames(), 4);
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
