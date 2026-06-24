//! Subnormal (denormal) float protection for the real-time path.
//!
//! Feedback effects that decay toward silence — delay lines, reverb comb
//! filters, IIR EQ states — let their state variables drift into the subnormal
//! range (magnitude below [`f32::MIN_POSITIVE`], i.e. `~1.18e-38`).  On x86_64
//! and several ARM cores arithmetic on subnormals triggers a microcode assist
//! that can cost 10–100× a normal operation, producing an unexplained CPU spike
//! (and audible glitch) precisely as a track fades out.
//!
//! [`flush`] zeroes such values on the way back into a feedback state, keeping
//! the recurrence in the normal range.  It is branch-cheap, allocation-free and
//! panic-free, suitable for per-sample use on the audio thread.
//!
//! A process-wide alternative — enabling the CPU's Flush-to-Zero / Denormals-Are-Zero
//! modes (MXCSR on x86_64, FPCR on ARM) once per audio-callback thread — belongs
//! to the engine layer when the mixer is wired into the cpal callback; this
//! per-state helper is the portable, testable complement applied inside the
//! stateful effects themselves.

/// Flushes a subnormal value to zero, leaving normal values untouched.
///
/// Any finite value whose magnitude is below the smallest normal float
/// ([`f32::MIN_POSITIVE`]) — every subnormal, plus signed zeros — is mapped to
/// `+0.0`; everything else (including infinities and NaN) passes through
/// unchanged.  Intended for the feedback state of recursive effects.
#[inline]
pub fn flush(x: f32) -> f32 {
    if x.abs() < f32::MIN_POSITIVE {
        0.0
    } else {
        x
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn subnormals_flush_to_zero() {
        // f32::MIN_POSITIVE is the smallest *normal* value; anything below it in
        // magnitude (but non-zero) is subnormal and must be flushed.
        let sub = f32::MIN_POSITIVE / 2.0;
        assert!(sub.is_sign_positive() && sub != 0.0);
        assert_eq!(flush(sub), 0.0);
        assert_eq!(flush(-sub), 0.0);
        assert_eq!(flush(f32::from_bits(1)), 0.0); // smallest positive subnormal
    }

    #[test]
    fn normal_values_pass_through() {
        for &v in &[1.0f32, -1.0, 0.5, -0.25, f32::MIN_POSITIVE, 1e-30] {
            assert_eq!(flush(v), v);
        }
    }

    #[test]
    fn zero_stays_zero() {
        assert_eq!(flush(0.0), 0.0);
        assert_eq!(flush(-0.0), 0.0);
    }

    #[test]
    fn repeated_flush_keeps_decay_out_of_subnormals() {
        // A geometric decay that would otherwise reach subnormals snaps to a
        // clean zero once it crosses the normal threshold and stays there.
        let mut state = 1.0f32;
        for _ in 0..10_000 {
            state = flush(state * 0.5);
        }
        assert_eq!(state, 0.0);
    }
}
