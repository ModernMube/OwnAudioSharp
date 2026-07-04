//! File-backed track source: owns a streaming decoder and its prefetch thread,
//! and feeds the decoded samples straight into a mixer track on the audio
//! thread — with no managed (C#) pump in the loop.
//!
//! A [`FileTrackSource`] wraps a [`StreamingTrack`] and implements
//! [`TrackSource`], so it can be installed as a track's source through the mixer
//! command queue. Loop and end-of-stream handling live here on the audio thread;
//! the control side observes them through a shared [`FileSourceControl`].

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;

use crate::decoder::streaming_track::SeekState;
use crate::decoder::{AudioStreamInfo, StreamingTrack};
use crate::error::Result;
use crate::multitrack::track::TrackSource;

/// Control-side state shared between the control thread (FFI / C#) and a
/// [`FileTrackSource`] running on the audio thread.
///
/// Once the source is installed on a track it is owned by the audio thread, so
/// every parameter the control side still needs to touch — loop toggle, the
/// finished latch, and seeking — lives here behind atomics.
pub struct FileSourceControl {
    /// When `true`, reaching end-of-stream rewinds to the start and keeps
    /// playing instead of finishing.
    loop_enabled: AtomicBool,
    /// Latched `true` once the source reaches end-of-stream without looping;
    /// polled by the control side to raise its completion event. Cleared as soon
    /// as audio flows again (after a seek).
    finished: AtomicBool,
    /// Shared seek-request channel into the streaming decoder's prefetch thread.
    seek: Arc<SeekState>,
}

impl FileSourceControl {
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

    /// Returns whether the source has reached end-of-stream without looping.
    #[inline]
    pub fn is_finished(&self) -> bool {
        self.finished.load(Ordering::Relaxed)
    }

    /// Requests a seek to `frame_position` (output frames). Non-blocking — the
    /// decoder's prefetch thread performs the reposition on its next iteration,
    /// and the finished latch clears once audio flows from the new position.
    #[inline]
    pub fn seek_frames(&self, frame_position: u64) {
        self.seek.request(frame_position);
    }
}

/// A [`TrackSource`] that decodes an audio file on its own prefetch thread.
///
/// The heavy lifting (decoding, resampling, prefetching ahead into a lock-free
/// ring) is done by the wrapped [`StreamingTrack`]; this type only adds the
/// audio-thread loop/EOF policy and routes it through the shared
/// [`FileSourceControl`].
pub struct FileTrackSource {
    streaming: StreamingTrack,
    control: Arc<FileSourceControl>,
}

impl FileTrackSource {
    /// Opens `path`, spawns its prefetch thread, and returns the source together
    /// with the shared control handle the caller retains after installing the
    /// source on a track.
    ///
    /// See [`open_streaming`](crate::decoder::open_streaming) for the parameter
    /// semantics.
    pub fn open(
        path: &str,
        target_sample_rate: u32,
        target_channels: u32,
        prefetch_frames: usize,
    ) -> Result<(Self, Arc<FileSourceControl>)> {
        let streaming =
            StreamingTrack::open(path, target_sample_rate, target_channels, prefetch_frames)?;

        let control = Arc::new(FileSourceControl {
            loop_enabled: AtomicBool::new(false),
            finished: AtomicBool::new(false),
            seek: streaming.seek_state(),
        });

        let source = Self {
            streaming,
            control: Arc::clone(&control),
        };

        Ok((source, control))
    }

    /// Decoded output format metadata (channels, sample rate, duration).
    pub fn stream_info(&self) -> AudioStreamInfo {
        self.streaming.stream_info()
    }
}

impl TrackSource for FileTrackSource {
    #[inline]
    fn read(&mut self, out: &mut [f32]) -> usize {
        let read = self.streaming.read(out);
        if read > 0 {
            // Audio is flowing (start, mid-stream, or resumed after a seek), so
            // the source is not finished.
            self.control.finished.store(false, Ordering::Relaxed);
            return read;
        }

        // No samples: either a transient prefetch underrun or true end-of-stream.
        if self.streaming.is_eof() {
            if self.control.loop_enabled.load(Ordering::Relaxed) {
                // Seamless loop: rewind and keep feeding. The reposition is
                // applied by the prefetch thread; reads return silence for the
                // brief refill window.
                self.streaming.seek(0);
            } else {
                self.control.finished.store(true, Ordering::Relaxed);
            }
        }

        read
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::decoder::test_support::TempWav;

    /// Builds a short stereo WAV (constant non-silent samples) for feed tests.
    fn stereo_wav(frames: usize) -> TempWav {
        let interleaved = vec![1_000i16; frames * 2];
        TempWav::write(2, 44_100, &interleaved)
    }

    /// Reads the whole source to end-of-stream, returning the total samples read.
    fn drain_to_eof(source: &mut FileTrackSource, control: &FileSourceControl) -> usize {
        let mut buf = vec![0.0f32; 4096];
        let mut total = 0usize;
        for _ in 0..10_000 {
            let n = source.read(&mut buf);
            total += n;
            if control.is_finished() {
                break;
            }
            if n == 0 {
                std::thread::sleep(std::time::Duration::from_millis(1));
            }
        }
        total
    }

    #[test]
    fn finishes_at_end_of_stream() {
        let wav = stereo_wav(4_000);
        let (mut source, control) = FileTrackSource::open(wav.path_str(), 44_100, 2, 8_192).unwrap();

        assert!(!control.is_finished());
        let total = drain_to_eof(&mut source, &control);
        assert!(control.is_finished(), "source should latch finished at EOF");
        assert!(total > 0, "expected to read some audio, got {total}");
    }

    #[test]
    fn loop_never_finishes() {
        let wav = stereo_wav(4_000);
        let (mut source, control) = FileTrackSource::open(wav.path_str(), 44_100, 2, 8_192).unwrap();
        control.set_loop(true);

        let mut buf = vec![0.0f32; 4096];
        // Read well past the file length; looping must keep the finished latch clear.
        for _ in 0..2_000 {
            let n = source.read(&mut buf);
            assert!(!control.is_finished(), "looping source must never finish");
            if n == 0 {
                std::thread::sleep(std::time::Duration::from_millis(1));
            }
        }
    }

    #[test]
    fn seek_clears_finished_latch() {
        let wav = stereo_wav(4_000);
        let (mut source, control) = FileTrackSource::open(wav.path_str(), 44_100, 2, 8_192).unwrap();

        drain_to_eof(&mut source, &control);
        assert!(control.is_finished());

        // Seek back to the start; once audio flows again the latch must clear.
        control.seek_frames(0);
        let mut buf = vec![0.0f32; 4096];
        let mut cleared = false;
        for _ in 0..2_000 {
            let n = source.read(&mut buf);
            if n > 0 {
                cleared = !control.is_finished();
                break;
            }
            std::thread::sleep(std::time::Duration::from_millis(1));
        }
        assert!(cleared, "seek should let the source play again and clear finished");
    }
}
