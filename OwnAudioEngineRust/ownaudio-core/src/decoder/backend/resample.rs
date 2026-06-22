//! Streaming wrapper around the FFT-based [`Resampler`](crate::resampler::Resampler).
//!
//! The decoder produces variable-size interleaved packets, while the underlying
//! `Resampler` requires a fixed input chunk size in planar layout.  This helper
//! bridges the two: it accumulates incoming frames per channel, runs the
//! resampler whenever a full chunk is available, and re-interleaves the output.

use crate::error::Result;
use crate::resampler::Resampler;

/// Fixed number of input frames fed to the resampler per process call.
const CHUNK_FRAMES: usize = 1024;

/// Accumulating, interleaved-in / interleaved-out resampler.
pub(crate) struct StreamResampler {
    inner: Resampler,
    channels: usize,

    /// Per-channel input accumulators (planar).
    in_acc: Vec<Vec<f32>>,
    /// Reused per-channel input chunk handed to the resampler.
    in_chunk: Vec<Vec<f32>>,
    /// Reused per-channel output scratch, sized to `output_frames_max`.
    out_scratch: Vec<Vec<f32>>,
}

impl StreamResampler {
    /// Creates a resampler converting `input_rate` → `output_rate` for the given
    /// interleaved `channels` count.
    pub(crate) fn new(input_rate: u32, output_rate: u32, channels: usize) -> Result<Self> {
        let inner = Resampler::new(input_rate, output_rate, channels, CHUNK_FRAMES)?;
        let out_max = inner.output_frames_max();

        Ok(Self {
            inner,
            channels,
            in_acc: vec![Vec::with_capacity(CHUNK_FRAMES * 2); channels],
            in_chunk: vec![vec![0.0f32; CHUNK_FRAMES]; channels],
            out_scratch: vec![vec![0.0f32; out_max]; channels],
        })
    }

    /// Feeds an interleaved input buffer and appends all resampled interleaved
    /// output that can be produced so far to `out`.
    pub(crate) fn push_interleaved(&mut self, interleaved: &[f32], out: &mut Vec<f32>) {
        let frames = interleaved.len() / self.channels;
        for f in 0..frames {
            let base = f * self.channels;
            for c in 0..self.channels {
                self.in_acc[c].push(interleaved[base + c]);
            }
        }
        self.drain_full_chunks(out);
    }

    /// Processes every complete chunk currently buffered.
    fn drain_full_chunks(&mut self, out: &mut Vec<f32>) {
        while self.in_acc[0].len() >= CHUNK_FRAMES {
            for c in 0..self.channels {
                // Move the leading CHUNK_FRAMES frames into the reused chunk buffer.
                self.in_chunk[c].clear();
                self.in_chunk[c].extend_from_slice(&self.in_acc[c][..CHUNK_FRAMES]);
                self.in_acc[c].drain(..CHUNK_FRAMES);
            }

            let written = self
                .inner
                .process(&self.in_chunk, &mut self.out_scratch)
                .unwrap_or(0);
            interleave_append(&self.out_scratch, self.channels, written, out);
        }
    }

    /// Flushes any remaining buffered input by zero-padding to a full chunk.
    ///
    /// Call once at end-of-stream so the trailing samples are not lost.
    pub(crate) fn flush(&mut self, out: &mut Vec<f32>) {
        let remaining = self.in_acc[0].len();
        if remaining == 0 {
            return;
        }
        for c in 0..self.channels {
            self.in_acc[c].resize(CHUNK_FRAMES, 0.0);
        }
        self.drain_full_chunks(out);
        // Discard any partial chunk left after padding.
        for acc in &mut self.in_acc {
            acc.clear();
        }
    }

    /// Drops all buffered input/output state after a seek.
    pub(crate) fn reset(&mut self) {
        for acc in &mut self.in_acc {
            acc.clear();
        }
    }
}

/// Interleaves the first `frames` of each planar channel slice and appends to `out`.
fn interleave_append(planar: &[Vec<f32>], channels: usize, frames: usize, out: &mut Vec<f32>) {
    for f in 0..frames {
        for ch in planar.iter().take(channels) {
            out.push(ch[f]);
        }
    }
}
