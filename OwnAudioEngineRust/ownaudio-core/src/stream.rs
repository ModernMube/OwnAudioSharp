use cpal::traits::StreamTrait;

use crate::error::Result;

/// A running or paused audio output stream.
///
/// Dropping this value stops and destroys the underlying Cpal stream.
/// The stream is initially paused after construction; call [`OutputStream::play`]
/// to start audio output.
pub struct OutputStream {
    // cpal::Stream is not Send on all platforms, so we keep it behind the
    // module boundary and do not expose it.
    pub(crate) stream: cpal::Stream,
}

impl OutputStream {
    /// Starts (or resumes) audio output.
    pub fn play(&self) -> Result<()> {
        self.stream.play().map_err(Into::into)
    }

    /// Pauses audio output without destroying the stream.
    pub fn pause(&self) -> Result<()> {
        self.stream.pause().map_err(Into::into)
    }
}

/// A running or paused audio input stream.
///
/// Dropping this value stops and destroys the underlying Cpal stream.
/// The stream is initially paused after construction; call [`InputStream::play`]
/// to start audio capture.
pub struct InputStream {
    pub(crate) stream: cpal::Stream,
}

impl InputStream {
    /// Starts (or resumes) audio capture.
    pub fn play(&self) -> Result<()> {
        self.stream.play().map_err(Into::into)
    }

    /// Pauses audio capture without destroying the stream.
    pub fn pause(&self) -> Result<()> {
        self.stream.pause().map_err(Into::into)
    }
}
