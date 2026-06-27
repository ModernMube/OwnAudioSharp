//! Real-time allocation-freedom test.
//!
//! A custom counting [`GlobalAlloc`] tallies every heap allocation.  After the
//! pipeline has warmed up (capacities stabilised), a long steady-state run of
//! `put_samples` + `receive_samples` with a fixed block size must perform
//! **zero** heap allocations — the acceptance criterion for the hot path.

use std::alloc::{GlobalAlloc, Layout, System};
use std::sync::atomic::{AtomicUsize, Ordering};

use ownaudio_soundtouch::SoundTouchProcessor;

static ALLOCS: AtomicUsize = AtomicUsize::new(0);
static COUNTING: AtomicUsize = AtomicUsize::new(0);

struct CountingAlloc;

unsafe impl GlobalAlloc for CountingAlloc {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        if COUNTING.load(Ordering::Relaxed) != 0 {
            ALLOCS.fetch_add(1, Ordering::Relaxed);
        }
        System.alloc(layout)
    }
    unsafe fn dealloc(&self, ptr: *mut u8, layout: Layout) {
        System.dealloc(ptr, layout);
    }
    unsafe fn realloc(&self, ptr: *mut u8, layout: Layout, new_size: usize) -> *mut u8 {
        if COUNTING.load(Ordering::Relaxed) != 0 {
            ALLOCS.fetch_add(1, Ordering::Relaxed);
        }
        System.realloc(ptr, layout, new_size)
    }
}

#[global_allocator]
static A: CountingAlloc = CountingAlloc;

#[test]
fn hot_path_is_allocation_free_in_steady_state() {
    let channels = 2usize;
    let block = 1024usize;

    let mut st = SoundTouchProcessor::new();
    st.set_sample_rate(44100).unwrap();
    st.set_channels(channels).unwrap();
    st.set_tempo(1.25);
    st.set_pitch_semitones(2.0);

    let input = vec![0.25f32; block * channels];
    let mut out = vec![0.0f32; block * 4 * channels];

    // Warm-up: let every internal FIFO grow to its steady-state ceiling.
    for _ in 0..200 {
        st.put_samples(&input, block).unwrap();
        while st.receive_samples(&mut out, block * 4) != 0 {}
    }

    // Measure: no allocation may happen now.
    COUNTING.store(1, Ordering::Relaxed);
    let before = ALLOCS.load(Ordering::Relaxed);
    for _ in 0..2000 {
        st.put_samples(&input, block).unwrap();
        while st.receive_samples(&mut out, block * 4) != 0 {}
    }
    let after = ALLOCS.load(Ordering::Relaxed);
    COUNTING.store(0, Ordering::Relaxed);

    assert_eq!(
        after - before,
        0,
        "hot path allocated {} time(s) in steady state",
        after - before
    );
}

#[test]
fn unity_tempo_preserves_energy() {
    // At unity tempo/pitch/rate the WSOLA reconstruction should preserve signal
    // energy: the output RMS must be close to the input RMS (a self-consistent
    // quality check standing in for the bit-inexact C# reference comparison).
    let mut st = SoundTouchProcessor::new();
    st.set_sample_rate(44100).unwrap();
    st.set_channels(1).unwrap();

    let n = 44100usize;
    let mut phase = 0.0f32;
    let block = 1024;
    let mut input = vec![0.0f32; block];
    let mut out = vec![0.0f32; block * 4];

    let mut out_sumsq = 0.0f64;
    let mut out_count = 0usize;
    let in_amp = 0.5f32;

    let mut produced = 0;
    while produced < n {
        for s in input.iter_mut() {
            *s = phase.sin() * in_amp;
            phase += 0.1;
        }
        st.put_samples(&input, block).unwrap();
        produced += block;
        loop {
            let got = st.receive_samples(&mut out, block * 4);
            if got == 0 {
                break;
            }
            for &v in &out[..got] {
                out_sumsq += (v as f64) * (v as f64);
                out_count += 1;
            }
        }
    }
    st.flush();
    loop {
        let got = st.receive_samples(&mut out, block * 4);
        if got == 0 {
            break;
        }
        for &v in &out[..got] {
            out_sumsq += (v as f64) * (v as f64);
            out_count += 1;
        }
    }

    let in_rms = (in_amp as f64) / 2.0f64.sqrt(); // RMS of a sine
    let out_rms = (out_sumsq / out_count as f64).sqrt();
    let ratio_db = 20.0 * (out_rms / in_rms).log10();
    assert!(
        ratio_db.abs() < 1.5,
        "output RMS deviates {ratio_db:.2} dB from input (out={out_rms:.4}, in={in_rms:.4})"
    );
}
