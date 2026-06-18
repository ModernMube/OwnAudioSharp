//! Sample-accurate central transport clock.

use std::sync::atomic::{AtomicU64, Ordering};

/// Sample-accurate transport clock shared between the audio thread and control threads.
///
/// The audio thread increments the position after every processed buffer;
/// control threads read position for display and seek operations.
pub struct SampleClock {
    position: AtomicU64,
    sample_rate: f32,
}

impl SampleClock {
    /// Creates a new clock stopped at position zero.
    pub fn new(sample_rate: f32) -> Self {
        Self {
            position: AtomicU64::new(0),
            sample_rate,
        }
    }

    /// Returns the current sample position.
    #[inline]
    pub fn position(&self) -> u64 {
        self.position.load(Ordering::Relaxed)
    }

    /// Advances the clock by `frames` samples.  Called by the audio thread.
    #[inline]
    pub fn advance(&self, frames: u64) {
        self.position.fetch_add(frames, Ordering::Relaxed);
    }

    /// Seeks the clock to an absolute sample position.
    pub fn seek(&self, sample_position: u64) {
        self.position.store(sample_position, Ordering::Release);
    }

    /// Resets the clock to zero.
    pub fn reset(&self) {
        self.position.store(0, Ordering::Release);
    }

    /// Converts the current position to seconds.
    pub fn position_seconds(&self) -> f64 {
        self.position() as f64 / self.sample_rate as f64
    }

    /// Converts a duration in seconds to a sample count.
    pub fn seconds_to_samples(&self, seconds: f64) -> u64 {
        (seconds * self.sample_rate as f64) as u64
    }
}
