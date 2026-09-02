//! Per-effect throughput, one 512-frame stereo block each — what the mixer hands
//! them in production. Worth reading against the period: 512 frames at 48 kHz is
//! a 10.67 ms budget.
//!
//!   cargo bench -p ownaudio-core -- --save-baseline before
//!   cargo bench -p ownaudio-core -- --baseline before

use criterion::{criterion_group, criterion_main, BenchmarkId, Criterion, Throughput};
use ownaudio_core::effects::{
    AutoGain, Chorus, Compressor, Delay, Distortion, DynamicAmp, Effect, Enhancer, Equalizer,
    Equalizer30, Flanger, Gate, Limiter, Overdrive, OwnReverb, Phaser, PitchShift, Reverb, Rotary,
    SmartMaster,
};

const SAMPLE_RATE: f32 = 48_000.0;
const CHANNELS: u16 = 2;
const BLOCK_FRAMES: usize = 512;

/// One block of a -12 dBFS 1 kHz tone.
fn tone_block() -> Vec<f32> {
    let amp = 10f32.powf(-12.0 / 20.0);
    let step = std::f32::consts::TAU * 1000.0 / SAMPLE_RATE;

    (0..BLOCK_FRAMES)
        .flat_map(|f| std::iter::repeat_n(amp * (step * f as f32).sin(), CHANNELS as usize))
        .collect()
}

/// Every effect worth measuring. The EQs get a band pushed up on purpose - both
/// skip flat bands, so a default instance would benchmark the bypass path.
fn effects() -> Vec<(&'static str, Box<dyn Effect>)> {
    let mut eq = Equalizer::new(SAMPLE_RATE);
    eq.set_param(ownaudio_core::effects::equalizer::PARAM_BAND_5, 6.0);

    let mut eq30 = Equalizer30::new(SAMPLE_RATE);
    for band in 0..30u32 {
        eq30.set_param(
            ownaudio_core::effects::equalizer30::PARAM_BAND_0 + band,
            3.0,
        );
    }

    vec![
        ("autogain", Box::new(AutoGain::new(SAMPLE_RATE))),
        ("chorus", Box::new(Chorus::new(SAMPLE_RATE))),
        ("compressor", Box::new(Compressor::new(SAMPLE_RATE))),
        ("delay", Box::new(Delay::new(SAMPLE_RATE))),
        ("distortion", Box::new(Distortion::new(SAMPLE_RATE))),
        ("dynamic_amp", Box::new(DynamicAmp::new(SAMPLE_RATE))),
        ("enhancer", Box::new(Enhancer::new(SAMPLE_RATE))),
        ("equalizer_1band", Box::new(eq)),
        ("equalizer30_all_bands", Box::new(eq30)),
        ("flanger", Box::new(Flanger::new(SAMPLE_RATE))),
        ("gate", Box::new(Gate::new(SAMPLE_RATE))),
        ("limiter", Box::new(Limiter::new(SAMPLE_RATE))),
        ("overdrive", Box::new(Overdrive::new(SAMPLE_RATE))),
        ("phaser", Box::new(Phaser::new(SAMPLE_RATE))),
        ("pitch_shift", Box::new(PitchShift::new(SAMPLE_RATE))),
        ("ownreverb", Box::new(OwnReverb::new(SAMPLE_RATE))),
        ("reverb", Box::new(Reverb::new(SAMPLE_RATE))),
        ("rotary", Box::new(Rotary::new(SAMPLE_RATE))),
        ("smartmaster", Box::new(SmartMaster::new(SAMPLE_RATE))),
    ]
}

fn bench_effects(c: &mut Criterion) {
    let block = tone_block();

    let mut group = c.benchmark_group("effect_block_512");
    group.throughput(Throughput::Elements(BLOCK_FRAMES as u64));

    for (name, mut fx) in effects() {
        // Fill delay lines and settle envelopes before measuring
        let mut warm = block.clone();
        for _ in 0..64 {
            warm.copy_from_slice(&block);
            fx.process(&mut warm, CHANNELS);
        }

        let mut buf = block.clone();
        group.bench_function(BenchmarkId::from_parameter(name), |b| {
            b.iter(|| {
                buf.copy_from_slice(&block);
                fx.process(std::hint::black_box(&mut buf), CHANNELS);
            });
        });
    }

    group.finish();
}

/// A whole track chain, closer to what a mix actually runs.
fn bench_typical_chain(c: &mut Criterion) {
    let block = tone_block();

    let mut chain: Vec<Box<dyn Effect>> = vec![
        Box::new(Gate::new(SAMPLE_RATE)),
        Box::new(Compressor::new(SAMPLE_RATE)),
        Box::new(Equalizer::new(SAMPLE_RATE)),
        Box::new(Reverb::new(SAMPLE_RATE)),
        Box::new(Limiter::new(SAMPLE_RATE)),
    ];

    let mut warm = block.clone();
    for _ in 0..64 {
        warm.copy_from_slice(&block);
        for fx in chain.iter_mut() {
            fx.process(&mut warm, CHANNELS);
        }
    }

    let mut buf = block.clone();
    let mut group = c.benchmark_group("track_chain_512");
    group.throughput(Throughput::Elements(BLOCK_FRAMES as u64));
    group.bench_function("gate_comp_eq_reverb_limiter", |b| {
        b.iter(|| {
            buf.copy_from_slice(&block);
            for fx in chain.iter_mut() {
                fx.process(std::hint::black_box(&mut buf), CHANNELS);
            }
        });
    });
    group.finish();
}

criterion_group!(benches, bench_effects, bench_typical_chain);
criterion_main!(benches);
