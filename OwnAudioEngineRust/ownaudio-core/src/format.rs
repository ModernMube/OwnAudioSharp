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
}
