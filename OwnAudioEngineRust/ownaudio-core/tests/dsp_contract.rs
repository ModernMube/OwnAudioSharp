//! The native side of the shared DSP contract.
//!
//! `OwnAudioTests/dsp-contract.json` states, in dB and Hz, what each effect has to do to
//! a sine. This runner measures the native effects against it; the managed ones are
//! measured against the same file by `DspContractTests.cs`.
//!
//! Neither runner generates the spec — comparing the two implementations to each other
//! would pass happily when both are wrong the same way, which is precisely what happened
//! with the delay read index. Measuring both against a written expectation does not.

use ownaudio_core::effects::{Effect, Equalizer, Equalizer30, Limiter};
use serde_json::Value;

const BLOCK_FRAMES: usize = 512;

/// Loads the spec from the repo, so the C# and Rust runners cannot drift apart.
fn contract() -> Value {
    let path = concat!(
        env!("CARGO_MANIFEST_DIR"),
        "/../../OwnAudioTests/dsp-contract.json"
    );
    let text = std::fs::read_to_string(path)
        .unwrap_or_else(|e| panic!("cannot read the DSP contract at {path}: {e}"));

    serde_json::from_str(&text).expect("the DSP contract is not valid JSON")
}

/// Interleaved sine at the given dBFS level, same phase on every channel.
fn sine(freq: f64, level_db: f64, frames: usize, channels: usize, rate: f64) -> Vec<f32> {
    let amp = 10f64.powf(level_db / 20.0);
    let step = std::f64::consts::TAU * freq / rate;

    (0..frames)
        .flat_map(|f| {
            let s = (amp * (step * f as f64).sin()) as f32;
            std::iter::repeat_n(s, channels)
        })
        .collect()
}

fn to_db(linear: f64) -> f64 {
    20.0 * linear.abs().max(1e-12).log10()
}

fn peak_db(mono: &[f32]) -> f64 {
    to_db(mono.iter().fold(0.0f64, |m, &s| m.max(s.abs() as f64)))
}

fn rms_db(mono: &[f32]) -> f64 {
    let sum: f64 = mono.iter().map(|&s| (s as f64) * (s as f64)).sum();
    to_db((sum / mono.len() as f64).sqrt())
}

/// Amplitude of one frequency component via a Hann-windowed single-bin DFT — the same
/// measurement `SignalMeasure.AmplitudeAt` makes on the managed side.
fn amplitude_at(mono: &[f32], freq: f64, rate: f64) -> f64 {
    let n = mono.len();
    let w = std::f64::consts::TAU * freq / rate;

    let (re, im, win) = mono
        .iter()
        .enumerate()
        .fold((0.0, 0.0, 0.0), |(re, im, win), (i, &s)| {
            let hann = 0.5 - 0.5 * (std::f64::consts::TAU * i as f64 / (n - 1) as f64).cos();
            let x = s as f64 * hann;
            (
                re + x * (w * i as f64).cos(),
                im - x * (w * i as f64).sin(),
                win + hann,
            )
        });

    2.0 * (re * re + im * im).sqrt() / win
}

/// Pulls one channel out of an interleaved buffer, skipping the settle window.
fn steady(interleaved: &[f32], channels: usize, settle_frames: usize) -> Vec<f32> {
    interleaved
        .chunks(channels)
        .skip(settle_frames)
        .map(|frame| frame[0])
        .collect()
}

/// Runs the effect over the signal the way a host would, in blocks.
fn render(fx: &mut dyn Effect, input: &[f32], channels: u16) -> Vec<f32> {
    let mut out = input.to_vec();
    for block in out.chunks_mut(BLOCK_FRAMES * channels as usize) {
        fx.process(block, channels);
    }
    out
}

fn param(params: &Value, name: &str, fallback: f32) -> f32 {
    params
        .get(name)
        .and_then(Value::as_f64)
        .map(|v| v as f32)
        .unwrap_or(fallback)
}

/// Builds the native effect a contract entry describes.
fn build(entry: &Value, rate: f32) -> Box<dyn Effect> {
    let p = &entry["params"];

    match entry["effect"]
        .as_str()
        .expect("contract entry has no effect name")
    {
        "limiter" => {
            let mut fx = Limiter::new(rate);
            fx.set_param(
                ownaudio_core::effects::limiter::PARAM_THRESHOLD,
                param(p, "thresholdDb", -3.0),
            );
            fx.set_param(
                ownaudio_core::effects::limiter::PARAM_CEILING,
                param(p, "ceilingDb", -0.1),
            );
            fx.set_param(
                ownaudio_core::effects::limiter::PARAM_RELEASE,
                param(p, "releaseMs", 50.0),
            );
            fx.set_param(
                ownaudio_core::effects::limiter::PARAM_LOOKAHEAD,
                param(p, "lookaheadMs", 5.0),
            );
            Box::new(fx)
        }

        "compressor" => {
            use ownaudio_core::effects::compressor::*;
            let mut fx = Compressor::new(rate);
            fx.set_param(PARAM_THRESHOLD, param(p, "thresholdDb", -20.0));
            fx.set_param(PARAM_RATIO, param(p, "ratio", 4.0));
            fx.set_param(PARAM_KNEE, param(p, "kneeDb", 0.0));
            fx.set_param(PARAM_ATTACK, param(p, "attackMs", 5.0));
            fx.set_param(PARAM_RELEASE, param(p, "releaseMs", 200.0));
            fx.set_param(PARAM_MAKEUP, param(p, "makeupDb", 0.0));
            Box::new(fx)
        }

        "equalizer" => {
            let mut fx = Equalizer::new(rate);
            for band in 0..10u32 {
                if let Some(gain) = p.get(format!("band{band}Db")).and_then(Value::as_f64) {
                    fx.set_param(
                        ownaudio_core::effects::equalizer::PARAM_BAND_0 + band,
                        gain as f32,
                    );
                }
            }
            Box::new(fx)
        }

        "equalizer30" => {
            let mut fx = Equalizer30::new(rate);
            for band in 0..30u32 {
                if let Some(gain) = p.get(format!("band{band}Db")).and_then(Value::as_f64) {
                    fx.set_param(
                        ownaudio_core::effects::equalizer30::PARAM_BAND_0 + band,
                        gain as f32,
                    );
                }
            }
            Box::new(fx)
        }

        other => panic!("the contract names an effect this runner does not build: {other}"),
    }
}

#[test]
fn native_effects_meet_the_dsp_contract() {
    let spec = contract();

    let rate = spec["sampleRate"].as_f64().unwrap();
    let channels = spec["channels"].as_u64().unwrap() as usize;
    let settle_frames = (spec["settleSeconds"].as_f64().unwrap() * rate) as usize;
    let measure_frames = (spec["measureSeconds"].as_f64().unwrap() * rate) as usize;

    let mut failures = Vec::new();
    let mut checked = 0;

    for entry in spec["effects"].as_array().unwrap() {
        for case in entry["cases"].as_array().unwrap() {
            checked += 1;

            let freq = case["freqHz"].as_f64().unwrap();
            let input_db = case["inputDb"].as_f64().unwrap();
            let measure = case["measure"].as_str().unwrap();
            let expect = case["expect"].as_f64().unwrap();
            let tolerance = case["tolerance"].as_f64().unwrap();

            let input = sine(
                freq,
                input_db,
                settle_frames + measure_frames,
                channels,
                rate,
            );
            let mut fx = build(entry, rate as f32);
            let output = render(fx.as_mut(), &input, channels as u16);

            let wet = steady(&output, channels, settle_frames);
            let dry = steady(&input, channels, settle_frames);

            let actual = match measure {
                "peakDb" => peak_db(&wet),
                "rmsDb" => rms_db(&wet),
                "gainDb" => {
                    to_db(amplitude_at(&wet, freq, rate)) - to_db(amplitude_at(&dry, freq, rate))
                }
                other => panic!("unknown measure '{other}' in the contract"),
            };

            if (actual - expect).abs() > tolerance {
                let note = case["note"]
                    .as_str()
                    .map(|n| format!(" — {n}"))
                    .unwrap_or_default();
                failures.push(format!(
                    "{} {} @ {freq:.0}Hz {input_db:.0}dB: {measure} expected {expect:.2} ±{tolerance:.2}, measured {actual:.2}{note}",
                    entry["effect"].as_str().unwrap(),
                    entry["params"]
                ));
            }
        }
    }

    assert!(checked > 0, "the contract produced no cases to check");
    assert!(
        failures.is_empty(),
        "{} of {checked} contract cases failed:\n{}",
        failures.len(),
        failures.join("\n")
    );
}
