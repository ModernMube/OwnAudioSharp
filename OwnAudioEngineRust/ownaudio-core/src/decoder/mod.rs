//! Streaming audio file decoder.
//!
//! This module replaces the original "load the whole file into memory" model
//! with a prefetch/streaming architecture: a background thread decodes the
//! file incrementally into a small lock-free ring buffer, so memory usage is
//! bounded by the prefetch size rather than the file size.  This keeps 20+
//! track multitrack playback affordable even with large uncompressed WAV files.
//!
//! ## Backend selection
//!
//! Decoding is performed by the pure-Rust **Symphonia** backend
//! ([`backend::symphonia_backend`]), which is always available and supports
//! WAV, MP3, FLAC, OGG/Vorbis, AAC/M4A and AIFF without any external runtime
//! dependency.
//!
//! FFmpeg-backed decoding is **not** part of the native library.  As in the
//! original design, FFmpeg is used only when it is installed on the system (or
//! found at a user-configured path); that runtime detection and decoding lives
//! in the managed C# layer, so the native binary never depends on FFmpeg at
//! build or load time.
//!
//! ## Usage
//!
//! ```no_run
//! use ownaudio_core::decoder::open_streaming;
//!
//! // Keep source format, 2 seconds of prefetch at 44.1 kHz.
//! let mut track = open_streaming("music.flac", 0, 0, 88_200).unwrap();
//! let info = track.stream_info();
//!
//! let mut buffer = vec![0.0f32; 4096];
//! loop {
//!     let n = track.read(&mut buffer);
//!     if n == 0 && track.is_eof() { break; }
//!     // ... consume `buffer[..n]` ...
//! }
//! ```

pub mod backend;
pub mod stream_info;
pub mod streaming_track;

#[cfg(test)]
pub(crate) mod test_support;
#[cfg(test)]
mod tests;

pub use stream_info::{AudioStreamInfo, DecoderReadResult};
pub use streaming_track::StreamingTrack;

use crate::error::Result;

/// Decoder backend abstraction implemented by each concrete decoder.
///
/// All methods are invoked from the prefetch thread only, never from the
/// real-time audio callback, so blocking I/O and heap allocation during
/// `open`/`seek` are acceptable.  `read_frames` should avoid steady-state
/// allocation where practical.
pub(crate) trait AudioDecoderBackend: Send {
    /// Returns metadata describing the decoded output format.
    fn stream_info(&self) -> AudioStreamInfo;

    /// Decodes audio into `buffer` (interleaved `f32`, output format).
    ///
    /// Fills as much of `buffer` as possible, decoding additional packets as
    /// needed, and returns how many samples were written together with the EOF
    /// flag.  A return of zero samples with `is_eof == true` means the stream
    /// is exhausted.
    fn read_frames(&mut self, buffer: &mut [f32]) -> Result<DecoderReadResult>;

    /// Seeks to the given output sample-frame position.
    ///
    /// `frame_position` is expressed in **output** frames (after resampling).
    fn seek(&mut self, frame_position: u64) -> Result<()>;
}

/// Opens an audio file, selects a backend and wraps it in a [`StreamingTrack`]
/// with a background prefetch thread.
///
/// - `path` — filesystem path to the audio file.
/// - `target_sample_rate` — desired output sample rate in Hz; `0` keeps the
///   source rate.
/// - `target_channels` — desired output channel count; `0` keeps the source
///   channel count.
/// - `prefetch_frames` — ring-buffer capacity in sample frames (e.g. `88_200`
///   for 2 seconds at 44.1 kHz).  Clamped to a sane minimum internally.
///
/// # Errors
/// Returns an [`AudioError`](crate::error::AudioError) when no backend can open
/// the file.
pub fn open_streaming(
    path: &str,
    target_sample_rate: u32,
    target_channels: u32,
    prefetch_frames: usize,
) -> Result<StreamingTrack> {
    StreamingTrack::open(path, target_sample_rate, target_channels, prefetch_frames)
}
