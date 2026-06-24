//! High-resolution MIDI timing clock.
//!
//! The clock runs a dedicated thread that fires a pulse callback at 24 pulses
//! per quarter note (PPQN), the rate defined by the MIDI specification for the
//! 0xF8 Timing Clock message. Between pulses the thread uses a hybrid sleep/spin
//! strategy: it sleeps for the bulk of the remaining interval (in short capped
//! chunks so tempo and stop requests stay responsive) and only busy-spins for
//! the final sub-millisecond window to keep jitter low. This keeps CPU usage
//! near zero instead of pinning a core. Sending the actual 0xF8 message (and the
//! Start/Stop/Continue transport messages) is left to the caller's pulse
//! callback so that the timing source stays decoupled from any particular output
//! port.

use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::Arc;
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

/// Time remaining before a pulse below which the thread busy-spins instead of
/// sleeping, trading a little CPU for sub-millisecond timing precision.
const SPIN_THRESHOLD_US: f64 = 1_000.0;

/// Upper bound on a single sleep, so the thread re-reads the tempo and the
/// running flag at least this often even at very slow tempos.
const MAX_SLEEP_US: u64 = 5_000;

/// Standard MIDI timing resolution: 24 pulses per quarter note.
const PULSES_PER_QUARTER_NOTE: f64 = 24.0;

/// Lowest tempo accepted, in beats per minute.
const MIN_BPM: f64 = 20.0;

/// Highest tempo accepted, in beats per minute.
const MAX_BPM: f64 = 300.0;

/// Callback invoked once per timing pulse from the clock thread.
pub type PulseCallback = dyn Fn() + Send + 'static;

/// State shared between the public handle and the running clock thread.
struct SharedState {
    /// Current tempo in beats per minute, stored as the bit pattern of an `f64`.
    bpm_bits: AtomicU64,

    /// Set to false to ask the clock thread to exit.
    running: AtomicBool,
}

/// A timing clock that drives a pulse callback at 24 PPQN.
pub struct MidiClock {
    state: Arc<SharedState>,
    thread: Option<JoinHandle<()>>,
}

impl MidiClock {
    /// Creates a stopped clock at the given tempo, clamped to [20, 300] BPM.
    pub fn new(bpm: f64) -> Self {
        let state = Arc::new(SharedState {
            bpm_bits: AtomicU64::new(clamp_bpm(bpm).to_bits()),
            running: AtomicBool::new(false),
        });
        Self {
            state,
            thread: None,
        }
    }

    /// Sets the tempo in beats per minute, clamped to [20, 300]. Takes effect on
    /// the next pulse interval calculation in the running thread.
    pub fn set_bpm(&self, bpm: f64) {
        self.state
            .bpm_bits
            .store(clamp_bpm(bpm).to_bits(), Ordering::Relaxed);
    }

    /// Returns the current tempo in beats per minute.
    pub fn bpm(&self) -> f64 {
        f64::from_bits(self.state.bpm_bits.load(Ordering::Relaxed))
    }

    /// Returns true while the clock thread is running.
    pub fn is_running(&self) -> bool {
        self.state.running.load(Ordering::Acquire)
    }

    /// Starts the clock thread, invoking `on_pulse` for each timing pulse.
    ///
    /// Calling this on an already-running clock is a no-op.
    pub fn start(&mut self, on_pulse: Box<PulseCallback>) {
        if self.is_running() {
            return;
        }

        self.state.running.store(true, Ordering::Release);
        let state = Arc::clone(&self.state);
        self.thread = Some(std::thread::spawn(move || run_clock(state, on_pulse)));
    }

    /// Stops the clock thread and waits for it to exit.
    pub fn stop(&mut self) {
        self.state.running.store(false, Ordering::Release);
        if let Some(handle) = self.thread.take() {
            let _ = handle.join();
        }
    }
}

impl Drop for MidiClock {
    fn drop(&mut self) {
        self.stop();
    }
}

/// Clamps a tempo value to the supported [20, 300] BPM range.
fn clamp_bpm(bpm: f64) -> f64 {
    bpm.clamp(MIN_BPM, MAX_BPM)
}

/// Returns the number of microseconds between consecutive pulses at `bpm`.
pub fn pulse_interval_us(bpm: f64) -> f64 {
    (60_000_000.0 / bpm) / PULSES_PER_QUARTER_NOTE
}

/// Clock thread body: fires `on_pulse` at the interval derived from the current
/// tempo, re-reading the tempo each iteration so changes take effect promptly.
fn run_clock(state: Arc<SharedState>, on_pulse: Box<PulseCallback>) {
    let start = Instant::now();
    let mut next_pulse_us: f64 = 0.0;

    while state.running.load(Ordering::Acquire) {
        let bpm = f64::from_bits(state.bpm_bits.load(Ordering::Relaxed));
        let interval_us = pulse_interval_us(bpm);

        let elapsed_us = start.elapsed().as_micros() as f64;
        let diff_us = next_pulse_us - elapsed_us;

        if diff_us <= 0.0 {
            on_pulse();
            next_pulse_us += interval_us;
        } else if diff_us > SPIN_THRESHOLD_US {
            let sleep_us = ((diff_us - SPIN_THRESHOLD_US) as u64).min(MAX_SLEEP_US);
            std::thread::sleep(Duration::from_micros(sleep_us));
        } else {
            std::hint::spin_loop();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn bpm_is_clamped_to_supported_range() {
        let clock = MidiClock::new(10.0);
        assert_eq!(clock.bpm(), MIN_BPM);
        clock.set_bpm(1000.0);
        assert_eq!(clock.bpm(), MAX_BPM);
    }

    #[test]
    fn pulse_interval_matches_24_ppqn() {
        // At 120 BPM a quarter note lasts 500 ms, so one of 24 pulses is ~20833 us.
        let interval = pulse_interval_us(120.0);
        assert!((interval - 20_833.33).abs() < 1.0);
    }

    #[test]
    fn start_then_stop_toggles_running_state() {
        use std::sync::atomic::AtomicU32;
        let counter = Arc::new(AtomicU32::new(0));
        let counter_clone = Arc::clone(&counter);

        let mut clock = MidiClock::new(120.0);
        clock.start(Box::new(move || {
            counter_clone.fetch_add(1, Ordering::Relaxed);
        }));
        assert!(clock.is_running());

        std::thread::sleep(std::time::Duration::from_millis(60));
        clock.stop();
        assert!(!clock.is_running());
        assert!(counter.load(Ordering::Relaxed) > 0);
    }

    #[test]
    fn hybrid_timing_keeps_pulse_cadence() {
        use std::sync::atomic::AtomicU32;

        // At 120 BPM the clock fires 48 pulses per second (24 PPQN x 2 beats).
        // Over ~300 ms that is ~14 pulses; allow a generous tolerance for
        // scheduler jitter while still proving the sleep/spin path keeps time.
        let counter = Arc::new(AtomicU32::new(0));
        let counter_clone = Arc::clone(&counter);

        let mut clock = MidiClock::new(120.0);
        clock.start(Box::new(move || {
            counter_clone.fetch_add(1, Ordering::Relaxed);
        }));

        std::thread::sleep(std::time::Duration::from_millis(300));
        clock.stop();

        let pulses = counter.load(Ordering::Relaxed);
        assert!(
            (8..=20).contains(&pulses),
            "expected ~14 pulses in 300 ms, got {pulses}"
        );
    }
}
