//! First-in-first-out sample buffer — port of `FifoSampleBuffer.cs`.
//!
//! Stores interleaved `f32` frames and takes care of the book-keeping that the
//! WSOLA pipeline relies on: direct access to the head of the used region
//! ([`FifoSampleBuffer::ptr_begin`]) and to the insertion point past the used
//! region ([`FifoSampleBuffer::ptr_end`]).
//!
//! ## Real-time behaviour
//!
//! In contrast to the C# original, growth is amortised rather than pool-based:
//! the backing [`Vec`] only ever reallocates when the requested capacity exceeds
//! what is already allocated, and otherwise *rewinds* (moves the live region
//! back to the front).  After the pipeline has warmed up the working set is
//! stable, so steady-state `put`/`receive` calls neither grow nor reallocate —
//! satisfying the zero-allocation requirement on the hot path while preserving
//! the exact semantics the C# pipeline expects.
//!
//! A frame ("sample" in SoundTouch terminology) consists of one value per
//! channel; `channels = 2` therefore means two `f32` per frame.

use crate::MAX_CHANNELS;

/// Interleaved FIFO sample buffer.
pub struct FifoSampleBuffer {
    /// Backing storage; its length is the allocated capacity in `f32` values.
    buffer: Vec<f32>,
    /// Number of channels (1 = mono, 2 = stereo, …).
    channels: usize,
    /// Number of frames currently held.
    samples_in_buffer: usize,
    /// Offset, in frames, of the live region from the start of `buffer`.
    buffer_pos: usize,
}

impl FifoSampleBuffer {
    /// Creates a new buffer for the given channel count with a small initial
    /// capacity, mirroring the C# constructor (`EnsureCapacity(32)`).
    ///
    /// `channels` is clamped to at least 1; this is a configuration-time call
    /// and may allocate.
    pub fn new(channels: usize) -> Self {
        let channels = channels.max(1);
        let mut b = FifoSampleBuffer {
            buffer: Vec::new(),
            channels,
            samples_in_buffer: 0,
            buffer_pos: 0,
        };
        b.ensure_capacity(32);
        b
    }

    /// Number of frames currently available for output.
    #[inline]
    pub fn available_samples(&self) -> usize {
        self.samples_in_buffer
    }

    /// Returns `true` when no frames are available.
    #[inline]
    pub fn is_empty(&self) -> bool {
        self.samples_in_buffer == 0
    }

    /// Current channel count.
    #[inline]
    pub fn channels(&self) -> usize {
        self.channels
    }

    /// Reinterprets the buffer for a new channel count.
    ///
    /// Follows the C# semantics of preserving the total interleaved value count;
    /// in practice the pipeline only changes channels while the buffer is empty.
    pub fn set_channels(&mut self, channels: usize) {
        if channels == 0 || channels > MAX_CHANNELS || channels == self.channels {
            return;
        }
        let total_floats = self.channels * self.samples_in_buffer;
        self.channels = channels;
        self.samples_in_buffer = total_floats / channels;
    }

    /// Read-only view of the live region, starting at the oldest frame.
    #[inline]
    pub fn ptr_begin(&self) -> &[f32] {
        &self.buffer[self.buffer_pos * self.channels..]
    }

    /// Mutable view of the live region, starting at the oldest frame.
    #[inline]
    pub fn ptr_begin_mut(&mut self) -> &mut [f32] {
        let off = self.buffer_pos * self.channels;
        &mut self.buffer[off..]
    }

    /// Ensures `slack_capacity` free frames past the live region and returns a
    /// mutable view of the insertion point.
    ///
    /// After writing through this slice the caller must call
    /// [`FifoSampleBuffer::commit_put`] with the number of frames written.
    pub fn ptr_end(&mut self, slack_capacity: usize) -> &mut [f32] {
        self.ensure_capacity(self.samples_in_buffer + slack_capacity);
        let off = (self.buffer_pos + self.samples_in_buffer) * self.channels;
        &mut self.buffer[off..]
    }

    /// Copies `num` frames from `src` into the buffer.
    pub fn put_samples_from(&mut self, src: &[f32], num: usize) {
        let n = num * self.channels;
        let dest = self.ptr_end(num);
        dest[..n].copy_from_slice(&src[..n]);
        self.samples_in_buffer += num;
    }

    /// Book-keeping update after frames were written directly via
    /// [`FifoSampleBuffer::ptr_end`].  Grows the buffer if necessary first.
    pub fn commit_put(&mut self, num: usize) {
        self.ensure_capacity(self.samples_in_buffer + num);
        self.samples_in_buffer += num;
    }

    /// Copies up to `max_samples` frames into `output`, removing them from the
    /// buffer.  Returns the number of frames written.
    pub fn receive_samples_into(&mut self, output: &mut [f32], max_samples: usize) -> usize {
        let num = max_samples.min(self.samples_in_buffer);
        let n = self.channels * num;
        output[..n].copy_from_slice(&self.ptr_begin()[..n]);
        self.receive_samples(num)
    }

    /// Removes up to `max_samples` frames from the front without copying them.
    /// Returns the number actually removed.
    pub fn receive_samples(&mut self, max_samples: usize) -> usize {
        if max_samples >= self.samples_in_buffer {
            let temp = self.samples_in_buffer;
            self.samples_in_buffer = 0;
            self.buffer_pos = 0;
            return temp;
        }
        self.samples_in_buffer -= max_samples;
        self.buffer_pos += max_samples;
        max_samples
    }

    /// Drops all frames.
    #[inline]
    pub fn clear(&mut self) {
        self.samples_in_buffer = 0;
        self.buffer_pos = 0;
    }

    /// Trims (only downward) the number of frames held.
    pub fn adjust_amount_of_samples(&mut self, num_samples: usize) -> usize {
        if num_samples < self.samples_in_buffer {
            self.samples_in_buffer = num_samples;
        }
        self.samples_in_buffer
    }

    /// Appends `num` frames of silence to the end of the buffer.
    pub fn add_silent(&mut self, num: usize) {
        let n = num * self.channels;
        let dest = self.ptr_end(num);
        dest[..n].fill(0.0);
        self.samples_in_buffer += num;
    }

    /// Moves all available frames from `other` into this buffer.
    pub fn move_samples_from(&mut self, other: &mut FifoSampleBuffer) {
        let n = other.available_samples();
        self.put_samples_from(other.ptr_begin(), n);
        other.receive_samples(n);
    }

    /// Moves the live region back to the front of the backing store.
    fn rewind(&mut self) {
        if self.buffer_pos == 0 {
            return;
        }
        if self.samples_in_buffer > 0 {
            let start = self.buffer_pos * self.channels;
            let len = self.samples_in_buffer * self.channels;
            self.buffer.copy_within(start..start + len, 0);
        }
        self.buffer_pos = 0;
    }

    /// Guarantees room for at least `capacity_req` frames, growing or rewinding
    /// as needed.  May allocate — call only at configuration time or during the
    /// pipeline warm-up, never expected in steady state.
    fn ensure_capacity(&mut self, capacity_req: usize) {
        let total_cap = self.buffer.len() / self.channels;
        let used_end = self.buffer_pos + self.samples_in_buffer;
        let available_at_end = total_cap.saturating_sub(used_end);

        if available_at_end >= capacity_req.saturating_sub(self.samples_in_buffer) {
            return;
        }

        if total_cap >= capacity_req {
            self.rewind();
            return;
        }

        let required_floats = capacity_req * self.channels;
        let mut new_size = required_floats.max(4096);
        let double = self.buffer.len() * 2;
        if new_size < double {
            new_size = double;
        }
        if new_size < required_floats {
            new_size = required_floats;
        }

        let mut new_buffer = vec![0.0f32; new_size];
        if self.samples_in_buffer > 0 {
            let start = self.buffer_pos * self.channels;
            let len = self.samples_in_buffer * self.channels;
            new_buffer[..len].copy_from_slice(&self.buffer[start..start + len]);
        }
        self.buffer = new_buffer;
        self.buffer_pos = 0;
    }

    /// Current allocated capacity, in frames.
    #[cfg(test)]
    pub(crate) fn capacity_frames(&self) -> usize {
        self.buffer.len() / self.channels
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn put_receive_roundtrip_stereo() {
        let mut fifo = FifoSampleBuffer::new(2);
        let input = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0]; // 3 stereo frames
        fifo.put_samples_from(&input, 3);
        assert_eq!(fifo.available_samples(), 3);
        assert!(!fifo.is_empty());

        let mut out = [0.0f32; 6];
        let got = fifo.receive_samples_into(&mut out, 3);
        assert_eq!(got, 3);
        assert_eq!(out, input);
        assert!(fifo.is_empty());
    }

    #[test]
    fn partial_receive_advances_position() {
        let mut fifo = FifoSampleBuffer::new(1);
        let input: Vec<f32> = (0..10).map(|i| i as f32).collect();
        fifo.put_samples_from(&input, 10);

        let mut out = [0.0f32; 4];
        let got = fifo.receive_samples_into(&mut out, 4);
        assert_eq!(got, 4);
        assert_eq!(out, [0.0, 1.0, 2.0, 3.0]);
        assert_eq!(fifo.available_samples(), 6);
        assert_eq!(fifo.ptr_begin()[0], 4.0);
    }

    #[test]
    fn receive_more_than_available_empties() {
        let mut fifo = FifoSampleBuffer::new(2);
        fifo.put_samples_from(&[1.0, 2.0], 1);
        let mut out = [0.0f32; 10];
        let got = fifo.receive_samples_into(&mut out, 5);
        assert_eq!(got, 1);
        assert!(fifo.is_empty());
    }

    #[test]
    fn add_silent_zeroes() {
        let mut fifo = FifoSampleBuffer::new(2);
        fifo.add_silent(4);
        assert_eq!(fifo.available_samples(), 4);
        assert!(fifo.ptr_begin()[..8].iter().all(|&v| v == 0.0));
    }

    #[test]
    fn move_samples_transfers_all() {
        let mut a = FifoSampleBuffer::new(1);
        let mut b = FifoSampleBuffer::new(1);
        a.put_samples_from(&[1.0, 2.0, 3.0], 3);
        b.move_samples_from(&mut a);
        assert!(a.is_empty());
        assert_eq!(b.available_samples(), 3);
        assert_eq!(&b.ptr_begin()[..3], &[1.0, 2.0, 3.0]);
    }

    #[test]
    fn steady_state_does_not_grow() {
        // After warm-up, alternating put/receive of a fixed block must reuse the
        // same allocation (rewind, never grow).
        let mut fifo = FifoSampleBuffer::new(2);
        let block = vec![0.5f32; 256 * 2];
        for _ in 0..8 {
            fifo.put_samples_from(&block, 256);
            let mut out = vec![0.0f32; 256 * 2];
            fifo.receive_samples_into(&mut out, 256);
        }
        let cap = fifo.capacity_frames();
        for _ in 0..1000 {
            fifo.put_samples_from(&block, 256);
            let mut out = vec![0.0f32; 256 * 2];
            fifo.receive_samples_into(&mut out, 256);
        }
        assert_eq!(fifo.capacity_frames(), cap, "buffer must not grow in steady state");
    }

    #[test]
    fn adjust_amount_only_trims_down() {
        let mut fifo = FifoSampleBuffer::new(1);
        fifo.put_samples_from(&[1.0, 2.0, 3.0, 4.0], 4);
        assert_eq!(fifo.adjust_amount_of_samples(2), 2);
        assert_eq!(fifo.adjust_amount_of_samples(10), 2);
    }
}
