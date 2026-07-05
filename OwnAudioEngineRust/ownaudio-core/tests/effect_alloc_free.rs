//! Formal proof for TODO item 2.3: the effect render hot path is allocation-free
//! in steady state.
//!
//! A counting global allocator (installed only for this test binary) tallies every
//! heap allocation while "armed". Each native DSP effect is constructed and warmed
//! up with the allocator disarmed (one-time growth — buffer pre-sizing, channel
//! layout, coefficient tables — is allowed), then `process` is called repeatedly
//! with the allocator armed and the allocation count is asserted to be exactly
//! zero. This mechanically guarantees no `process` call touches the allocator on
//! the real-time audio thread.
//!
//! `PitchShift` is intentionally excluded: it is backed by the vendored SoundTouch
//! WSOLA processor whose internal FIFO management is outside this crate's control;
//! its own scratch is pre-sized (see `effects/pitch_shift.rs`), but SoundTouch's
//! allocation behaviour is not something this test can meaningfully assert.

use std::alloc::{GlobalAlloc, Layout, System};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};

use ownaudio_core::effects::{
    AutoGain, Chorus, Compressor, Delay, Distortion, DynamicAmp, Effect, Enhancer, Equalizer,
    Equalizer30, Flanger, Gate, Limiter, Overdrive, Phaser, Reverb, Rotary, VstAudioBuffer,
    VstEffect,
};
use ownaudio_core::multitrack::{MultiTrackMixer, TrackSource, TrackState};

/// Counts allocations that happen while [`ARMED`] is set; otherwise a pass-through
/// to the system allocator.
struct CountingAllocator;

static ALLOC_COUNT: AtomicUsize = AtomicUsize::new(0);
static ARMED: AtomicBool = AtomicBool::new(false);

#[inline]
fn note_alloc() {
    if ARMED.load(Ordering::Relaxed) {
        ALLOC_COUNT.fetch_add(1, Ordering::Relaxed);
    }
}

unsafe impl GlobalAlloc for CountingAllocator {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        note_alloc();
        System.alloc(layout)
    }

    unsafe fn dealloc(&self, ptr: *mut u8, layout: Layout) {
        System.dealloc(ptr, layout);
    }

    unsafe fn alloc_zeroed(&self, layout: Layout) -> *mut u8 {
        note_alloc();
        System.alloc_zeroed(layout)
    }

    unsafe fn realloc(&self, ptr: *mut u8, layout: Layout, new_size: usize) -> *mut u8 {
        note_alloc();
        System.realloc(ptr, layout, new_size)
    }
}

#[global_allocator]
static ALLOCATOR: CountingAllocator = CountingAllocator;

/// Runs `f` with the allocator armed and returns the number of allocations it made.
fn count_allocs(f: impl FnOnce()) -> usize {
    ALLOC_COUNT.store(0, Ordering::Relaxed);
    ARMED.store(true, Ordering::SeqCst);
    f();
    ARMED.store(false, Ordering::SeqCst);
    ALLOC_COUNT.load(Ordering::Relaxed)
}

#[test]
fn effect_process_is_allocation_free_in_steady_state() {
    const SAMPLE_RATE: f32 = 48_000.0;
    const CHANNELS: u16 = 2;
    const FRAMES: usize = 512;
    const WARMUP_BLOCKS: usize = 8;
    const MEASURED_BLOCKS: usize = 64;

    // All native DSP effects. Constructed with the allocator disarmed, so the boxes
    // and any internal buffers allocate freely here.
    let effects: Vec<(&str, Box<dyn Effect>)> = vec![
        ("Reverb", Box::new(Reverb::new(SAMPLE_RATE))),
        ("Equalizer", Box::new(Equalizer::new(SAMPLE_RATE))),
        ("Equalizer30", Box::new(Equalizer30::new(SAMPLE_RATE))),
        ("Compressor", Box::new(Compressor::new(SAMPLE_RATE))),
        ("Limiter", Box::new(Limiter::new(SAMPLE_RATE))),
        ("Delay", Box::new(Delay::new(SAMPLE_RATE))),
        ("Chorus", Box::new(Chorus::new(SAMPLE_RATE))),
        ("Distortion", Box::new(Distortion::new(SAMPLE_RATE))),
        ("Overdrive", Box::new(Overdrive::new(SAMPLE_RATE))),
        ("Flanger", Box::new(Flanger::new(SAMPLE_RATE))),
        ("Phaser", Box::new(Phaser::new(SAMPLE_RATE))),
        ("Rotary", Box::new(Rotary::new(SAMPLE_RATE))),
        ("AutoGain", Box::new(AutoGain::new(SAMPLE_RATE))),
        ("Enhancer", Box::new(Enhancer::new(SAMPLE_RATE))),
        ("Gate", Box::new(Gate::new(SAMPLE_RATE))),
        ("DynamicAmp", Box::new(DynamicAmp::new(SAMPLE_RATE))),
    ];

    for (name, mut effect) in effects {
        // A loud-ish signal so envelopes, filters and gates are actually exercised
        // (some effects short-circuit on silence). Enable a non-trivial EQ band /
        // active state where relevant by driving several bands via set_param.
        let mut buffer = vec![0.0f32; FRAMES * CHANNELS as usize];
        for (i, s) in buffer.iter_mut().enumerate() {
            *s = 0.3 * ((i as f32) * 0.05).sin();
        }

        // Encourage effects that lazily size per-channel or per-band state to grow
        // now, while the allocator is disarmed.
        for band in 2..8u32 {
            effect.set_param(band, 3.0); // harmless for effects without that param id
        }

        // Warm up: the first blocks may perform one-time growth (channel state,
        // look-ahead lines, SoundTouch-independent scratch), all allowed here.
        for _ in 0..WARMUP_BLOCKS {
            effect.process(&mut buffer, CHANNELS);
        }

        // Measure: steady-state processing must not allocate at all.
        let allocs = count_allocs(|| {
            for _ in 0..MEASURED_BLOCKS {
                effect.process(&mut buffer, CHANNELS);
            }
        });

        assert_eq!(
            allocs, 0,
            "{name}::process allocated {allocs} time(s) across {MEASURED_BLOCKS} steady-state blocks"
        );
    }
}

/// A plugin stub that copies input planes to output planes without allocating,
/// standing in for a real `VST3Plugin_ProcessAudio` callback.
unsafe extern "C" fn vst_passthrough(
    _handle: *mut std::os::raw::c_void,
    buffer: *mut VstAudioBuffer,
) -> bool {
    let b = &*buffer;
    for c in 0..b.num_channels as usize {
        let inp = *b.inputs.add(c);
        let outp = *b.outputs.add(c);
        for f in 0..b.num_samples as usize {
            *outp.add(f) = *inp.add(f);
        }
    }
    true
}

#[test]
fn vst_effect_process_is_allocation_free_in_steady_state() {
    // The VST bridge deinterleaves into pre-allocated planar scratch, calls the
    // (non-allocating) plugin stub, and reinterleaves — none of which may touch
    // the allocator on the audio thread once the scratch is sized in `new`.
    const CHANNELS: u16 = 2;
    const FRAMES: usize = 512;

    let mut effect = VstEffect::new(std::ptr::null_mut(), vst_passthrough, CHANNELS, FRAMES, 0);
    // Exercise the dry/wet path too, which uses the pre-allocated dry buffer.
    effect.set_param(1, 0.5);

    let mut buffer = vec![0.0f32; FRAMES * CHANNELS as usize];
    for (i, s) in buffer.iter_mut().enumerate() {
        *s = 0.3 * ((i as f32) * 0.05).sin();
    }

    for _ in 0..8 {
        effect.process(&mut buffer, CHANNELS);
    }

    let allocs = count_allocs(|| {
        for _ in 0..64 {
            effect.process(&mut buffer, CHANNELS);
        }
    });

    assert_eq!(
        allocs, 0,
        "VstEffect::process allocated {allocs} time(s) across 64 steady-state blocks"
    );
}

/// A [`TrackSource`] that always fills the requested block with silence.
struct SilenceSource;

impl TrackSource for SilenceSource {
    fn read(&mut self, out: &mut [f32]) -> usize {
        out.fill(0.0);
        out.len()
    }
}

#[test]
fn mixer_mix_is_allocation_free_in_steady_state() {
    // The top-level real-time render call: draining the (empty) command queue,
    // clearing the output, and additively mixing every active track must not touch
    // the allocator once the per-track scratch has been sized on the first blocks.
    const SAMPLE_RATE: f32 = 48_000.0;
    const CHANNELS: u16 = 2;
    const FRAMES: usize = 512;
    const TRACKS: usize = 8;

    let mut mixer = MultiTrackMixer::new(SAMPLE_RATE, CHANNELS);
    for _ in 0..TRACKS {
        let (id, shared) = mixer.add_track();
        mixer.set_track_source(id, Some(Box::new(SilenceSource)));
        shared.set_state(TrackState::Playing);
    }

    let mut output = vec![0.0f32; FRAMES * CHANNELS as usize];

    // Warm up so any one-time per-track scratch growth happens with the allocator
    // disarmed.
    for _ in 0..8 {
        mixer.mix(&mut output);
    }

    let allocs = count_allocs(|| {
        for _ in 0..64 {
            mixer.mix(&mut output);
        }
    });

    assert_eq!(
        allocs, 0,
        "MultiTrackMixer::mix allocated {allocs} time(s) across 64 steady-state blocks"
    );
}
