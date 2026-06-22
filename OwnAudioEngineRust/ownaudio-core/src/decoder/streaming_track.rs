//! Prefetching streaming track: a background thread decodes ahead into a
//! lock-free ring buffer so the real-time consumer never blocks on I/O.

use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::Arc;
use std::thread::JoinHandle;
use std::time::Duration;

use crate::decoder::backend::create_backend;
use crate::decoder::stream_info::AudioStreamInfo;
use crate::decoder::AudioDecoderBackend;
use crate::error::Result;
use crate::ringbuffer::{ring_buffer, RingBufferReader, RingBufferWriter};

/// Minimum ring-buffer capacity in frames, regardless of the requested size.
const MIN_PREFETCH_FRAMES: usize = 4_096;

/// Number of interleaved samples decoded per prefetch iteration.
const DECODE_CHUNK_SAMPLES: usize = 8_192;

/// Idle sleep when the ring buffer is full (avoids busy-spinning the CPU).
const FULL_SLEEP: Duration = Duration::from_micros(500);

/// A streaming audio file reader with a dedicated prefetch thread.
///
/// The decoded output is exposed through [`read`](Self::read), which is
/// real-time safe: it only pops from a lock-free SPSC ring buffer and never
/// allocates or blocks.  The prefetch thread is stopped and joined on drop.
pub struct StreamingTrack {
    /// Read side of the ring buffer, consumed by the audio callback.
    reader: RingBufferReader,

    /// Shared seek request flag set by [`seek`](Self::seek).
    seek_requested: Arc<AtomicBool>,
    /// `true` from the moment a seek is requested until the prefetch thread has
    /// repositioned the backend; suppresses stale reads in between.
    seek_pending: Arc<AtomicBool>,
    /// Target output frame for a pending seek.
    seek_frame: Arc<AtomicU64>,
    /// Signals the prefetch thread to exit.
    stop_signal: Arc<AtomicBool>,
    /// Set by the prefetch thread once the source is fully decoded.
    eof_reached: Arc<AtomicBool>,
    /// Stale samples the consumer must drop from the ring after a seek, set by
    /// the prefetch thread once it has repositioned the backend.
    discard_samples: Arc<AtomicU64>,

    stream_info: AudioStreamInfo,

    /// Prefetch thread handle, joined in [`Drop`].
    prefetch_thread: Option<JoinHandle<()>>,
}

impl StreamingTrack {
    /// Opens `path`, selects a backend and spawns the prefetch thread.
    ///
    /// See [`open_streaming`](crate::decoder::open_streaming) for the parameter
    /// semantics.
    pub fn open(
        path: &str,
        target_sample_rate: u32,
        target_channels: u32,
        prefetch_frames: usize,
    ) -> Result<Self> {
        let backend = create_backend(path, target_sample_rate, target_channels)?;
        let stream_info = backend.stream_info();
        let channels = stream_info.channels.max(1) as usize;

        let frames = prefetch_frames.max(MIN_PREFETCH_FRAMES);
        let capacity_samples = frames * channels;

        let (writer, reader) = ring_buffer(capacity_samples);

        let seek_requested = Arc::new(AtomicBool::new(false));
        let seek_pending = Arc::new(AtomicBool::new(false));
        let seek_frame = Arc::new(AtomicU64::new(0));
        let stop_signal = Arc::new(AtomicBool::new(false));
        let eof_reached = Arc::new(AtomicBool::new(false));
        let discard_samples = Arc::new(AtomicU64::new(0));

        let prefetch_thread = spawn_prefetch_thread(
            backend,
            writer,
            capacity_samples,
            Arc::clone(&seek_requested),
            Arc::clone(&seek_pending),
            Arc::clone(&seek_frame),
            Arc::clone(&stop_signal),
            Arc::clone(&eof_reached),
            Arc::clone(&discard_samples),
        );

        Ok(Self {
            reader,
            seek_requested,
            seek_pending,
            seek_frame,
            stop_signal,
            eof_reached,
            discard_samples,
            stream_info,
            prefetch_thread: Some(prefetch_thread),
        })
    }

    /// Pops up to `dst.len()` decoded interleaved `f32` samples into `dst`.
    ///
    /// Returns the number of samples written.  Real-time safe: never blocks or
    /// allocates.  A return value smaller than `dst.len()` indicates either EOF
    /// or a transient prefetch underrun; the caller should silence the rest.
    #[inline]
    pub fn read(&mut self, dst: &mut [f32]) -> usize {
        // While a seek is in flight, suppress reads so the consumer never sees
        // pre-seek samples still buffered in the ring.
        if self.seek_pending.load(Ordering::Acquire) {
            return 0;
        }

        // Drop any stale samples left in the ring by a seek before returning data.
        let pending = self.discard_samples.load(Ordering::Acquire);
        if pending > 0 {
            let mut remaining = pending;
            while remaining > 0 {
                let chunk = remaining.min(dst.len() as u64) as usize;
                let dropped = self.reader.read(&mut dst[..chunk]);
                if dropped == 0 {
                    break;
                }
                remaining -= dropped as u64;
            }
            self.discard_samples
                .fetch_sub(pending - remaining, Ordering::Release);
        }

        self.reader.read(dst)
    }

    /// Requests a seek to `frame_position` (output frames).  Non-blocking — the
    /// prefetch thread performs the actual seek on its next iteration.
    pub fn seek(&self, frame_position: u64) {
        self.seek_frame.store(frame_position, Ordering::Release);
        self.seek_pending.store(true, Ordering::Release);
        self.seek_requested.store(true, Ordering::Release);
    }

    /// Decoded output format metadata.
    pub fn stream_info(&self) -> AudioStreamInfo {
        self.stream_info
    }

    /// Returns `true` once the source has been fully decoded and the ring
    /// buffer has been drained.
    pub fn is_eof(&self) -> bool {
        self.eof_reached.load(Ordering::Acquire) && self.reader.available() == 0
    }
}

impl Drop for StreamingTrack {
    fn drop(&mut self) {
        self.stop_signal.store(true, Ordering::Release);
        if let Some(handle) = self.prefetch_thread.take() {
            let _ = handle.join();
        }
    }
}

/// Spawns the background prefetch thread that owns the decoder backend.
#[allow(clippy::too_many_arguments)]
fn spawn_prefetch_thread(
    mut backend: Box<dyn AudioDecoderBackend>,
    mut writer: RingBufferWriter,
    capacity_samples: usize,
    seek_requested: Arc<AtomicBool>,
    seek_pending: Arc<AtomicBool>,
    seek_frame: Arc<AtomicU64>,
    stop_signal: Arc<AtomicBool>,
    eof_reached: Arc<AtomicBool>,
    discard_samples: Arc<AtomicU64>,
) -> JoinHandle<()> {
    std::thread::Builder::new()
        .name("ownaudio-prefetch".to_string())
        .spawn(move || {
            let mut decode_buf = vec![0.0f32; DECODE_CHUNK_SAMPLES];
            // Samples decoded but not yet accepted by the ring buffer.
            let mut leftover: Vec<f32> = Vec::new();

            loop {
                if stop_signal.load(Ordering::Acquire) {
                    break;
                }

                // Pending seek takes priority and invalidates buffered data.
                if seek_requested.swap(false, Ordering::AcqRel) {
                    let frame = seek_frame.load(Ordering::Acquire);
                    leftover.clear();
                    eof_reached.store(false, Ordering::Release);
                    if let Err(e) = backend.seek(frame) {
                        log::error!("prefetch: seek to frame {frame} failed: {e}");
                    }
                    // Everything still sitting in the ring is pre-seek data; tell
                    // the consumer to discard exactly that many samples so the next
                    // read returns audio from the new position.
                    let stale = (capacity_samples - writer.free()) as u64;
                    discard_samples.store(stale, Ordering::Release);
                    // Release the consumer now that the new position is in effect.
                    seek_pending.store(false, Ordering::Release);
                    continue;
                }

                // Flush any leftover from a previous iteration first.
                if !leftover.is_empty() {
                    let n = writer.write(&leftover);
                    if n > 0 {
                        leftover.drain(..n);
                    }
                    if !leftover.is_empty() {
                        std::thread::sleep(FULL_SLEEP);
                    }
                    continue;
                }

                // Don't decode if the ring buffer can't take a full chunk.
                if writer.free() < decode_buf.len() {
                    std::thread::sleep(FULL_SLEEP);
                    continue;
                }

                match backend.read_frames(&mut decode_buf) {
                    Ok(result) => {
                        if result.samples_written > 0 {
                            let n = writer.write(&decode_buf[..result.samples_written]);
                            if n < result.samples_written {
                                leftover.extend_from_slice(&decode_buf[n..result.samples_written]);
                            }
                        }
                        if result.is_eof {
                            eof_reached.store(true, Ordering::Release);
                            // Wait for either a stop or a seek request.
                            while !stop_signal.load(Ordering::Acquire)
                                && !seek_requested.load(Ordering::Acquire)
                            {
                                std::thread::sleep(Duration::from_millis(10));
                            }
                        }
                    }
                    Err(e) => {
                        log::error!("prefetch: decode error: {e}");
                        eof_reached.store(true, Ordering::Release);
                        while !stop_signal.load(Ordering::Acquire)
                            && !seek_requested.load(Ordering::Acquire)
                        {
                            std::thread::sleep(Duration::from_millis(10));
                        }
                    }
                }
            }
        })
        .expect("failed to spawn prefetch thread")
}
