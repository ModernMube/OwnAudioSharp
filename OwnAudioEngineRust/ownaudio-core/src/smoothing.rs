//! Per-sample parameter smoothing to remove zipper noise from abrupt changes.
//!
//! When a control thread changes a parameter (gain, an EQ band, …) the audio
//! thread would otherwise jump to the new value at the next sample, producing an
//! audible click / zipper noise.  [`SmoothedParam`] interpolates from the old
//! value toward the new one over a short time constant, one sample at a time, on
//! the real-time path with no allocation and no panic.

/// One-pole low-pass smoother for a single scalar parameter.
///
/// The control thread calls [`SmoothedParam::set_target`] with the new value;
/// the audio thread calls [`SmoothedParam::advance`] once per frame to obtain
/// the next smoothed value, which approaches the target exponentially with the
/// time constant fixed at construction.
///
/// The recurrence is `current += (target - current) * coeff`, where `coeff` is
/// derived from the time constant and sample rate.  A non-positive sample rate
/// or time constant collapses `coeff` to `1.0`, i.e. instantaneous (un-smoothed)
/// behaviour.
#[derive(Debug, Clone, Copy)]
pub struct SmoothedParam {
    /// The current, smoothed value emitted by [`SmoothedParam::advance`].
    current: f32,
    /// The destination the smoother ramps toward.
    target: f32,
    /// Per-sample one-pole coefficient in `(0.0, 1.0]`.
    coeff: f32,
}

impl SmoothedParam {
    /// Creates a smoother resting at `initial`, smoothing with a one-pole time
    /// constant of `time_ms` milliseconds at `sample_rate` Hz.
    ///
    /// A non-positive `sample_rate` or `time_ms` yields an instantaneous
    /// smoother (`coeff == 1.0`), so [`advance`](Self::advance) jumps straight to
    /// the target.
    pub fn new(initial: f32, sample_rate: f32, time_ms: f32) -> Self {
        Self {
            current: initial,
            target: initial,
            coeff: Self::coeff_for(sample_rate, time_ms),
        }
    }

    /// Computes the one-pole coefficient for the given time constant.
    fn coeff_for(sample_rate: f32, time_ms: f32) -> f32 {
        if sample_rate <= 0.0 || time_ms <= 0.0 {
            return 1.0;
        }
        let tau = time_ms * 0.001;
        let c = 1.0 - (-1.0 / (tau * sample_rate)).exp();
        c.clamp(f32::MIN_POSITIVE, 1.0)
    }

    /// Sets the value the smoother ramps toward.  Cheap; safe on any thread that
    /// owns the smoother (the smoother itself is single-owner, not shared).
    #[inline]
    pub fn set_target(&mut self, target: f32) {
        self.target = target;
    }

    /// Jumps both the current value and the target to `value` with no ramp.
    ///
    /// Used to seed the smoother to a known starting point (e.g. on reset) so
    /// the next block does not fade in from a stale value.
    #[inline]
    pub fn snap(&mut self, value: f32) {
        self.current = value;
        self.target = value;
    }

    /// Advances one frame and returns the new smoothed value.
    #[inline]
    pub fn advance(&mut self) -> f32 {
        self.current += (self.target - self.current) * self.coeff;
        self.current
    }

    /// Returns the current smoothed value without advancing.
    #[inline]
    pub fn current(&self) -> f32 {
        self.current
    }

    /// Returns the target the smoother is ramping toward.
    #[inline]
    pub fn target(&self) -> f32 {
        self.target
    }

    /// Returns `true` when the current value is within `eps` of the target.
    #[inline]
    pub fn is_settled(&self, eps: f32) -> bool {
        (self.target - self.current).abs() <= eps
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn starts_at_initial_value() {
        let s = SmoothedParam::new(0.5, 48_000.0, 5.0);
        assert_eq!(s.current(), 0.5);
        assert_eq!(s.target(), 0.5);
    }

    #[test]
    fn constant_target_holds_exactly() {
        // current == target means advance never drifts away from it.
        let mut s = SmoothedParam::new(1.0, 48_000.0, 5.0);
        for _ in 0..1000 {
            assert_eq!(s.advance(), 1.0);
        }
    }

    #[test]
    fn ramps_monotonically_toward_target() {
        let mut s = SmoothedParam::new(1.0, 48_000.0, 5.0);
        s.set_target(0.0);
        let mut prev = s.current();
        for _ in 0..100 {
            let v = s.advance();
            assert!(v <= prev, "value must not overshoot upward: {v} > {prev}");
            assert!(v >= 0.0, "value must not undershoot target");
            prev = v;
        }
        // The first step must already move (no dead first sample).
        assert!(prev < 1.0);
    }

    #[test]
    fn converges_to_target_within_a_few_time_constants() {
        let sample_rate = 48_000.0;
        let time_ms = 5.0;
        let mut s = SmoothedParam::new(0.0, sample_rate, time_ms);
        s.set_target(2.0);
        // Eight time constants leaves ~0.03 % residual error for a one-pole.
        let samples = (sample_rate * time_ms * 0.001 * 8.0) as usize;
        for _ in 0..samples {
            s.advance();
        }
        assert!(s.is_settled(1e-3), "current={} target=2.0", s.current());
    }

    #[test]
    fn snap_jumps_without_ramp() {
        let mut s = SmoothedParam::new(1.0, 48_000.0, 5.0);
        s.snap(0.25);
        assert_eq!(s.current(), 0.25);
        assert_eq!(s.advance(), 0.25);
    }

    #[test]
    fn non_positive_config_is_instantaneous() {
        let mut s = SmoothedParam::new(0.0, 0.0, 5.0);
        s.set_target(0.7);
        assert_eq!(s.advance(), 0.7);

        let mut s = SmoothedParam::new(0.0, 48_000.0, 0.0);
        s.set_target(0.7);
        assert_eq!(s.advance(), 0.7);
    }
}
