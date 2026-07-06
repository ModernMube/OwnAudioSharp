//! Shared, lock-free stream error state.
//!
//! cpal delivers stream errors (device removed, backend failure, format change)
//! on an internal callback — [`AudioEngine::open_output_stream`] and
//! [`AudioEngine::open_input_stream`] route those into a [`StreamErrorState`]
//! that both the audio side (writer) and the control side (poller) share through
//! an `Arc`. The control thread polls it periodically to raise a device-lost /
//! faulted event, instead of the error vanishing into an `eprintln!`.
//!
//! [`AudioEngine::open_output_stream`]: crate::AudioEngine::open_output_stream
//! [`AudioEngine::open_input_stream`]: crate::AudioEngine::open_input_stream

use std::sync::atomic::{AtomicU32, AtomicU64, Ordering};

/// Classification of a cpal stream error, published to the control side.
///
/// The numeric values are part of the FFI contract (mirrored by the C# enum),
/// so existing discriminants must stay stable.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u32)]
pub enum StreamErrorKind {
    /// No error has been observed on this stream.
    None = 0,
    /// The audio device is no longer available (unplugged, disabled, lost on
    /// sleep/wake or a mixer sample-rate change).
    DeviceNotAvailable = 1,
    /// A backend-specific error the platform reported that is not a plain device
    /// removal.
    BackendSpecific = 2,
}

impl StreamErrorKind {
    /// Reconstructs a kind from its raw discriminant, mapping unknown values to
    /// [`StreamErrorKind::BackendSpecific`].
    #[inline]
    pub fn from_u32(raw: u32) -> Self {
        match raw {
            0 => StreamErrorKind::None,
            1 => StreamErrorKind::DeviceNotAvailable,
            _ => StreamErrorKind::BackendSpecific,
        }
    }
}

/// Lock-free error state shared between a stream's cpal error callback and the
/// control thread that polls it.
///
/// Both fields are plain atomics, so the (rare) error callback never blocks and
/// the poll path is wait-free. `count` lets the control side distinguish a fresh
/// error from a previously-seen one even if `kind` repeats.
#[derive(Debug)]
pub struct StreamErrorState {
    /// Most recent [`StreamErrorKind`] discriminant.
    kind: AtomicU32,
    /// Monotonic count of errors reported on this stream since it opened.
    count: AtomicU64,
}

impl StreamErrorState {
    /// Creates a clean state with no error recorded.
    pub fn new() -> Self {
        Self {
            kind: AtomicU32::new(StreamErrorKind::None as u32),
            count: AtomicU64::new(0),
        }
    }

    /// Records a cpal stream error, called from the audio backend's error
    /// callback. Never blocks or allocates.
    ///
    /// Device-loss-class errors (device gone, host gone, stream invalidated by a
    /// route/sample-rate change) collapse to [`StreamErrorKind::DeviceNotAvailable`]
    /// so the control side treats them uniformly as "the stream stopped and must
    /// be reopened"; anything else is reported as
    /// [`StreamErrorKind::BackendSpecific`].
    pub fn record(&self, err: &cpal::Error) {
        let kind = match err.kind() {
            cpal::ErrorKind::DeviceNotAvailable
            | cpal::ErrorKind::HostUnavailable
            | cpal::ErrorKind::StreamInvalidated => StreamErrorKind::DeviceNotAvailable,
            _ => StreamErrorKind::BackendSpecific,
        };
        self.kind.store(kind as u32, Ordering::Release);
        self.count.fetch_add(1, Ordering::AcqRel);
    }

    /// Returns the most recently recorded error kind.
    #[inline]
    pub fn kind(&self) -> StreamErrorKind {
        StreamErrorKind::from_u32(self.kind.load(Ordering::Acquire))
    }

    /// Returns the number of errors reported on this stream so far. A change in
    /// this value between two polls means a new error occurred.
    #[inline]
    pub fn count(&self) -> u64 {
        self.count.load(Ordering::Acquire)
    }
}

impl Default for StreamErrorState {
    fn default() -> Self {
        Self::new()
    }
}
