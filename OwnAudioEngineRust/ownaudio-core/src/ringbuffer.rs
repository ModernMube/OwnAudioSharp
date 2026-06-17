use rtrb::{Consumer, Producer, RingBuffer};

/// Creates a matched writer/reader pair for a lock-free SPSC audio ring buffer.
///
/// `capacity_samples` is fixed for the buffer's lifetime; no reallocation
/// happens after this call.  Typical sizing: `sample_rate * channels * latency_ms / 1000`.
///
/// The writer is intended for the producer thread (decoder / input callback);
/// the reader for the real-time audio output callback.  Both are `Send`.
pub fn ring_buffer(capacity_samples: usize) -> (RingBufferWriter, RingBufferReader) {
    let (producer, consumer) = RingBuffer::<f32>::new(capacity_samples);
    (RingBufferWriter { producer }, RingBufferReader { consumer })
}

/// Write-side of the SPSC audio ring buffer.
///
/// Safe to send to any thread.  All writes are non-blocking and lock-free.
pub struct RingBufferWriter {
    producer: Producer<f32>,
}

/// Read-side of the SPSC audio ring buffer.
///
/// Safe to send to the real-time audio callback thread.  All reads are
/// non-blocking and lock-free.
pub struct RingBufferReader {
    consumer: Consumer<f32>,
}

impl RingBufferWriter {
    /// Writes as many samples from `samples` as the buffer can currently accept.
    ///
    /// Returns the number of samples actually written.  Never allocates and never
    /// blocks.  The caller is responsible for handling the dropped tail (overflow).
    pub fn write(&mut self, samples: &[f32]) -> usize {
        let available = self.producer.slots();
        let to_write = samples.len().min(available);
        if to_write == 0 {
            return 0;
        }
        // SAFETY: we checked `to_write <= slots()` above.
        let mut chunk = self.producer.write_chunk_uninit(to_write).expect("slots checked");
        let (first, second) = chunk.as_mut_slices();
        let first_len = first.len().min(to_write);
        for (dst, &src) in first[..first_len].iter_mut().zip(samples) {
            dst.write(src);
        }
        let written_in_first = first_len;
        let remaining = to_write - written_in_first;
        for (dst, &src) in second[..remaining].iter_mut().zip(&samples[written_in_first..]) {
            dst.write(src);
        }
        // SAFETY: we initialised exactly `to_write` elements above.
        unsafe { chunk.commit_all() };
        to_write
    }
}

impl RingBufferReader {
    /// Reads up to `out.len()` samples from the buffer.
    ///
    /// Returns the number of samples actually read.  If fewer samples are
    /// available than `out.len()`, only the available portion is filled; the
    /// caller must silence-fill the rest.  Never allocates and never blocks.
    pub fn read(&mut self, out: &mut [f32]) -> usize {
        let available = self.consumer.slots();
        let to_read = out.len().min(available);
        if to_read == 0 {
            return 0;
        }
        // SAFETY: `to_read <= slots()`.
        let chunk = self.consumer.read_chunk(to_read).expect("slots checked");
        let (first, second) = chunk.as_slices();
        let first_len = first.len();
        out[..first_len].copy_from_slice(first);
        out[first_len..to_read].copy_from_slice(&second[..to_read - first_len]);
        chunk.commit_all();
        to_read
    }

    /// Returns the number of samples currently available to read.
    pub fn available(&self) -> usize {
        self.consumer.slots()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn basic_write_read() {
        let (mut writer, mut reader) = ring_buffer(16);
        let data = [1.0f32, 2.0, 3.0, 4.0];
        assert_eq!(writer.write(&data), 4);
        assert_eq!(reader.available(), 4);

        let mut out = [0.0f32; 4];
        assert_eq!(reader.read(&mut out), 4);
        assert_eq!(out, data);
    }

    #[test]
    fn underrun_returns_partial() {
        let (mut writer, mut reader) = ring_buffer(16);
        writer.write(&[1.0, 2.0]);
        let mut out = [0.0f32; 8];
        let read = reader.read(&mut out);
        assert_eq!(read, 2);
        assert_eq!(out[0], 1.0);
        assert_eq!(out[1], 2.0);
    }

    #[test]
    fn overflow_drops_excess() {
        let (mut writer, mut reader) = ring_buffer(4);
        let data = [1.0f32; 8];
        let written = writer.write(&data);
        assert_eq!(written, 4);

        let mut out = [0.0f32; 4];
        assert_eq!(reader.read(&mut out), 4);
    }

    #[test]
    fn capacity_boundary() {
        let (mut writer, mut reader) = ring_buffer(3);
        assert_eq!(writer.write(&[0.1, 0.2, 0.3]), 3);
        assert_eq!(writer.write(&[0.4]), 0);
        let mut out = [0.0f32; 3];
        reader.read(&mut out);
        assert!((out[0] - 0.1).abs() < 1e-7);
        assert!((out[1] - 0.2).abs() < 1e-7);
        assert!((out[2] - 0.3).abs() < 1e-7);
    }

    #[test]
    fn empty_read_returns_zero() {
        let (_writer, mut reader) = ring_buffer(8);
        let mut out = [0.0f32; 4];
        assert_eq!(reader.read(&mut out), 0);
    }
}
