//! Getting whatever the caller has into what the encoder wants: mono, 16 kHz, fixed-size chunks.
//!
//! The mel spectrogram is baked into the exported encoder graph, so there is deliberately no DSP
//! here — downmix, resample, slice, done.

use rubato::{FftFixedIn, Resampler};

use crate::error::{Mt3Error, Result};

/// Averages interleaved channels down to mono. Already-mono input is handed straight back.
pub fn to_mono(samples: &[f32], channels: u16) -> std::borrow::Cow<'_, [f32]> {
    if channels <= 1 {
        return std::borrow::Cow::Borrowed(samples);
    }

    let channels = channels as usize;
    let scale = 1.0 / channels as f32;
    let mono = samples
        .chunks_exact(channels)
        .map(|frame| frame.iter().sum::<f32>() * scale)
        .collect();

    std::borrow::Cow::Owned(mono)
}

/// Resamples mono audio to `target_rate`. Same rate in and out costs nothing.
pub fn resample(samples: &[f32], from_rate: u32, target_rate: u32) -> Result<Vec<f32>> {
    if from_rate == target_rate || samples.is_empty() {
        return Ok(samples.to_vec());
    }

    const CHUNK: usize = 4096;
    let mut resampler =
        FftFixedIn::<f32>::new(from_rate as usize, target_rate as usize, CHUNK, 2, 1)
            .map_err(|e| Mt3Error::Resample(e.to_string()))?;

    let expected = samples.len() * target_rate as usize / from_rate as usize;
    let mut out = Vec::with_capacity(expected + CHUNK);
    let mut scratch = vec![0.0f32; CHUNK];

    for chunk in samples.chunks(CHUNK) {
        // The FFT resampler insists on exactly CHUNK frames, so the tail gets zero-padded.
        scratch[..chunk.len()].copy_from_slice(chunk);
        scratch[chunk.len()..].fill(0.0);

        let resampled = resampler
            .process(&[scratch.as_slice()], None)
            .map_err(|e| Mt3Error::Resample(e.to_string()))?;
        out.extend_from_slice(&resampled[0]);
    }

    out.truncate(expected.max(1));
    Ok(out)
}

/// Splits audio into the fixed-length segments the encoder was exported with, zero-padding the
/// last one. Returns an empty vec for empty input rather than one segment of silence.
pub fn segments(samples: &[f32], segment_samples: usize) -> Vec<Vec<f32>> {
    if samples.is_empty() || segment_samples == 0 {
        return Vec::new();
    }

    samples
        .chunks(segment_samples)
        .map(|chunk| {
            let mut segment = vec![0.0f32; segment_samples];
            segment[..chunk.len()].copy_from_slice(chunk);
            segment
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn stereo_averages_to_mono() {
        let stereo = [1.0, 0.0, 0.5, 0.5, -1.0, 1.0];
        let mono = to_mono(&stereo, 2);

        assert_eq!(&*mono, &[0.5, 0.5, 0.0]);
    }

    #[test]
    fn mono_input_is_not_copied() {
        let mono = [0.1, 0.2, 0.3];

        assert!(matches!(to_mono(&mono, 1), std::borrow::Cow::Borrowed(_)));
    }

    #[test]
    fn same_rate_resampling_is_a_passthrough() {
        let samples = vec![0.25; 100];

        assert_eq!(resample(&samples, 16000, 16000).unwrap(), samples);
    }

    #[test]
    fn downsampling_lands_within_a_chunk_of_the_expected_length() {
        let samples = vec![0.0f32; 44100];
        let out = resample(&samples, 44100, 16000).unwrap();

        assert!((out.len() as i64 - 16000).abs() < 4096);
    }

    #[test]
    fn the_last_segment_is_zero_padded() {
        let samples: Vec<f32> = (0..10).map(|i| i as f32).collect();
        let segs = segments(&samples, 4);

        assert_eq!(segs.len(), 3);
        assert_eq!(segs[2], vec![8.0, 9.0, 0.0, 0.0]);
    }

    #[test]
    fn empty_audio_yields_no_segments() {
        assert!(segments(&[], 1024).is_empty());
    }
}
