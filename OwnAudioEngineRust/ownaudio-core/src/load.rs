//! DSP load accounting for the audio callback.
//!
//! Underruns say a block was already late; this says how close the rest came.
//! Load is `elapsed / budget`, where the budget is what the frame count buys at
//! the sample rate — 1.0 means the next block starts late.
//!
//! RT-safe: one `Instant::now()` pair (vDSO `clock_gettime`) and five relaxed
//! atomics per callback. No allocation, no lock.

use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Instant;

/// Peak load is kept in ppm so `fetch_max` has an integer to work on.
const PPM: u64 = 1_000_000;

/// Running load tallies, shared between the audio callback and the control side.
#[derive(Debug)]
pub struct LoadCounters {
    sample_rate: u32,
    block_count: AtomicU64,
    total_block_ns: AtomicU64,
    total_budget_ns: AtomicU64,
    peak_block_ns: AtomicU64,
    peak_load_ppm: AtomicU64,
}

impl LoadCounters {
    /// Starts a fresh set of counters for a stream running at `sample_rate`.
    pub fn new(sample_rate: u32) -> Self {
        Self {
            sample_rate: sample_rate.max(1),
            block_count: AtomicU64::new(0),
            total_block_ns: AtomicU64::new(0),
            total_budget_ns: AtomicU64::new(0),
            peak_block_ns: AtomicU64::new(0),
            peak_load_ppm: AtomicU64::new(0),
        }
    }

    /// Books one callback. Called from the audio thread, nowhere else.
    #[inline]
    pub fn record(&self, elapsed_ns: u64, frames: u64) {
        let budget_ns = frames * 1_000_000_000 / self.sample_rate as u64;
        let load_ppm = elapsed_ns
            .saturating_mul(PPM)
            .checked_div(budget_ns)
            .unwrap_or(0);

        self.block_count.fetch_add(1, Ordering::Relaxed);
        self.total_block_ns.fetch_add(elapsed_ns, Ordering::Relaxed);
        self.total_budget_ns.fetch_add(budget_ns, Ordering::Relaxed);
        self.peak_block_ns.fetch_max(elapsed_ns, Ordering::Relaxed);
        self.peak_load_ppm.fetch_max(load_ppm, Ordering::Relaxed);
    }

    /// Reads the tallies. Not atomic as a group, so a snapshot taken mid-callback
    /// can mix two blocks - fine for monitoring, and the alternative is a lock on
    /// the audio thread.
    pub fn snapshot(&self) -> LoadSnapshot {
        let blocks = self.block_count.load(Ordering::Relaxed);
        let total_ns = self.total_block_ns.load(Ordering::Relaxed);
        let budget_ns = self.total_budget_ns.load(Ordering::Relaxed);

        LoadSnapshot {
            block_count: blocks,
            peak_block_ns: self.peak_block_ns.load(Ordering::Relaxed),
            average_block_ns: total_ns.checked_div(blocks).unwrap_or(0),
            average_load: if budget_ns > 0 {
                total_ns as f32 / budget_ns as f32
            } else {
                0.0
            },
            peak_load: self.peak_load_ppm.load(Ordering::Relaxed) as f32 / PPM as f32,
        }
    }

    /// Clears the tallies. Device warm-up is never representative.
    pub fn reset(&self) {
        self.block_count.store(0, Ordering::Relaxed);
        self.total_block_ns.store(0, Ordering::Relaxed);
        self.total_budget_ns.store(0, Ordering::Relaxed);
        self.peak_block_ns.store(0, Ordering::Relaxed);
        self.peak_load_ppm.store(0, Ordering::Relaxed);
    }
}

/// What the counters looked like at one moment.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct LoadSnapshot {
    /// Callbacks seen since the last reset.
    pub block_count: u64,
    /// Longest single callback, in nanoseconds.
    pub peak_block_ns: u64,
    /// Mean callback duration, in nanoseconds.
    pub average_block_ns: u64,
    /// Mean share of the period spent in the callback, 1.0 = late.
    pub average_load: f32,
    /// Worst single block. This is what predicts dropouts, not the average.
    pub peak_load: f32,
}

/// Times a callback body and books it.
#[inline]
pub fn measured<T>(counters: &LoadCounters, frames: u64, body: impl FnOnce() -> T) -> T {
    let started = Instant::now();
    let out = body();
    counters.record(started.elapsed().as_nanos() as u64, frames);
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn load_is_elapsed_over_the_block_budget() {
        let c = LoadCounters::new(48_000);

        // 512 frames at 48 kHz is a 10.667 ms period; half of it is 50% load.
        c.record(5_333_333, 512);

        let s = c.snapshot();
        assert_eq!(s.block_count, 1);
        assert!(
            (s.average_load - 0.5).abs() < 0.01,
            "load was {}",
            s.average_load
        );
        assert!((s.peak_load - 0.5).abs() < 0.01);
        assert_eq!(s.peak_block_ns, 5_333_333);
    }

    #[test]
    fn peak_keeps_the_worst_block_not_the_last() {
        let c = LoadCounters::new(48_000);
        c.record(1_000_000, 512);
        c.record(9_000_000, 512);
        c.record(1_000_000, 512);

        let s = c.snapshot();
        assert_eq!(s.peak_block_ns, 9_000_000);
        assert_eq!(s.average_block_ns, 11_000_000 / 3);
        assert!(s.peak_load > s.average_load);
    }

    #[test]
    fn overrunning_the_period_reads_above_one() {
        let c = LoadCounters::new(48_000);
        // 512 frames want 10.667 ms; taking 16 ms is 150%.
        c.record(16_000_000, 512);

        assert!(c.snapshot().peak_load > 1.4);
    }

    #[test]
    fn reset_clears_everything() {
        let c = LoadCounters::new(48_000);
        c.record(5_000_000, 512);
        c.reset();

        assert_eq!(
            c.snapshot(),
            LoadSnapshot {
                block_count: 0,
                peak_block_ns: 0,
                average_block_ns: 0,
                average_load: 0.0,
                peak_load: 0.0,
            }
        );
    }

    #[test]
    fn a_zero_frame_callback_does_not_divide_by_zero() {
        let c = LoadCounters::new(48_000);
        c.record(1_000, 0);

        let s = c.snapshot();
        assert_eq!(s.block_count, 1);
        assert_eq!(s.peak_load, 0.0);
    }

    #[test]
    fn measured_books_the_body() {
        let c = LoadCounters::new(48_000);
        let out = measured(&c, 512, || 7);

        assert_eq!(out, 7);
        assert_eq!(c.snapshot().block_count, 1);
    }
}
