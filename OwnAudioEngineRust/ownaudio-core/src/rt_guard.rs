//! Real-time panic boundary for audio callbacks.
//!
//! cpal invokes the data callback from a high-priority OS thread that lives
//! inside a C stack frame.  If a Rust panic were allowed to unwind out of the
//! callback and across that C frame, the behaviour is undefined.  The helpers
//! in this module run the user-supplied fill logic inside
//! [`std::panic::catch_unwind`] so that a panic can never reach the cpal
//! boundary: for output it leaves the device buffer filled with silence, for
//! input it is simply swallowed.

/// Enables hardware flush-to-zero / denormals-are-zero on the current thread.
///
/// Subnormal (denormal) floats in recursive DSP state (reverb tails, IIR/EQ
/// states, delay feedback) trigger a 10–100× microcode assist on x86_64 and
/// some ARM cores.  Setting FTZ/DAZ makes the FPU flush such values to zero in
/// hardware at zero cost.  This is a per-thread CPU register state, so it must
/// run on the audio thread; it is applied at the top of every callback (a
/// couple of instructions, negligible) so it holds regardless of which thread
/// cpal dispatches the callback on.
///
/// The per-effect software `denormal::flush` calls are deliberately kept as a
/// portable safety net: they guarantee the same behaviour on architectures
/// where this is a no-op and keep the effects bit-identical to their reference.
#[inline]
pub(crate) fn enable_denormal_flush() {
    #[cfg(target_arch = "x86_64")]
    unsafe {
        use std::arch::asm;
        // MXCSR: FTZ = bit 15 (0x8000), DAZ = bit 6 (0x40).
        let mut mxcsr: u32 = 0;
        asm!("stmxcsr [{}]", in(reg) &mut mxcsr, options(nostack, preserves_flags));
        mxcsr |= 0x8040;
        asm!("ldmxcsr [{}]", in(reg) &mxcsr, options(nostack, preserves_flags, readonly));
    }
    #[cfg(target_arch = "aarch64")]
    unsafe {
        use std::arch::asm;
        // FPCR: FZ = bit 24 (flush-to-zero for the standard floating-point ops).
        let mut fpcr: u64;
        asm!("mrs {}, fpcr", out(reg) fpcr, options(nomem, nostack, preserves_flags));
        fpcr |= 1 << 24;
        asm!("msr fpcr, {}", in(reg) fpcr, options(nomem, nostack, preserves_flags));
    }
}

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
    enable_denormal_flush();
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

    /// After enabling FTZ/DAZ the FPU must treat a subnormal operand as zero.
    /// Run on a throwaway thread so the register change cannot leak into other
    /// tests sharing the cargo test thread-pool.
    #[test]
    #[cfg(any(target_arch = "x86_64", target_arch = "aarch64"))]
    fn enable_denormal_flush_zeros_subnormals() {
        let flushed = std::thread::spawn(|| {
            enable_denormal_flush();
            // Smallest positive subnormal f32 (~1.4e-45); black_box defeats
            // compile-time folding so the multiply runs on the FPU.
            let tiny = std::hint::black_box(f32::from_bits(1));
            std::hint::black_box(tiny) * std::hint::black_box(1.0f32)
        })
        .join()
        .unwrap();
        assert_eq!(flushed, 0.0, "subnormal operand was not flushed to zero");
    }
}
