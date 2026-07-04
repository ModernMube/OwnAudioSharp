//! Multi-track mixer with a shared sample-accurate transport clock.

pub mod clock;
pub mod command;
pub mod file_source;
pub mod track;

pub use clock::SampleClock;
pub use command::{command_channel, CommandReceiver, MixerCommand, MixerController, Retired};
pub use file_source::{FileSourceControl, FileTrackSource};
pub use track::{Track, TrackShared, TrackSource, TrackState};

use crate::effects::EffectEntry;
use rtrb::Producer;
use std::sync::Arc;

/// Default per-track scratch size in samples, used when the mixer is created
/// without an explicit block size.  Covers a 4096-frame stereo callback; larger
/// blocks grow the scratch once (amortised, never in steady state).
const DEFAULT_MAX_BUFFER: usize = 4096 * 2;

/// Maximum number of simultaneous tracks the mixer pre-allocates room for, so
/// adding a track (even via a drained command on the audio thread) never
/// reallocates the track vector.  Exceeding it is reported as an error rather
/// than growing the vector on the audio thread.
pub const MAX_TRACKS: usize = 256;

/// Central multi-track mixer.
///
/// Holds a collection of [`Track`]s and a shared [`SampleClock`].  On every
/// audio callback [`MultiTrackMixer::mix`] renders each active track into its
/// own scratch buffer, applies the track's effect chain and gain, and sums the
/// results **additively** into the output buffer before advancing the clock.
///
/// Track parameters (gain, mute, solo, state, tempo, pitch) live in each
/// track's [`TrackShared`] block and are mutated atomically by the control
/// thread, so parameter changes never race with the audio thread.  Tracks carry
/// stable [`Track::id`]s; lookups are by id, so a handle stays valid even after
/// other tracks are removed.
pub struct MultiTrackMixer {
    /// Tracks in insertion order, each with a stable id.  Pre-allocated to
    /// [`MAX_TRACKS`] so audio-thread inserts never reallocate.
    tracks: Vec<Track>,
    /// Shared sample-accurate clock.
    clock: SampleClock,
    /// Number of audio channels (1 = mono, 2 = stereo, …).
    channels: u16,
    /// Sample rate in Hz, handed to new tracks for gain-smoothing.
    sample_rate: f32,
    /// Monotonic id generator for new tracks (direct, non-queued path).
    next_id: u64,
    /// Monotonic id generator for new effects across all tracks (direct path).
    next_effect_id: u64,
    /// Scratch size handed to new tracks.
    max_buffer_size: usize,
    /// Optional lock-free command channel drained at the start of every
    /// [`MultiTrackMixer::mix`].  When present, structural changes (add/remove
    /// track, source, effects, effect params) arrive through it instead of via
    /// direct `&mut` mutation, so they never race the audio thread.
    commands: Option<CommandReceiver>,
}

impl MultiTrackMixer {
    /// Creates an empty mixer with a default per-track scratch size.
    pub fn new(sample_rate: f32, channels: u16) -> Self {
        Self::with_buffer_size(sample_rate, channels, DEFAULT_MAX_BUFFER)
    }

    /// Creates an empty mixer whose tracks pre-allocate `max_buffer_size`
    /// samples of scratch each.
    pub fn with_buffer_size(sample_rate: f32, channels: u16, max_buffer_size: usize) -> Self {
        Self {
            tracks: Vec::with_capacity(MAX_TRACKS),
            clock: SampleClock::new(sample_rate),
            channels,
            sample_rate,
            next_id: 0,
            next_effect_id: 0,
            max_buffer_size: max_buffer_size.max(1),
            commands: None,
        }
    }

    /// Sample rate the mixer was created with (Hz).
    pub fn sample_rate(&self) -> f32 {
        self.sample_rate
    }

    /// Number of interleaved channels.
    pub fn channels(&self) -> u16 {
        self.channels
    }

    /// Per-track scratch size handed to new tracks.
    pub fn max_buffer_size(&self) -> usize {
        self.max_buffer_size
    }

    /// Allocates the next stable effect id (direct, non-queued FFI path).
    ///
    /// Effect ids are unique across the mixer for its lifetime, so an effect
    /// handle keeps addressing the same effect even after sibling effects are
    /// removed.
    pub fn alloc_effect_id(&mut self) -> u64 {
        let id = self.next_effect_id;
        self.next_effect_id += 1;
        id
    }

    /// Attaches a [`CommandReceiver`] whose queued structural changes are
    /// drained — allocation-free — at the start of every [`MultiTrackMixer::mix`].
    ///
    /// Pairs with the [`MixerController`] returned by [`command_channel`].
    pub fn attach_command_receiver(&mut self, receiver: CommandReceiver) {
        self.commands = Some(receiver);
    }

    /// Appends a new track and returns its stable id together with a clone of
    /// its shared parameter block.
    ///
    /// The returned `Arc<TrackShared>` lets the caller mutate the track's
    /// parameters lock-free without holding a reference into the mixer.
    /// Must be called from outside the audio thread.
    pub fn add_track(&mut self) -> (u64, Arc<TrackShared>) {
        let id = self.next_id;
        self.next_id += 1;
        let track = Track::new(id, self.sample_rate, self.max_buffer_size);
        let shared = track.shared.clone();
        self.tracks.push(track);
        (id, shared)
    }

    /// Removes the track with the given id.  Returns `false` when not found.
    ///
    /// Remaining tracks keep their ids, so other handles stay valid.
    /// Must be called from outside the audio thread.
    pub fn remove_track(&mut self, id: u64) -> bool {
        let before = self.tracks.len();
        self.tracks.retain(|t| t.id != id);
        self.tracks.len() != before
    }

    /// Returns a mutable reference to the track with the given id, or `None`.
    pub fn track_mut(&mut self, id: u64) -> Option<&mut Track> {
        self.tracks.iter_mut().find(|t| t.id == id)
    }

    /// Returns a clone of the shared parameter block for the track with `id`.
    pub fn track_shared(&self, id: u64) -> Option<Arc<TrackShared>> {
        self.tracks
            .iter()
            .find(|t| t.id == id)
            .map(|t| t.shared.clone())
    }

    /// Sets (or clears) the audio source for the track with the given id.
    ///
    /// Returns `false` when no track has that id.
    pub fn set_track_source(
        &mut self,
        id: u64,
        source: Option<Box<dyn TrackSource>>,
    ) -> bool {
        match self.track_mut(id) {
            Some(t) => {
                t.set_source(source);
                true
            }
            None => false,
        }
    }

    /// Starts every track at once by setting them all to
    /// [`TrackState::Playing`] in a single control-thread operation.
    ///
    /// Because all the state flips complete before the audio thread reads them
    /// in the next [`MultiTrackMixer::mix`] call, every track begins on the same
    /// render block — a sample-accurate start — without a per-track round-trip.
    /// Must be called from outside the audio thread.
    pub fn play_all(&self) {
        for track in &self.tracks {
            track.shared.set_state(TrackState::Playing);
        }
    }

    /// Returns the number of tracks.
    pub fn track_count(&self) -> usize {
        self.tracks.len()
    }

    /// Returns a reference to the shared clock.
    pub fn clock(&self) -> &SampleClock {
        &self.clock
    }

    /// Mixes all active tracks into `output` and advances the clock.
    ///
    /// `output` is a mutable slice of interleaved f32 samples.  Each active
    /// track is rendered independently and **summed additively** into `output`,
    /// so tracks never feed one another (no serial-chain bleed).  Must be called
    /// from the audio thread only; never allocates in steady state.
    pub fn mix(&mut self, output: &mut [f32]) {
        // Apply any queued structural changes before rendering, so the audio
        // thread remains the sole mutator of its tracks/effects.  Draining is
        // allocation-free: commands carry their payloads by value and removed
        // resources are handed back via the retirement queue.
        if let Some(mut rx) = self.commands.take() {
            while let Ok(cmd) = rx.commands.pop() {
                self.apply_command(cmd, &mut rx.retire);
            }
            self.commands = Some(rx);
        }

        let frames = (output.len() / self.channels.max(1) as usize) as u64;

        for sample in output.iter_mut() {
            *sample = 0.0;
        }

        let any_soloed = self
            .tracks
            .iter()
            .any(|t| t.shared.soloed() && t.shared.state() == TrackState::Playing);

        let channels = self.channels;
        for track in &mut self.tracks {
            if track.is_active(any_soloed) {
                track.process_additive(output, channels);
            }
        }

        self.clock.advance(frames);
    }

    /// Applies one drained [`MixerCommand`] to the mixer's owned data.
    ///
    /// Runs on the audio thread.  Never reallocates: inserts only use the
    /// capacity reserved at construction, and any removed or capacity-refused
    /// resource is pushed onto the retirement queue to be freed by the control
    /// thread instead of being dropped here.
    fn apply_command(&mut self, cmd: MixerCommand, retire: &mut Producer<Retired>) {
        match cmd {
            MixerCommand::AddTrack(track) => {
                if self.tracks.len() < self.tracks.capacity() {
                    self.tracks.push(track);
                } else {
                    // Would reallocate on the audio thread — refuse and retire.
                    let _ = retire.push(Retired::Track(track));
                }
            }
            MixerCommand::RemoveTrack(id) => {
                if let Some(pos) = self.tracks.iter().position(|t| t.id == id) {
                    let removed = self.tracks.remove(pos);
                    let _ = retire.push(Retired::Track(removed));
                }
            }
            MixerCommand::SetTrackSource { track_id, source } => match self.track_mut(track_id) {
                Some(t) => {
                    if let Some(old) = t.replace_source(source) {
                        let _ = retire.push(Retired::Source(old));
                    }
                }
                None => {
                    if let Some(orphan) = source {
                        let _ = retire.push(Retired::Source(orphan));
                    }
                }
            },
            MixerCommand::AddEffect {
                track_id,
                effect_id,
                effect,
            } => {
                let chain = self.track_mut(track_id).map(|t| &mut t.effects);
                match chain {
                    Some(chain) if chain.len() < chain.capacity() => {
                        chain.add_with_id(effect_id, effect);
                    }
                    _ => {
                        // Unknown track or chain at capacity — retire the effect.
                        let _ = retire.push(Retired::Effect(EffectEntry {
                            id: effect_id,
                            effect,
                        }));
                    }
                }
            }
            MixerCommand::RemoveEffect {
                track_id,
                effect_id,
            } => {
                if let Some(t) = self.track_mut(track_id) {
                    if let Some(entry) = t.effects.remove_by_id(effect_id) {
                        let _ = retire.push(Retired::Effect(entry));
                    }
                }
            }
            MixerCommand::SetEffectParam {
                track_id,
                effect_id,
                param_id,
                value,
            } => {
                if let Some(t) = self.track_mut(track_id) {
                    if let Some(effect) = t.effects.effect_mut_by_id(effect_id) {
                        effect.set_param(param_id, value);
                    }
                }
            }
            MixerCommand::SetEffectEnabled {
                track_id,
                effect_id,
                enabled,
            } => {
                if let Some(t) = self.track_mut(track_id) {
                    if let Some(effect) = t.effects.effect_mut_by_id(effect_id) {
                        effect.set_enabled(enabled);
                    }
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::track::TrackSource;
    use super::*;
    use std::sync::atomic::{AtomicU64, Ordering};

    /// In-memory test source that yields a fixed buffer once, then silence.
    struct VecSource {
        data: Vec<f32>,
        pos: usize,
    }

    impl VecSource {
        fn new(data: Vec<f32>) -> Self {
            Self { data, pos: 0 }
        }
    }

    impl TrackSource for VecSource {
        fn read(&mut self, out: &mut [f32]) -> usize {
            let n = out.len().min(self.data.len() - self.pos);
            out[..n].copy_from_slice(&self.data[self.pos..self.pos + n]);
            self.pos += n;
            n
        }
    }

    fn play(mixer: &mut MultiTrackMixer, id: u64) {
        mixer.track_shared(id).unwrap().set_state(TrackState::Playing);
    }

    #[test]
    fn empty_mixer_outputs_silence() {
        let mut mixer = MultiTrackMixer::new(48_000.0, 1);
        let mut out = [9.0f32; 4];
        mixer.mix(&mut out);
        assert_eq!(out, [0.0, 0.0, 0.0, 0.0]);
    }

    #[test]
    fn two_tracks_sum_additively() {
        let mut mixer = MultiTrackMixer::new(48_000.0, 1);
        let (a, _) = mixer.add_track();
        let (b, _) = mixer.add_track();
        mixer.set_track_source(a, Some(Box::new(VecSource::new(vec![1.0, 2.0, 3.0, 4.0]))));
        mixer.set_track_source(b, Some(Box::new(VecSource::new(vec![0.5, 0.5, 0.5, 0.5]))));
        play(&mut mixer, a);
        play(&mut mixer, b);

        let mut out = [0.0f32; 4];
        mixer.mix(&mut out);
        assert_eq!(out, [1.5, 2.5, 3.5, 4.5]);
    }

    #[test]
    fn additive_equals_sum_of_individual_renders() {
        // The N-track mix must equal the sample-wise sum of each track rendered
        // alone — the regression guard against the serial-mixing bug.
        let sources = [
            vec![0.1f32, -0.2, 0.3, -0.4, 0.5, -0.6],
            vec![0.05f32, 0.05, -0.1, 0.2, -0.3, 0.4],
            vec![1.0f32, 1.0, 1.0, 1.0, 1.0, 1.0],
        ];

        let mut reference = vec![0.0f32; 6];
        for src in &sources {
            for (r, &s) in reference.iter_mut().zip(src) {
                *r += s;
            }
        }

        let mut mixer = MultiTrackMixer::new(48_000.0, 2);
        for src in &sources {
            let (id, _) = mixer.add_track();
            mixer.set_track_source(id, Some(Box::new(VecSource::new(src.clone()))));
            play(&mut mixer, id);
        }

        let mut out = vec![0.0f32; 6];
        mixer.mix(&mut out);
        for (o, r) in out.iter().zip(&reference) {
            assert!((o - r).abs() < 1e-6, "out={out:?} reference={reference:?}");
        }
    }

    #[test]
    fn gain_converges_to_target_over_time() {
        // The gain is ramped (anti-zipper), so it approaches the target rather
        // than snapping; over enough samples it must settle at 0.25.
        let mut mixer = MultiTrackMixer::new(48_000.0, 1);
        let (a, shared) = mixer.add_track();
        mixer.set_track_source(a, Some(Box::new(VecSource::new(vec![1.0; 2048]))));
        shared.set_gain(0.25);
        play(&mut mixer, a);

        let mut out = vec![0.0f32; 2048];
        mixer.mix(&mut out);
        // The tail (well past ~8 time constants of the 5 ms ramp) sits at the
        // target gain.
        for &s in &out[1800..] {
            assert!((s - 0.25).abs() < 1e-3, "tail sample {s} not at target 0.25");
        }
    }

    #[test]
    fn gain_change_ramps_rather_than_clicks() {
        // Regression guard for zipper noise: a gain step must not appear as an
        // instantaneous jump on the very first sample of the next block.
        let mut mixer = MultiTrackMixer::new(48_000.0, 1);
        let (a, shared) = mixer.add_track();
        mixer.set_track_source(a, Some(Box::new(VecSource::new(vec![1.0; 4096]))));
        play(&mut mixer, a);

        // First block settles the gain at the default 1.0.
        let mut out = vec![0.0f32; 2048];
        mixer.mix(&mut out);
        assert!((out[out.len() - 1] - 1.0).abs() < 1e-3);

        // Drop the gain and render another block; the first samples must still
        // be near 1.0 and only gradually descend, never jumping straight to 0.0.
        shared.set_gain(0.0);
        let mut out2 = vec![0.0f32; 2048];
        mixer.mix(&mut out2);
        assert!(out2[0] > 0.9, "first sample {} jumped instead of ramping", out2[0]);
        assert!(out2[0] < 1.0, "gain did not begin ramping down");
        // Monotonically non-increasing as it fades toward 0.0.
        for w in out2.windows(2) {
            assert!(w[1] <= w[0] + 1e-6, "ramp not monotonic: {} -> {}", w[0], w[1]);
        }
        assert!(out2[out2.len() - 1] < 1e-3, "did not reach target 0.0");
    }

    #[test]
    fn stopped_and_muted_tracks_are_silent() {
        let mut mixer = MultiTrackMixer::new(48_000.0, 1);
        let (a, sa) = mixer.add_track();
        let (b, sb) = mixer.add_track();
        mixer.set_track_source(a, Some(Box::new(VecSource::new(vec![1.0; 4]))));
        mixer.set_track_source(b, Some(Box::new(VecSource::new(vec![1.0; 4]))));
        // a stays Stopped; b plays but is muted.
        sb.set_state(TrackState::Playing);
        sb.set_muted(true);
        let _ = sa;

        let mut out = [0.0f32; 4];
        mixer.mix(&mut out);
        assert_eq!(out, [0.0, 0.0, 0.0, 0.0]);
    }

    #[test]
    fn solo_mutes_non_soloed_tracks() {
        let mut mixer = MultiTrackMixer::new(48_000.0, 1);
        let (a, sa) = mixer.add_track();
        let (b, sb) = mixer.add_track();
        mixer.set_track_source(a, Some(Box::new(VecSource::new(vec![1.0; 4]))));
        mixer.set_track_source(b, Some(Box::new(VecSource::new(vec![2.0; 4]))));
        sa.set_state(TrackState::Playing);
        sb.set_state(TrackState::Playing);
        sb.set_soloed(true);

        let mut out = [0.0f32; 4];
        mixer.mix(&mut out);
        // Only b (soloed) is audible.
        for &s in &out {
            assert!((s - 2.0).abs() < 1e-6);
        }
    }

    #[test]
    fn underrun_is_silence_padded() {
        let mut mixer = MultiTrackMixer::new(48_000.0, 1);
        let (a, _) = mixer.add_track();
        mixer.set_track_source(a, Some(Box::new(VecSource::new(vec![1.0, 1.0]))));
        play(&mut mixer, a);

        let mut out = [0.0f32; 4];
        mixer.mix(&mut out);
        assert_eq!(out, [1.0, 1.0, 0.0, 0.0]);
    }

    #[test]
    fn remove_keeps_other_track_ids_valid() {
        let mut mixer = MultiTrackMixer::new(48_000.0, 1);
        let (a, _) = mixer.add_track();
        let (b, sb) = mixer.add_track();
        mixer.set_track_source(b, Some(Box::new(VecSource::new(vec![3.0; 4]))));
        play(&mut mixer, b);

        assert!(mixer.remove_track(a));
        assert!(!mixer.remove_track(a));
        // b's id and shared handle still resolve after a was removed.
        assert!(mixer.track_shared(b).is_some());
        sb.set_gain(1.0);

        let mut out = [0.0f32; 4];
        mixer.mix(&mut out);
        for &s in &out {
            assert!((s - 3.0).abs() < 1e-6);
        }
    }

    #[test]
    fn play_all_starts_every_track_in_one_call() {
        // A single play_all() must flip all tracks to Playing, so the very next
        // mix renders every track together — sample-accurate start.
        let mut mixer = MultiTrackMixer::new(48_000.0, 1);
        let (a, _) = mixer.add_track();
        let (b, _) = mixer.add_track();
        mixer.set_track_source(a, Some(Box::new(VecSource::new(vec![1.0; 4]))));
        mixer.set_track_source(b, Some(Box::new(VecSource::new(vec![2.0; 4]))));

        // Nothing is audible before play_all.
        let mut pre = [0.0f32; 4];
        mixer.mix(&mut pre);
        assert_eq!(pre, [0.0, 0.0, 0.0, 0.0]);

        mixer.play_all();
        assert_eq!(mixer.track_shared(a).unwrap().state(), TrackState::Playing);
        assert_eq!(mixer.track_shared(b).unwrap().state(), TrackState::Playing);

        let mut out = [0.0f32; 4];
        mixer.mix(&mut out);
        // Both tracks contribute on the same block: 1.0 + 2.0.
        for &s in &out {
            assert!((s - 3.0).abs() < 1e-6, "out={out:?}");
        }
    }

    #[test]
    fn clock_advances_by_frame_count() {
        let mut mixer = MultiTrackMixer::new(48_000.0, 2);
        let mut out = [0.0f32; 8]; // 4 stereo frames
        mixer.mix(&mut out);
        assert_eq!(mixer.clock().position(), 4);
    }

    #[test]
    fn track_vector_is_preallocated_and_never_reallocates_on_mix() {
        // The track vector is reserved to MAX_TRACKS up front; draining queued
        // AddTrack commands on the audio thread must not grow (reallocate) it.
        let mut mixer = MultiTrackMixer::new(48_000.0, 1);
        assert_eq!(mixer.tracks.capacity(), MAX_TRACKS);

        let (mut ctl, rx) = command_channel(64, mixer.sample_rate(), mixer.max_buffer_size());
        mixer.attach_command_receiver(rx);
        for _ in 0..8 {
            ctl.add_track().unwrap();
        }
        let mut out = [0.0f32; 16];
        mixer.mix(&mut out);

        assert_eq!(mixer.track_count(), 8);
        // Capacity unchanged → no reallocation occurred on the audio thread.
        assert_eq!(mixer.tracks.capacity(), MAX_TRACKS);
    }

    /// A [`TrackSource`] that emits a monotonically increasing sample ramp (each
    /// sample equals its global index) and records how many samples it has been
    /// asked for.  The ramp lets the mix output be checked for gap-free continuity
    /// across blocks, while the counter lets each track's consumption be compared
    /// to the transport clock — together detecting any inter-track or clock drift.
    struct RampSource {
        next: u64,
        read_total: Arc<AtomicU64>,
    }

    impl RampSource {
        fn new(read_total: Arc<AtomicU64>) -> Self {
            Self { next: 0, read_total }
        }
    }

    impl TrackSource for RampSource {
        fn read(&mut self, out: &mut [f32]) -> usize {
            for s in out.iter_mut() {
                *s = self.next as f32;
                self.next += 1;
            }
            self.read_total.fetch_add(out.len() as u64, Ordering::Relaxed);
            out.len()
        }
    }

    #[test]
    fn tracks_stay_sample_locked_over_many_blocks_no_drift() {
        // 2.4 sync-drift regression: many tracks driven by the shared clock must
        // advance in exact lockstep over thousands of blocks.  The mixed output
        // stays a gap-free sum of ramps (any skipped or repeated sample would break
        // the continuity), the clock advances by exactly one frame per output frame,
        // and every track consumes exactly the clock's worth of samples — zero
        // accumulated drift.  The default track gain is unity and its smoother rests
        // there, so the ramp arithmetic stays exact.
        const CHANNELS: u16 = 1;
        const BLOCK: usize = 512;
        const BLOCKS: usize = 4000;
        const TRACKS: usize = 3;

        let mut mixer = MultiTrackMixer::new(48_000.0, CHANNELS);
        let counters: Vec<Arc<AtomicU64>> =
            (0..TRACKS).map(|_| Arc::new(AtomicU64::new(0))).collect();
        for c in &counters {
            let (id, _) = mixer.add_track();
            mixer.set_track_source(id, Some(Box::new(RampSource::new(c.clone()))));
            play(&mut mixer, id);
        }

        let mut out = [0.0f32; BLOCK];
        for block in 0..BLOCKS {
            mixer.mix(&mut out);
            for (i, &sample) in out.iter().enumerate() {
                let global = block as u64 * BLOCK as u64 + i as u64;
                let expected = (TRACKS as u64 * global) as f32;
                assert_eq!(
                    sample, expected,
                    "sync drift at block {block}, offset {i}: got {sample}, expected {expected}"
                );
            }
        }

        let total_frames = (BLOCKS * BLOCK) as u64;
        assert_eq!(
            mixer.clock().position(),
            total_frames,
            "clock must advance by exactly one frame per output frame"
        );
        for (t, c) in counters.iter().enumerate() {
            assert_eq!(
                c.load(Ordering::Relaxed),
                total_frames * CHANNELS as u64,
                "track {t} consumed a different sample count than the clock → drift"
            );
        }
    }
}
