//! In-memory track source: owns an interleaved sample buffer and serves it
//! straight to a mixer track on the audio thread — with no managed (C#) pump in
//! the loop.
//!
//! A [`MemoryTrackSource`] holds a fully-decoded interleaved `f32` buffer (at the
//! session's sample rate and channel count) and implements [`TrackSource`], so it
//! can be installed as a track's source through the mixer command queue. Loop and
//! end-of-stream handling live here on the audio thread; the control side observes
//! and steers them through a shared [`MemorySourceControl`].
//!
//! This is the memory-backed counterpart of [`FileTrackSource`](super::file_source::FileTrackSource):
//! a `SampleSource` in the managed layer hands its buffer across the FFI boundary
//! exactly once (a control-thread copy, never on the audio path), after which the
//! audio thread owns the data and the managed side is only a controller.

use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::Arc;

use crate::multitrack::track::TrackSource;

/// Control-side state shared between the control thread (FFI / C#) and a
/// [`MemoryTrackSource`] running on the audio thread.
///
/// Once the source is installed on a track it is owned by the audio thread, so
/// every parameter the control side still needs to touch — loop toggle, the
/// finished latch, and seeking — lives here behind atomics.
pub struct MemorySourceControl {
    /// When `true`, reaching the end of the buffer rewinds to the start and keeps
    /// playing instead of finishing.
    loop_enabled: AtomicBool,
    /// Latched `true` once the source reaches the end of the buffer without
    /// looping; polled by the control side to raise its completion event. Cleared
    /// as soon as audio flows again (after a seek).
    finished: AtomicBool,
    /// Target sample-frame position requested by the control thread; consumed once
    /// by the audio thread via the [`MemorySourceControl::seek_pending`] latch.
    seek_frame: AtomicU64,
    /// Latch: `true` when the control thread has written a new
    /// [`MemorySourceControl::seek_frame`] the audio thread has not yet taken.
    seek_pending: AtomicBool,
}

impl MemorySourceControl {
    /// Enables or disables seamless looping.
    #[inline]
    pub fn set_loop(&self, enabled: bool) {
        self.loop_enabled.store(enabled, Ordering::Relaxed);
    }

    /// Returns whether looping is currently enabled.
    #[inline]
    pub fn loop_enabled(&self) -> bool {
        self.loop_enabled.load(Ordering::Relaxed)
    }

    /// Returns whether the source has reached the end of the buffer without looping.
    #[inline]
    pub fn is_finished(&self) -> bool {
        self.finished.load(Ordering::Relaxed)
    }

    /// Requests a seek to `frame_position` (output frames). Non-blocking — the
    /// audio thread applies the reposition on its next read and the finished latch
    /// clears once audio flows from the new position.
    #[inline]
    pub fn seek_frames(&self, frame_position: u64) {
        self.seek_frame.store(frame_position, Ordering::Relaxed);
        self.seek_pending.store(true, Ordering::Release);
    }
}

/// A [`TrackSource`] that serves a fully-decoded in-memory interleaved buffer.
///
/// The buffer is owned by the audio thread once the source is installed; the
/// control side steers loop/seek and observes end-of-stream through the shared
/// [`MemorySourceControl`], never touching the audio data.
pub struct MemoryTrackSource {
    /// Interleaved `f32` samples at the session sample rate / channel count.
    samples: Vec<f32>,
    /// Interleaved channel count (used to convert a frame seek to a sample index).
    channels: usize,
    /// Current read position, in interleaved samples.
    pos: usize,
    /// Shared control block.
    control: Arc<MemorySourceControl>,
}

impl MemoryTrackSource {
    /// Creates a memory source over `samples` (interleaved, `channels`-wide) and
    /// returns it together with the shared control handle the caller retains after
    /// installing the source on a track.
    pub fn new(samples: Vec<f32>, channels: u32) -> (Self, Arc<MemorySourceControl>) {
        let control = Arc::new(MemorySourceControl {
            loop_enabled: AtomicBool::new(false),
            finished: AtomicBool::new(false),
            seek_frame: AtomicU64::new(0),
            seek_pending: AtomicBool::new(false),
        });

        let source = Self {
            samples,
            channels: channels.max(1) as usize,
            pos: 0,
            control: Arc::clone(&control),
        };

        (source, control)
    }

    /// Applies a pending control-side seek request (if any) to the read position.
    #[inline]
    fn apply_pending_seek(&mut self) {
        if self.control.seek_pending.swap(false, Ordering::Acquire) {
            let frame = self.control.seek_frame.load(Ordering::Relaxed);
            let sample = frame.saturating_mul(self.channels as u64);
            self.pos = (sample as usize).min(self.samples.len());
            self.control.finished.store(false, Ordering::Relaxed);
        }
    }
}

impl TrackSource for MemoryTrackSource {
    #[inline]
    fn read(&mut self, out: &mut [f32]) -> usize {
        self.apply_pending_seek();

        if self.samples.is_empty() {
            return 0;
        }

        let mut written = 0;
        while written < out.len() {
            if self.pos >= self.samples.len() {
                if self.control.loop_enabled.load(Ordering::Relaxed) {
                    // Seamless loop: rewind and keep filling this same block.
                    self.pos = 0;
                } else {
                    self.control.finished.store(true, Ordering::Relaxed);
                    break;
                }
            }

            let n = (out.len() - written).min(self.samples.len() - self.pos);
            out[written..written + n].copy_from_slice(&self.samples[self.pos..self.pos + n]);
            self.pos += n;
            written += n;
        }

        written
    }

    #[inline]
    fn is_eof(&self) -> bool {
        // Real end-of-stream only: fully played and not looping. False while
        // looping, so the stretch stage never flushes its FIFO tail mid-loop.
        self.control.finished.load(Ordering::Relaxed)
            && !self.control.loop_enabled.load(Ordering::Relaxed)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A ramp buffer so read output is positionally identifiable.
    fn ramp(frames: usize, channels: u32) -> Vec<f32> {
        (0..frames * channels as usize).map(|i| i as f32).collect()
    }

    #[test]
    fn reads_buffer_then_finishes_without_loop() {
        let (mut src, control) = MemoryTrackSource::new(ramp(4, 2), 2);
        let mut out = [0.0f32; 16];

        let n = src.read(&mut out);
        assert_eq!(n, 8, "reads all 4 stereo frames = 8 samples");
        assert_eq!(&out[..8], &[0.0, 1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0]);
        assert!(
            control.is_finished(),
            "latches finished at end without loop"
        );

        let n2 = src.read(&mut out);
        assert_eq!(n2, 0, "no more samples once finished");
    }

    #[test]
    fn loop_wraps_and_never_finishes() {
        let (mut src, control) = MemoryTrackSource::new(ramp(2, 2), 2);
        control.set_loop(true);
        let mut out = [0.0f32; 12];

        let n = src.read(&mut out);
        assert_eq!(n, 12, "fills the whole block by wrapping");
        assert_eq!(
            &out[..],
            &[0.0, 1.0, 2.0, 3.0, 0.0, 1.0, 2.0, 3.0, 0.0, 1.0, 2.0, 3.0]
        );
        assert!(!control.is_finished(), "looping never finishes");
        assert!(!src.is_eof(), "looping is never eof");
    }

    #[test]
    fn seek_repositions_and_clears_finished() {
        let (mut src, control) = MemoryTrackSource::new(ramp(4, 2), 2);
        let mut out = [0.0f32; 16];
        src.read(&mut out);
        assert!(control.is_finished());

        control.seek_frames(2); // frame 2 => sample index 4
        let mut small = [0.0f32; 4];
        let n = src.read(&mut small);
        assert_eq!(n, 4, "reads frames 2..4 = 4 samples");
        assert_eq!(&small[..4], &[4.0, 5.0, 6.0, 7.0]);
        assert!(!control.is_finished(), "seek clears the finished latch");
    }

    #[test]
    fn empty_buffer_reads_nothing() {
        let (mut src, _control) = MemoryTrackSource::new(Vec::new(), 2);
        let mut out = [1.0f32; 4];
        assert_eq!(src.read(&mut out), 0);
    }
}
