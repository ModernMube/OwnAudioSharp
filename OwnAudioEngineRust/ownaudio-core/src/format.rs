/// Converts a buffer of `i16` samples to normalised `f32` in the range `[-1.0, 1.0]`.
///
/// # Panics
/// Panics if `input.len() != out.len()`.
pub fn i16_to_f32(input: &[i16], out: &mut [f32]) {
    assert_eq!(input.len(), out.len(), "i16_to_f32: length mismatch");
    for (o, &s) in out.iter_mut().zip(input) {
        *o = s as f32 / i16::MAX as f32;
    }
}

/// Converts a buffer of `u16` samples to normalised `f32` in the range `[-1.0, 1.0]`.
///
/// `u16` mid-point (32768) maps to 0.0.
///
/// # Panics
/// Panics if `input.len() != out.len()`.
pub fn u16_to_f32(input: &[u16], out: &mut [f32]) {
    assert_eq!(input.len(), out.len(), "u16_to_f32: length mismatch");
    for (o, &s) in out.iter_mut().zip(input) {
        *o = s as f32 / u16::MAX as f32 * 2.0 - 1.0;
    }
}

/// Converts a buffer of `i32` samples to normalised `f32` in the range `[-1.0, 1.0]`.
///
/// The division runs in `f64` because `i32` exceeds the 24-bit `f32` mantissa;
/// the result is rounded to `f32` only at the end.
///
/// # Panics
/// Panics if `input.len() != out.len()`.
pub fn i32_to_f32(input: &[i32], out: &mut [f32]) {
    assert_eq!(input.len(), out.len(), "i32_to_f32: length mismatch");
    for (o, &s) in out.iter_mut().zip(input) {
        *o = (s as f64 / i32::MAX as f64) as f32;
    }
}

/// Converts normalised `f32` samples in the range `[-1.0, 1.0]` to `i32`.
///
/// Values outside `[-1.0, 1.0]` are clamped to `[i32::MIN, i32::MAX]`.
///
/// # Panics
/// Panics if `input.len() != out.len()`.
pub fn f32_to_i32(input: &[f32], out: &mut [i32]) {
    assert_eq!(input.len(), out.len(), "f32_to_i32: length mismatch");
    // Scale by 2^31 in f64 (i32 exceeds the f32 mantissa) so that -1.0 maps to
    // i32::MIN and 1.0 maps to 2^31, which is clamped to i32::MAX.
    for (o, &s) in out.iter_mut().zip(input) {
        *o = (s as f64 * 2_147_483_648.0).clamp(i32::MIN as f64, i32::MAX as f64) as i32;
    }
}

/// Converts normalised `f32` samples in the range `[-1.0, 1.0]` to `i16`.
///
/// Values outside `[-1.0, 1.0]` are clamped to `[i16::MIN, i16::MAX]`.
///
/// # Panics
/// Panics if `input.len() != out.len()`.
pub fn f32_to_i16(input: &[f32], out: &mut [i16]) {
    assert_eq!(input.len(), out.len(), "f32_to_i16: length mismatch");
    // Scale by 32768 so that -1.0 maps to i16::MIN (-32768) and 1.0 maps to
    // 32768, which is clamped to i16::MAX (32767).
    for (o, &s) in out.iter_mut().zip(input) {
        *o = (s * 32768.0).clamp(i16::MIN as f32, i16::MAX as f32) as i16;
    }
}

/// Converts normalised `f32` samples in the range `[-1.0, 1.0]` to `u16`.
///
/// Values outside `[-1.0, 1.0]` are clamped to `[u16::MIN, u16::MAX]`.
///
/// # Panics
/// Panics if `input.len() != out.len()`.
pub fn f32_to_u16(input: &[f32], out: &mut [u16]) {
    assert_eq!(input.len(), out.len(), "f32_to_u16: length mismatch");
    for (o, &s) in out.iter_mut().zip(input) {
        *o = ((s + 1.0) * 0.5 * u16::MAX as f32).clamp(0.0, u16::MAX as f32) as u16;
    }
}

/// Remaps an interleaved `f32` buffer from `src_channels` to `dst_channels`,
/// replacing the contents of `out` with the result.
///
/// This is the capture-side counterpart of the decoder's channel conversion: it
/// lets a device that only supports a different channel count than requested (a
/// mono-only microphone asked for stereo, say) still feed a stream of the
/// requested width.
///
/// Real-time safe once `out` is pre-sized to the expected output length: the
/// vector only allocates when it has to grow, so sizing it ahead of the audio
/// thread avoids allocation in the callback. When the channel counts already
/// match, the input is copied verbatim.
///
/// - Mono to N: the single channel is duplicated across every output channel.
/// - N to mono: all source channels are averaged.
/// - Otherwise: shared channels are copied and any extra output channels are silenced.
pub fn remap_channels_into(
    src: &[f32],
    src_channels: usize,
    dst_channels: usize,
    out: &mut Vec<f32>,
) {
    out.clear();

    if src_channels == 0 || dst_channels == 0 {
        return;
    }

    let frames = src.len() / src_channels;
    out.reserve(frames * dst_channels);

    if src_channels == dst_channels {
        out.extend_from_slice(&src[..frames * src_channels]);
    } else if src_channels == 1 {
        for &s in &src[..frames] {
            for _ in 0..dst_channels {
                out.push(s);
            }
        }
    } else if dst_channels == 1 {
        let inv = 1.0 / src_channels as f32;
        for f in 0..frames {
            let base = f * src_channels;
            let mut acc = 0.0f32;
            for c in 0..src_channels {
                acc += src[base + c];
            }
            out.push(acc * inv);
        }
    } else {
        let common = src_channels.min(dst_channels);
        for f in 0..frames {
            let base = f * src_channels;
            for c in 0..dst_channels {
                out.push(if c < common { src[base + c] } else { 0.0 });
            }
        }
    }
}

/// Converts planar audio (one slice per channel) to interleaved layout in `out`.
///
/// `out.len()` must equal `sum of channel slice lengths * number_of_channels`.
/// All channel slices must have the same length (number of frames).
///
/// # Panics
/// Panics if channel slices have unequal lengths or `out` has the wrong size.
pub fn interleave(channels: &[&[f32]], out: &mut [f32]) {
    if channels.is_empty() {
        return;
    }
    let frames = channels[0].len();
    assert!(
        channels.iter().all(|c| c.len() == frames),
        "interleave: all channel slices must have equal length"
    );
    assert_eq!(
        out.len(),
        frames * channels.len(),
        "interleave: out length must equal frames * channel_count"
    );
    let n_ch = channels.len();
    for (frame_idx, frame) in out.chunks_mut(n_ch).enumerate() {
        for (ch_idx, ch) in channels.iter().enumerate() {
            frame[ch_idx] = ch[frame_idx];
        }
    }
}

/// Converts interleaved audio to planar layout.
///
/// `out` must have exactly `channels` elements, each pre-allocated to hold
/// `interleaved.len() / channels` frames.
///
/// # Panics
/// Panics if `interleaved.len()` is not divisible by `channels`, or `out`
/// has a wrong length or any element has the wrong length.
pub fn deinterleave(interleaved: &[f32], channels: usize, out: &mut [Vec<f32>]) {
    assert!(channels > 0, "deinterleave: channels must be > 0");
    assert_eq!(
        interleaved.len() % channels,
        0,
        "deinterleave: interleaved length must be divisible by channels"
    );
    let frames = interleaved.len() / channels;
    assert_eq!(
        out.len(),
        channels,
        "deinterleave: out must have exactly `channels` elements"
    );
    for ch_vec in out.iter() {
        assert_eq!(
            ch_vec.len(),
            frames,
            "deinterleave: each out element must have length == frames"
        );
    }
    for (frame_idx, frame) in interleaved.chunks(channels).enumerate() {
        for (ch_idx, &sample) in frame.iter().enumerate() {
            out[ch_idx][frame_idx] = sample;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn i16_to_f32_round_trip() {
        let input: Vec<i16> = vec![0, i16::MAX, i16::MIN, 16384];
        let mut out = vec![0.0f32; input.len()];
        i16_to_f32(&input, &mut out);
        assert!((out[0] - 0.0).abs() < 1e-6);
        assert!((out[1] - 1.0).abs() < 1e-4);
        assert!(out[2] < -0.999);
        assert!(out[3] > 0.4 && out[3] < 0.6);
    }

    #[test]
    fn u16_to_f32_midpoint_is_zero() {
        let input = vec![0u16, 32768, u16::MAX];
        let mut out = vec![0.0f32; 3];
        u16_to_f32(&input, &mut out);
        assert!(out[0] < -0.999);
        assert!((out[1]).abs() < 0.01);
        assert!(out[2] > 0.999);
    }

    #[test]
    fn f32_to_i16_clamps() {
        let input = vec![0.0f32, 1.0, -1.0, 2.0, -2.0];
        let mut out = vec![0i16; 5];
        f32_to_i16(&input, &mut out);
        assert_eq!(out[0], 0);
        assert_eq!(out[1], i16::MAX);
        assert_eq!(out[2], i16::MIN);
        assert_eq!(out[3], i16::MAX);
        assert_eq!(out[4], i16::MIN);
    }

    #[test]
    fn i32_to_f32_round_trip() {
        let input: Vec<i32> = vec![0, i32::MAX, i32::MIN, 1 << 30];
        let mut out = vec![0.0f32; input.len()];
        i32_to_f32(&input, &mut out);
        assert!((out[0] - 0.0).abs() < 1e-9);
        assert!((out[1] - 1.0).abs() < 1e-6);
        assert!(out[2] < -0.999);
        assert!(out[3] > 0.4 && out[3] < 0.6);
    }

    #[test]
    fn f32_to_i32_clamps() {
        let input = vec![0.0f32, 1.0, -1.0, 2.0, -2.0];
        let mut out = vec![0i32; 5];
        f32_to_i32(&input, &mut out);
        assert_eq!(out[0], 0);
        assert_eq!(out[1], i32::MAX);
        assert_eq!(out[2], i32::MIN);
        assert_eq!(out[3], i32::MAX);
        assert_eq!(out[4], i32::MIN);
    }

    #[test]
    fn f32_to_u16_clamps() {
        let input = vec![0.0f32, 1.0, -1.0, 2.0, -2.0];
        let mut out = vec![0u16; 5];
        f32_to_u16(&input, &mut out);
        assert_eq!(out[2], 0);
        assert_eq!(out[1], u16::MAX);
        assert_eq!(out[3], u16::MAX);
        assert_eq!(out[4], 0);
    }

    #[test]
    fn interleave_deinterleave_roundtrip() {
        let ch0 = vec![1.0f32, 2.0, 3.0];
        let ch1 = vec![4.0f32, 5.0, 6.0];
        let mut interleaved = vec![0.0f32; 6];
        interleave(&[&ch0, &ch1], &mut interleaved);
        assert_eq!(interleaved, vec![1.0, 4.0, 2.0, 5.0, 3.0, 6.0]);

        let mut planar = vec![vec![0.0f32; 3], vec![0.0f32; 3]];
        deinterleave(&interleaved, 2, &mut planar);
        assert_eq!(planar[0], ch0);
        assert_eq!(planar[1], ch1);
    }

    #[test]
    fn remap_channels_mono_to_stereo_duplicates() {
        let mono = vec![1.0f32, 2.0, 3.0];
        let mut out = Vec::new();
        remap_channels_into(&mono, 1, 2, &mut out);
        assert_eq!(out, vec![1.0, 1.0, 2.0, 2.0, 3.0, 3.0]);
    }

    #[test]
    fn remap_channels_stereo_to_mono_averages() {
        let stereo = vec![1.0f32, 3.0, -2.0, 2.0];
        let mut out = Vec::new();
        remap_channels_into(&stereo, 2, 1, &mut out);
        assert_eq!(out, vec![2.0, 0.0]);
    }

    #[test]
    fn remap_channels_equal_counts_copies_verbatim() {
        let stereo = vec![1.0f32, 2.0, 3.0, 4.0];
        let mut out = Vec::new();
        remap_channels_into(&stereo, 2, 2, &mut out);
        assert_eq!(out, stereo);
    }

    #[test]
    fn remap_channels_widen_pads_extra_with_silence() {
        let stereo = vec![1.0f32, 2.0];
        let mut out = Vec::new();
        remap_channels_into(&stereo, 2, 4, &mut out);
        assert_eq!(out, vec![1.0, 2.0, 0.0, 0.0]);
    }

    #[test]
    fn remap_channels_reuses_buffer_without_leftovers() {
        let mut out = vec![9.0f32; 16];
        remap_channels_into(&[1.0f32, 2.0], 1, 2, &mut out);
        assert_eq!(out, vec![1.0, 1.0, 2.0, 2.0]);
    }
}
