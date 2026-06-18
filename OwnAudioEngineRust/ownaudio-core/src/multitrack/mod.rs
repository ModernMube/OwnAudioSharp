//! Multi-track mixer with a shared sample-accurate transport clock.

pub mod clock;
pub mod track;

pub use clock::SampleClock;
pub use track::{Track, TrackState};

/// Central multi-track mixer.
///
/// Holds a collection of [`Track`]s and a shared [`SampleClock`].
/// On every audio callback the mixer iterates active tracks, applies their
/// effect chains, sums the results into the output buffer, and advances the
/// clock.
///
/// All structural mutations (add/remove track, set mute/solo) happen through
/// command objects posted via an `rtrb` producer; the audio thread drains the
/// queue at the start of each callback to stay allocation-free on the hot path.
pub struct MultiTrackMixer {
    /// Ordered list of tracks.
    tracks: Vec<Track>,
    /// Shared sample-accurate clock.
    clock: SampleClock,
    /// Number of audio channels (1 = mono, 2 = stereo, …).
    channels: u16,
}

impl MultiTrackMixer {
    /// Creates an empty mixer.
    pub fn new(sample_rate: f32, channels: u16) -> Self {
        Self {
            tracks: Vec::new(),
            clock: SampleClock::new(sample_rate),
            channels,
        }
    }

    /// Appends a new track and returns its index.
    ///
    /// Must be called from outside the audio thread.
    pub fn add_track(&mut self) -> usize {
        let idx = self.tracks.len();
        self.tracks.push(Track::new());
        idx
    }

    /// Removes the track at `index`.  Returns `false` when out of range.
    ///
    /// Must be called from outside the audio thread.
    pub fn remove_track(&mut self, index: usize) -> bool {
        if index < self.tracks.len() {
            self.tracks.remove(index);
            true
        } else {
            false
        }
    }

    /// Returns a mutable reference to the track at `index`, or `None`.
    pub fn track_mut(&mut self, index: usize) -> Option<&mut Track> {
        self.tracks.get_mut(index)
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
    /// `output` is a mutable slice of interleaved f32 samples.
    /// Must be called from the audio thread only; never allocates.
    pub fn mix(&mut self, output: &mut [f32]) {
        let frames = (output.len() / self.channels as usize) as u64;

        for sample in output.iter_mut() {
            *sample = 0.0;
        }

        let channels = self.channels;
        for track in &mut self.tracks {
            if track.is_active() {
                track.effects.process_all(output, channels);
                let gain = track.gain;
                for s in output.iter_mut() {
                    *s *= gain;
                }
            }
        }

        self.clock.advance(frames);
    }
}
