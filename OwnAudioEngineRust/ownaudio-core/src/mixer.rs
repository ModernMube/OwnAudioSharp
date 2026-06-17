/// Allocation-free multi-source audio mixer.
///
/// All internal working memory is allocated once in [`Mixer::new`]; the
/// hot-path methods [`mix`] and [`mix_with_gain`] perform no heap allocation
/// and acquire no locks.
pub struct Mixer {
    /// General-purpose scratch buffer, sized to `max_buffer_size` at construction.
    /// Available for callers that need intermediate storage without extra allocation.
    scratch: Vec<f32>,
}

impl Mixer {
    /// Creates a new mixer whose scratch buffer covers up to `max_buffer_size` samples.
    ///
    /// `max_buffer_size` should be at least as large as the biggest audio callback
    /// buffer that will ever be passed to [`mix`] or [`mix_with_gain`].
    pub fn new(max_buffer_size: usize) -> Self {
        Self {
            scratch: vec![0.0f32; max_buffer_size],
        }
    }

    /// Additively mixes all `sources` into `out`.
    ///
    /// `out` is first zeroed, then each source is summed into it sample-by-sample.
    /// Clipping is intentional: no normalisation or limiting is applied at this
    /// layer — that is a concern for a downstream compressor or limiter stage.
    ///
    /// # Panics
    /// Panics if any source slice has a different length from `out`.
    pub fn mix(&mut self, sources: &[&[f32]], out: &mut [f32]) {
        out.fill(0.0);
        for &source in sources {
            assert_eq!(
                source.len(),
                out.len(),
                "mix: source length {} != out length {}",
                source.len(),
                out.len()
            );
            for (o, &s) in out.iter_mut().zip(source) {
                *o += s;
            }
        }
    }

    /// Additively mixes `sources` into `out`, applying a per-source linear gain.
    ///
    /// Each element of `sources` is `(samples, gain)`.  A gain of `1.0` leaves
    /// the level unchanged; `0.0` silences the source.  Values above `1.0` are
    /// valid (may cause clipping).
    ///
    /// `out` is zeroed before mixing.
    ///
    /// # Panics
    /// Panics if any source slice has a different length from `out`.
    pub fn mix_with_gain(&mut self, sources: &[(&[f32], f32)], out: &mut [f32]) {
        out.fill(0.0);
        for &(source, gain) in sources {
            assert_eq!(
                source.len(),
                out.len(),
                "mix_with_gain: source length {} != out length {}",
                source.len(),
                out.len()
            );
            for (o, &s) in out.iter_mut().zip(source) {
                *o += s * gain;
            }
        }
    }

    /// Returns a mutable reference to the pre-allocated scratch buffer.
    ///
    /// Callers may use this for intermediate processing steps without allocating.
    /// The scratch length equals `max_buffer_size` passed to [`Mixer::new`].
    pub fn scratch_mut(&mut self) -> &mut [f32] {
        &mut self.scratch
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn mix_two_sources_additive() {
        let mut mixer = Mixer::new(4);
        let a = [1.0f32, 2.0, 3.0, 4.0];
        let b = [0.5f32, 0.5, 0.5, 0.5];
        let mut out = [0.0f32; 4];
        mixer.mix(&[&a, &b], &mut out);
        assert_eq!(out, [1.5, 2.5, 3.5, 4.5]);
    }

    #[test]
    fn mix_three_sources() {
        let mut mixer = Mixer::new(2);
        let a = [1.0f32, 0.0];
        let b = [0.0f32, 1.0];
        let c = [0.5f32, 0.5];
        let mut out = [0.0f32; 2];
        mixer.mix(&[&a, &b, &c], &mut out);
        assert!((out[0] - 1.5).abs() < 1e-6);
        assert!((out[1] - 1.5).abs() < 1e-6);
    }

    #[test]
    fn mix_zeroes_out_first() {
        let mut mixer = Mixer::new(2);
        let mut out = [99.0f32, 99.0];
        mixer.mix(&[], &mut out);
        assert_eq!(out, [0.0, 0.0]);
    }

    #[test]
    fn mix_with_gain_scales() {
        let mut mixer = Mixer::new(4);
        let src = [1.0f32, 1.0, 1.0, 1.0];
        let mut out = [0.0f32; 4];
        mixer.mix_with_gain(&[(&src, 0.5), (&src, 0.25)], &mut out);
        for &s in &out {
            assert!((s - 0.75).abs() < 1e-6);
        }
    }

    #[test]
    fn mix_allows_clipping() {
        let mut mixer = Mixer::new(1);
        let src = [1.0f32];
        let mut out = [0.0f32; 1];
        mixer.mix(&[&src, &src, &src], &mut out);
        assert!((out[0] - 3.0).abs() < 1e-6);
    }

    #[test]
    #[should_panic(expected = "mix: source length")]
    fn mix_panics_on_length_mismatch() {
        let mut mixer = Mixer::new(8);
        let a = [1.0f32; 4];
        let b = [1.0f32; 3];
        let mut out = [0.0f32; 4];
        mixer.mix(&[&a, &b], &mut out);
    }

    #[test]
    #[should_panic(expected = "mix_with_gain: source length")]
    fn mix_with_gain_panics_on_length_mismatch() {
        let mut mixer = Mixer::new(8);
        let a = [1.0f32; 4];
        let b = [1.0f32; 5];
        let mut out = [0.0f32; 4];
        mixer.mix_with_gain(&[(&a, 1.0), (&b, 1.0)], &mut out);
    }
}
