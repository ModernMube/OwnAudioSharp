use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::Arc;

use cpal::traits::StreamTrait;

use crate::error::Result;
use crate::load::{LoadCounters, LoadSnapshot};
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
    /// Hardware playback latency in frames, refreshed by the data callback from
    /// the cpal timestamp (`playback - callback`). 0 until the first callback
    /// fires, or when the backend does not report a latency.
    pub(crate) latency_frames: Arc<AtomicU32>,
    /// Frames the last callback actually asked for. The driver is free to ignore
    /// the size we requested, and this is the only place that shows up.
    pub(crate) callback_frames: Arc<AtomicU32>,
    /// How long each callback took against the budget its frame count buys.
    pub(crate) load_counters: Arc<LoadCounters>,
    /// Channel count the device was actually opened with, after config adaptation.
    pub(crate) device_channels: u16,
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

    /// Hardware playback latency in frames — how far ahead of the DAC the
    /// callback runs. 0 means not yet known (no callback fired) or the backend
    /// does not report one. Read it after playback has started for a live value.
    #[inline]
    pub fn latency_frames(&self) -> u32 {
        self.latency_frames.load(Ordering::Relaxed)
    }

    /// Frames the device handed the last render callback. `0` until the first one
    /// fires. Backends that vary the block size report the most recent one, so this
    /// is what the driver granted, not what we asked for.
    #[inline]
    pub fn callback_frames(&self) -> u32 {
        self.callback_frames.load(Ordering::Relaxed)
    }

    /// Channels the device really opened with. The requested width is only a request —
    /// [`crate::config`] adapts it to the nearest the device supports — so anything drawing
    /// output sockets (or deciding how far a route may reach) has to ask here, not look at
    /// what it asked for.
    #[inline]
    pub fn channel_count(&self) -> u16 {
        self.device_channels
    }

    /// DSP load since the stream opened or the counters were last reset. The
    /// underrun tally says a block was already late; this says how close the
    /// others came.
    #[inline]
    pub fn load(&self) -> LoadSnapshot {
        self.load_counters.snapshot()
    }

    /// Zeroes the load tallies. Worth calling once playback has settled — the
    /// first blocks after device start are never representative.
    #[inline]
    pub fn reset_load(&self) {
        self.load_counters.reset();
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
    /// Hardware capture latency in frames, refreshed by the data callback from
    /// the cpal timestamp (`callback - capture`). 0 until the first callback
    /// fires, or when the backend does not report a latency.
    pub(crate) latency_frames: Arc<AtomicU32>,
    /// Frames the last callback actually delivered.
    pub(crate) callback_frames: Arc<AtomicU32>,
    /// Channel count the capture device was actually opened with.
    pub(crate) device_channels: u16,
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

    /// Hardware capture latency in frames — how long ago the samples in each
    /// buffer actually hit the ADC. 0 means not yet known (no callback fired) or
    /// the backend does not report one. Subtract it from the capture position to
    /// line a recording up with the real timeline.
    #[inline]
    pub fn latency_frames(&self) -> u32 {
        self.latency_frames.load(Ordering::Relaxed)
    }

    /// Frames the device handed the last capture callback. `0` until the first one
    /// fires. Same caveat as on the render side: a backend with a variable block
    /// size reports the most recent block, not a fixed contract.
    #[inline]
    pub fn callback_frames(&self) -> u32 {
        self.callback_frames.load(Ordering::Relaxed)
    }

    /// Capture channels the device really opened with — the counterpart of
    /// [`OutputStream::channel_count`], and what a capture bridge fans out from.
    #[inline]
    pub fn channel_count(&self) -> u16 {
        self.device_channels
    }
}
