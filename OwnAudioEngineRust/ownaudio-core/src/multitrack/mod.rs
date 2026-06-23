//! Multi-track mixer with a shared sample-accurate transport clock.

pub mod clock;
pub mod track;

pub use clock::SampleClock;
pub use track::{Track, TrackShared, TrackSource, TrackState};

use std::sync::Arc;

/// Default per-track scratch size in samples, used when the mixer is created
/// without an explicit block size.  Covers a 4096-frame stereo callback; larger
/// blocks grow the scratch once (amortised, never in steady state).
const DEFAULT_MAX_BUFFER: usize = 4096 * 2;

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
    /// Tracks in insertion order, each with a stable id.
    tracks: Vec<Track>,
    /// Shared sample-accurate clock.
    clock: SampleClock,
    /// Number of audio channels (1 = mono, 2 = stereo, …).
    channels: u16,
    /// Sample rate in Hz, handed to new tracks for gain-smoothing.
    sample_rate: f32,
    /// Monotonic id generator for new tracks.
    next_id: u64,
    /// Scratch size handed to new tracks.
    max_buffer_size: usize,
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
            tracks: Vec::new(),
            clock: SampleClock::new(sample_rate),
            channels,
            sample_rate,
            next_id: 0,
            max_buffer_size: max_buffer_size.max(1),
        }
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
}

#[cfg(test)]
mod tests {
    use super::track::TrackSource;
    use super::*;

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
    fn clock_advances_by_frame_count() {
        let mut mixer = MultiTrackMixer::new(48_000.0, 2);
        let mut out = [0.0f32; 8]; // 4 stereo frames
        mixer.mix(&mut out);
        assert_eq!(mixer.clock().position(), 4);
    }
}
