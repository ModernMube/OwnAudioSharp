//! Real-time panic boundary for audio callbacks.
//!
//! cpal invokes the data callback from a high-priority OS thread that lives
//! inside a C stack frame.  If a Rust panic were allowed to unwind out of the
//! callback and across that C frame, the behaviour is undefined.  The helpers
//! in this module run the user-supplied fill logic inside
//! [`std::panic::catch_unwind`] so that a panic can never reach the cpal
//! boundary: for output it leaves the device buffer filled with silence, for
//! input it is simply swallowed.

/// Runs an output fill closure under a panic guard.
///
/// `fill` is given the device buffer and is expected to populate it.  If `fill`
/// panics, the panic is caught (never unwinding across the cpal/C boundary) and
/// `data` is overwritten with `silence` so the device receives a clean silent
/// buffer instead of partially-written garbage.
///
/// `silence` is the per-format silent sample value: `0.0` for `f32`, `0` for
/// `i16`, and `32768` (the mid-point) for `u16`.
#[inline]
pub(crate) fn guard_output<T: Copy>(data: &mut [T], silence: T, fill: impl FnOnce(&mut [T])) {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| fill(&mut *data)));
    if result.is_err() {
        data.fill(silence);
    }
}

/// Runs an input capture closure under a panic guard.
///
/// A panic in `run` is caught so it cannot unwind across the cpal/C boundary.
/// There is no output buffer to sanitise, so the panic is simply swallowed.
#[inline]
pub(crate) fn guard_input(run: impl FnOnce()) {
    let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(run));
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn guard_output_runs_fill_when_no_panic() {
        let mut buf = [0.0f32; 4];
        guard_output(&mut buf, 0.0, |b| {
            for (i, s) in b.iter_mut().enumerate() {
                *s = i as f32;
            }
        });
        assert_eq!(buf, [0.0, 1.0, 2.0, 3.0]);
    }

    #[test]
    fn guard_output_silences_buffer_on_panic_f32() {
        let mut buf = [7.0f32; 4];
        guard_output(&mut buf, 0.0, |b| {
            b[0] = 1.0;
            panic!("callback blew up");
        });
        assert_eq!(buf, [0.0, 0.0, 0.0, 0.0]);
    }

    #[test]
    fn guard_output_uses_format_silence_on_panic_u16() {
        let mut buf = [0u16; 3];
        guard_output(&mut buf, 32768u16, |_| panic!("boom"));
        assert_eq!(buf, [32768, 32768, 32768]);
    }

    #[test]
    fn guard_output_uses_zero_silence_on_panic_i16() {
        let mut buf = [123i16; 3];
        guard_output(&mut buf, 0i16, |_| panic!("boom"));
        assert_eq!(buf, [0, 0, 0]);
    }

    #[test]
    fn guard_input_swallows_panic() {
        let mut ran = false;
        guard_input(|| {
            ran = true;
            panic!("input callback blew up");
        });
        // We return normally (no unwind) and the closure did start executing.
        assert!(ran);
    }

    #[test]
    fn guard_input_runs_when_no_panic() {
        let mut counter = 0;
        guard_input(|| counter += 1);
        assert_eq!(counter, 1);
    }
}
