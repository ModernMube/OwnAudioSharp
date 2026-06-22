use thiserror::Error;

/// Unified error type for the audio core.
///
/// Every Cpal-specific error is mapped to one of these variants so that
/// callers (and the future C# FFI layer) never need to depend on Cpal types.
#[derive(Debug, Error)]
pub enum AudioError {
    /// No audio device matched the requested criteria.
    #[error("No audio device found matching the request")]
    DeviceNotFound,

    /// The OS audio subsystem failed to enumerate or describe devices.
    #[error("Failed to enumerate audio devices: {0}")]
    DeviceEnumeration(String),

    /// The requested stream parameters are not supported by the device.
    #[error("Unsupported stream configuration: {0}")]
    UnsupportedConfig(String),

    /// The audio stream could not be built.
    #[error("Failed to build audio stream: {0}")]
    StreamBuild(String),

    /// Starting or stopping the audio stream failed.
    #[error("Failed to control audio stream: {0}")]
    StreamControl(String),

    /// The ring buffer received more data than it could hold; `dropped` samples were lost.
    #[error("Ring buffer overflow: {dropped} samples dropped")]
    RingBufferOverflow { dropped: usize },

    /// A read requested more samples than were available in the ring buffer.
    #[error("Ring buffer underrun: requested {requested}, available {available}")]
    RingBufferUnderrun { requested: usize, available: usize },

    /// Resampler initialisation failed (e.g. unsupported ratio or channel count).
    #[error("Resampler initialization failed: {0}")]
    ResamplerInit(String),

    /// Resampler encountered an error during processing.
    #[error("Resampler processing failed: {0}")]
    ResamplerProcess(String),

    /// The audio file could not be opened or its format could not be probed.
    #[error("Failed to open audio file: {0}")]
    DecoderOpen(String),

    /// No decoder backend could handle the file's container or codec.
    #[error("Unsupported audio format: {0}")]
    DecoderUnsupported(String),

    /// An error occurred while decoding audio data from the stream.
    #[error("Audio decode error: {0}")]
    DecoderRead(String),

    /// Seeking within the audio stream failed.
    #[error("Audio seek failed: {0}")]
    DecoderSeek(String),
}

/// Convenience alias — all public functions in this crate return this.
pub type Result<T> = std::result::Result<T, AudioError>;

// ---------------------------------------------------------------------------
// Cpal 0.18 unified error conversion
// ---------------------------------------------------------------------------

impl From<cpal::Error> for AudioError {
    fn from(e: cpal::Error) -> Self {
        use cpal::ErrorKind;
        match e.kind() {
            ErrorKind::DeviceNotAvailable | ErrorKind::HostUnavailable => {
                AudioError::DeviceNotFound
            }
            ErrorKind::UnsupportedConfig | ErrorKind::UnsupportedOperation => {
                AudioError::UnsupportedConfig(e.to_string())
            }
            _ => AudioError::DeviceEnumeration(e.to_string()),
        }
    }
}
