use rubato::{FftFixedIn, Resampler as RubatoResampler};

use crate::error::{AudioError, Result};

/// Sample-rate converter built on rubato's `FftFixedIn`.
///
/// Uses FFT-based resampling for fixed input/output sample rate ratios
/// (e.g. 44 100 → 48 000 Hz device conversion). This is considerably faster
/// than sinc-based approaches and delivers the best quality for static ratios.
/// SIMD acceleration is provided automatically via RustFFT:
/// AVX/SSE on x86_64, NEON on aarch64.
///
/// All working memory is pre-allocated in [`Resampler::new`]; the
/// [`process`] method does not heap-allocate on the steady-state path.
///
/// Input and output use **planar** layout (one `Vec<f32>` per channel),
/// which matches rubato's native format. Use [`crate::format::interleave`]
/// and [`crate::format::deinterleave`] to convert between interleaved and
/// planar representations.
pub struct Resampler {
    inner: FftFixedIn<f32>,
    /// Pre-allocated output scratch (one element per channel, each sized
    /// to hold `output_frames_max()` frames).
    output_scratch: Vec<Vec<f32>>,
    input_rate: u32,
    output_rate: u32,
}

impl Resampler {
    /// Creates a new FFT-based resampler for a fixed sample-rate ratio.
    ///
    /// - `input_rate` / `output_rate` — source and target sample rates in Hz.
    /// - `channels` — number of audio channels (must match the slices passed to [`process`]).
    /// - `chunk_size` — exact number of input frames per [`process`] call.
    ///   Every call to [`process`] must pass exactly this many frames per channel.
    ///
    /// # Errors
    /// Returns [`AudioError::ResamplerInit`] if rubato rejects the configuration.
    pub fn new(
        input_rate: u32,
        output_rate: u32,
        channels: usize,
        chunk_size: usize,
    ) -> Result<Self> {
        let inner = FftFixedIn::<f32>::new(
            input_rate as usize,
            output_rate as usize,
            chunk_size,
            2, // sub_chunks: 2 gives better quality with minimal overhead
            channels,
        )
        .map_err(|e| AudioError::ResamplerInit(e.to_string()))?;

        let max_out = inner.output_frames_max();
        let output_scratch = vec![vec![0.0f32; max_out]; channels];

        Ok(Self {
            inner,
            output_scratch,
            input_rate,
            output_rate,
        })
    }

    /// Resamples `input` (planar, one `Vec<f32>` per channel) into `out`.
    ///
    /// Each channel slice in `input` must contain exactly `chunk_size` frames
    /// (the value passed to [`new`]).
    ///
    /// `out` must have `channels` elements, each pre-allocated with at least
    /// [`output_frames_max`] capacity. Use `vec![0.0f32; rs.output_frames_max()]`
    /// when constructing these buffers and reuse them across calls.
    ///
    /// Returns the number of output frames written per channel.
    ///
    /// # Errors
    /// - [`AudioError::UnsupportedConfig`] if the number of frames in any
    ///   `input` channel does not equal `chunk_size` passed to [`new`].
    /// - [`AudioError::ResamplerProcess`] if rubato reports a processing error.
    ///
    /// # Panics
    /// Panics if `input.len() != out.len()` (channel count mismatch).
    pub fn process(&mut self, input: &[Vec<f32>], out: &mut [Vec<f32>]) -> Result<usize> {
        assert_eq!(
            input.len(),
            out.len(),
            "process: input and out must have the same channel count"
        );

        let expected = self.inner.input_frames_max();
        if let Some(ch) = input.iter().find(|c| c.len() != expected) {
            return Err(AudioError::UnsupportedConfig(format!(
                "resampler: input has {} frames, expected exactly {}",
                ch.len(),
                expected
            )));
        }

        let (_, frames_written) = self
            .inner
            .process_into_buffer(input, &mut self.output_scratch, None)
            .map_err(|e| AudioError::ResamplerProcess(e.to_string()))?;

        for (out_ch, scratch_ch) in out.iter_mut().zip(self.output_scratch.iter()) {
            debug_assert!(
                out_ch.len() >= frames_written,
                "out buffer too small; pre-allocate with output_frames_max()"
            );
            out_ch[..frames_written].copy_from_slice(&scratch_ch[..frames_written]);
            out_ch.truncate(frames_written);
        }

        Ok(frames_written)
    }

    /// Returns the maximum number of output frames that a single [`process`]
    /// call can produce. Use this to size the `out` parameter.
    pub fn output_frames_max(&self) -> usize {
        self.inner.output_frames_max()
    }

    /// Source sample rate in Hz.
    pub fn input_rate(&self) -> u32 {
        self.input_rate
    }

    /// Target sample rate in Hz.
    pub fn output_rate(&self) -> u32 {
        self.output_rate
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sine_channel(freq: f32, sample_rate: u32, frames: usize) -> Vec<f32> {
        use std::f32::consts::TAU;
        let sr = sample_rate as f32;
        (0..frames)
            .map(|i| (i as f32 / sr * freq * TAU).sin() * 0.5)
            .collect()
    }

    #[test]
    fn upsampling_44100_to_48000() {
        let frames_in = 1024;
        let mut rs = Resampler::new(44_100, 48_000, 1, frames_in).unwrap();
        let input = vec![sine_channel(440.0, 44_100, frames_in)];
        let mut out = vec![vec![0.0f32; rs.output_frames_max()]];
        let written = rs.process(&input, &mut out).unwrap();
        assert!(written > 0, "upsampling should produce output frames");
        assert!(
            out[0][..written].iter().all(|&s| s.is_finite()),
            "output must not contain NaN or inf"
        );
    }

    #[test]
    fn upsampling_steady_state_ratio() {
        let frames_in = 512;
        let mut rs = Resampler::new(44_100, 48_000, 1, frames_in).unwrap();
        let mut total_in = 0usize;
        let mut total_out = 0usize;
        for _ in 0..16 {
            let input = vec![sine_channel(440.0, 44_100, frames_in)];
            // Re-allocate to output_frames_max each iteration since truncate
            // shrinks the vec after the first process call.
            let mut out = vec![vec![0.0f32; rs.output_frames_max()]];
            let written = rs.process(&input, &mut out).unwrap();
            total_in += frames_in;
            total_out += written;
        }
        let ratio = total_out as f64 / total_in as f64;
        let expected = 48_000.0 / 44_100.0;
        assert!(
            (ratio - expected).abs() < 0.02,
            "steady-state output ratio {ratio:.4} deviates from expected {expected:.4}"
        );
    }

    #[test]
    fn downsampling_48000_to_44100() {
        let frames_in = 1024;
        let mut rs = Resampler::new(48_000, 44_100, 1, frames_in).unwrap();
        let input = vec![sine_channel(440.0, 48_000, frames_in)];
        let mut out = vec![vec![0.0f32; rs.output_frames_max()]];
        let written = rs.process(&input, &mut out).unwrap();
        assert!(written < frames_in, "downsampling should produce fewer frames");
        assert!(
            out[0][..written].iter().all(|&s| s.is_finite()),
            "output must not contain NaN or inf"
        );
    }

    #[test]
    fn identity_rate_passthrough() {
        let frames_in = 512;
        let mut rs = Resampler::new(48_000, 48_000, 2, frames_in).unwrap();
        let ch = sine_channel(440.0, 48_000, frames_in);
        let input = vec![ch.clone(), ch];
        let mut out = vec![vec![0.0f32; rs.output_frames_max()]; 2];
        let written = rs.process(&input, &mut out).unwrap();
        assert!(written > 0);
        assert!(out[0][..written].iter().all(|&s| s.is_finite()));
        assert!(out[1][..written].iter().all(|&s| s.is_finite()));
    }

    #[test]
    fn wrong_input_size_returns_error() {
        let frames_in = 256;
        let mut rs = Resampler::new(44_100, 48_000, 1, frames_in).unwrap();
        let too_big = vec![vec![0.0f32; frames_in + 1]];
        let mut out = vec![vec![0.0f32; rs.output_frames_max()]];
        let result = rs.process(&too_big, &mut out);
        assert!(
            matches!(result, Err(AudioError::UnsupportedConfig(_))),
            "expected UnsupportedConfig error for wrong input size"
        );
    }

    #[test]
    fn accessors_return_configured_rates() {
        let rs = Resampler::new(44_100, 48_000, 2, 512).unwrap();
        assert_eq!(rs.input_rate(), 44_100);
        assert_eq!(rs.output_rate(), 48_000);
    }
}
