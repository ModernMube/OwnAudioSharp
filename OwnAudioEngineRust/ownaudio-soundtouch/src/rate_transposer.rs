//! Sample-rate transposer pipeline stage — port of `RateTransposer.cs`.
//!
//! Combines a fractional-rate [`Transposer`] with an [`AntiAliasFilter`].  When
//! slowing down (`rate < 1`) the samples are transposed first and then
//! low-pass filtered; when speeding up (`rate > 1`) the filter runs first to
//! remove the frequencies that would otherwise fold over.

use crate::fifo_buffer::FifoSampleBuffer;
use crate::filter::AntiAliasFilter;
use crate::interpolate::Transposer;
use crate::MAX_CHANNELS;

/// Anti-alias-filtered sample-rate transposer.
pub struct RateTransposer {
    aa_filter: AntiAliasFilter,
    transposer: Transposer,
    input_buffer: FifoSampleBuffer,
    mid_buffer: FifoSampleBuffer,
    output_buffer: FifoSampleBuffer,
    use_aa_filter: bool,
}

impl RateTransposer {
    /// Creates a transposer with a 64-tap anti-alias filter enabled.
    pub fn new() -> Self {
        let mut rt = RateTransposer {
            aa_filter: AntiAliasFilter::new(64),
            transposer: Transposer::new(),
            input_buffer: FifoSampleBuffer::new(2),
            mid_buffer: FifoSampleBuffer::new(2),
            output_buffer: FifoSampleBuffer::new(2),
            use_aa_filter: true,
        };
        rt.clear();
        rt
    }

    /// `true` when neither the output nor the input buffer holds samples.
    #[inline]
    pub fn is_empty(&self) -> bool {
        self.output_buffer.is_empty() && self.input_buffer.is_empty()
    }

    /// Approximate initial input-output latency in frames.
    #[inline]
    pub fn latency(&self) -> usize {
        self.transposer.latency()
            + if self.use_aa_filter {
                self.aa_filter.length() / 2
            } else {
                0
            }
    }

    /// Whether the anti-alias filter is currently enabled.
    #[inline]
    pub fn is_aa_filter_enabled(&self) -> bool {
        self.use_aa_filter
    }

    /// Enables/disables the anti-alias filter (clears the pipeline).
    pub fn enable_aa_filter(&mut self, enable: bool) {
        self.use_aa_filter = enable;
        self.clear();
    }

    /// Anti-alias filter tap count.
    #[inline]
    pub fn aa_filter_length(&self) -> usize {
        self.aa_filter.length()
    }

    /// Sets the anti-alias filter tap count.
    pub fn set_aa_filter_length(&mut self, length: usize) {
        self.aa_filter.set_length(length);
    }

    /// Sets the target rate and redesigns the anti-alias cut-off accordingly.
    pub fn set_rate(&mut self, new_rate: f64) {
        self.transposer.set_rate(new_rate);
        let cutoff = if new_rate > 1.0 {
            0.5 / new_rate
        } else {
            0.5 * new_rate
        };
        self.aa_filter.set_cutoff_freq(cutoff);
    }

    /// Sets the channel count for every internal buffer.
    pub fn set_channels(&mut self, channels: usize) {
        if channels == 0 || channels > MAX_CHANNELS || self.transposer.channels() == channels {
            return;
        }
        self.transposer.set_channels(channels);
        self.input_buffer.set_channels(channels);
        self.mid_buffer.set_channels(channels);
        self.output_buffer.set_channels(channels);
    }

    /// Feeds `num` interleaved frames into the transposer.
    pub fn put_samples(&mut self, src: &[f32], num: usize) {
        self.process_samples(src, num);
    }

    /// Clears all buffers and prefills the input with `latency` silent frames so
    /// the first real samples are not lost.
    pub fn clear(&mut self) {
        self.output_buffer.clear();
        self.mid_buffer.clear();
        self.input_buffer.clear();
        self.transposer.reset_registers();
        let prefill = self.latency();
        self.input_buffer.add_silent(prefill);
    }

    /// Output buffer (the pipeline tail).
    #[inline]
    pub fn output(&self) -> &FifoSampleBuffer {
        &self.output_buffer
    }

    /// Mutable output buffer, used by the processor for pipeline hand-off.
    #[inline]
    pub fn output_mut(&mut self) -> &mut FifoSampleBuffer {
        &mut self.output_buffer
    }

    fn process_samples(&mut self, src: &[f32], num: usize) {
        if num == 0 {
            return;
        }
        self.input_buffer.put_samples_from(src, num);

        if !self.use_aa_filter {
            self.transposer
                .transpose(&mut self.output_buffer, &mut self.input_buffer);
            return;
        }

        if self.transposer.rate() < 1.0 {
            // Transpose first, then remove the aliasing it introduced.
            self.transposer
                .transpose(&mut self.mid_buffer, &mut self.input_buffer);
            self.aa_filter
                .evaluate_buffers(&mut self.output_buffer, &mut self.mid_buffer);
        } else {
            // Filter first to prevent fold-over, then transpose.
            self.aa_filter
                .evaluate_buffers(&mut self.mid_buffer, &mut self.input_buffer);
            self.transposer
                .transpose(&mut self.output_buffer, &mut self.mid_buffer);
        }
    }
}

impl Default for RateTransposer {
    fn default() -> Self {
        Self::new()
    }
}
