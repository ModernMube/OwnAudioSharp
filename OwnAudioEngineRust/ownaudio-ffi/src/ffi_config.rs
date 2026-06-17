use ownaudio_core::{SampleFormat, StreamConfig};

/// Sample data format used in C-facing stream configuration.
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum OwnAudioSampleFormat {
    /// 32-bit IEEE float — recommended for DSP work.
    F32 = 0,
    /// Signed 16-bit integer.
    I16 = 1,
    /// Unsigned 16-bit integer.
    U16 = 2,
}

impl From<OwnAudioSampleFormat> for SampleFormat {
    fn from(fmt: OwnAudioSampleFormat) -> Self {
        match fmt {
            OwnAudioSampleFormat::F32 => SampleFormat::F32,
            OwnAudioSampleFormat::I16 => SampleFormat::I16,
            OwnAudioSampleFormat::U16 => SampleFormat::U16,
        }
    }
}

/// Parameters for opening an audio stream, passed across the FFI boundary.
///
/// `buffer_size_frames = 0` means "let the engine choose the platform default".
/// Any non-zero value requests a specific buffer size.
#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct OwnAudioStreamConfig {
    /// Target sample rate in Hz (e.g. 44100, 48000).
    pub sample_rate: u32,
    /// Number of channels (1 = mono, 2 = stereo).
    pub channels: u16,
    /// Sample data format.
    pub sample_format: OwnAudioSampleFormat,
    /// Requested buffer size in audio frames; 0 uses the platform default.
    pub buffer_size_frames: u32,
}

impl From<OwnAudioStreamConfig> for StreamConfig {
    fn from(c: OwnAudioStreamConfig) -> Self {
        StreamConfig {
            sample_rate: c.sample_rate,
            channels: c.channels,
            sample_format: c.sample_format.into(),
            buffer_size_frames: if c.buffer_size_frames == 0 {
                None
            } else {
                Some(c.buffer_size_frames)
            },
        }
    }
}
