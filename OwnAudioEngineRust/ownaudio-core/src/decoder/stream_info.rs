/// Metadata describing the audio stream produced by a decoder backend.
///
/// All fields describe the **output** of the decoder after any requested
/// channel and sample-rate conversion, not necessarily the raw source file.
/// The layout is `#[repr(C)]` so the same struct can be returned by value
/// across the FFI boundary.
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct AudioStreamInfo {
    /// Number of interleaved channels in the decoded output (1 = mono, 2 = stereo).
    pub channels: u32,

    /// Output sample rate in Hz.
    pub sample_rate: u32,

    /// Total stream duration in milliseconds, or `u64::MAX` when the duration
    /// cannot be determined from the container metadata.
    pub duration_ms: u64,

    /// Source bit depth in bits (e.g. 16, 24), or `0` for float / compressed
    /// formats where a meaningful integer bit depth is not available.
    pub bit_depth: u32,
}

impl AudioStreamInfo {
    /// Sentinel duration value used when the stream length is unknown.
    pub const UNKNOWN_DURATION: u64 = u64::MAX;

    /// Returns `true` when the total duration could not be determined.
    pub fn has_unknown_duration(&self) -> bool {
        self.duration_ms == Self::UNKNOWN_DURATION
    }

    /// Total number of sample frames, derived from duration and sample rate.
    ///
    /// Returns `None` when the duration is unknown.
    pub fn total_frames(&self) -> Option<u64> {
        if self.has_unknown_duration() {
            None
        } else {
            Some(self.duration_ms.saturating_mul(self.sample_rate as u64) / 1000)
        }
    }
}

/// Result of a single [`AudioDecoderBackend::read_frames`](crate::decoder::AudioDecoderBackend::read_frames) call.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct DecoderReadResult {
    /// Number of interleaved `f32` samples written to the caller's buffer.
    ///
    /// This is `frames * channels`, not the frame count.
    pub samples_written: usize,

    /// Presentation timestamp of the first written sample, in milliseconds.
    pub pts_ms: f64,

    /// `true` once the end of the stream has been reached and no further
    /// samples will be produced (until a seek occurs).
    pub is_eof: bool,
}
