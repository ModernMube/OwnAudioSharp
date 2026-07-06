use std::sync::Arc;

use cpal::traits::StreamTrait;

use crate::error::Result;
use crate::stream_error::StreamErrorState;

/// A running or paused audio output stream.
///
/// Dropping this value stops and destroys the underlying Cpal stream.
/// The stream is initially paused after construction; call [`OutputStream::play`]
/// to start audio output.
pub struct OutputStream {
    // cpal::Stream is not Send on all platforms, so we keep it behind the
    // module boundary and do not expose it.
    pub(crate) stream: cpal::Stream,
    /// Shared error state written by the cpal error callback; the control side
    /// polls it (via [`OutputStream::error_state`]) to detect device loss.
    pub(crate) error_state: Arc<StreamErrorState>,
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

    /// Shared error state for this stream. The control thread polls it to learn
    /// when the audio backend reported a device-lost / backend error.
    #[inline]
    pub fn error_state(&self) -> &Arc<StreamErrorState> {
        &self.error_state
    }
}

/// A running or paused audio input stream.
///
/// Dropping this value stops and destroys the underlying Cpal stream.
/// The stream is initially paused after construction; call [`InputStream::play`]
/// to start audio capture.
pub struct InputStream {
    pub(crate) stream: cpal::Stream,
    /// Shared error state written by the cpal error callback; the control side
    /// polls it (via [`InputStream::error_state`]) to detect device loss.
    pub(crate) error_state: Arc<StreamErrorState>,
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

    /// Shared error state for this stream. The control thread polls it to learn
    /// when the audio backend reported a device-lost / backend error.
    #[inline]
    pub fn error_state(&self) -> &Arc<StreamErrorState> {
        &self.error_state
    }
}
