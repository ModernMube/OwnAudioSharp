//! End-to-end run against a real exported model.
//!
//! Skipped unless you point it at one, because the weights are hundreds of megabytes and are
//! deliberately not in the repository:
//!
//! ```text
//! OWNAUDIO_MT3_MODELS=~/Downloads/mt3-onnx \
//! OWNAUDIO_MT3_AUDIO=/tmp/funk.f32 \
//!   cargo test --test inference -- --ignored --nocapture
//! ```
//!
//! The audio file is raw little-endian f32 mono at the model's rate — no decoder in this crate,
//! and none needed, since the managed layer hands it samples anyway.

#![cfg(feature = "inference")]

use ownaudio_mt3_core::{ModelPaths, Mt3Transcriber, TranscribeOptions};

fn env(name: &str) -> Option<String> {
    std::env::var(name).ok().filter(|v| !v.is_empty())
}

fn read_f32(path: &str) -> Vec<f32> {
    let bytes = std::fs::read(path).expect("audio file should be readable");
    bytes
        .chunks_exact(4)
        .map(|b| f32::from_le_bytes([b[0], b[1], b[2], b[3]]))
        .collect()
}

#[test]
#[ignore = "needs an exported model; set OWNAUDIO_MT3_MODELS"]
fn transcribes_real_audio() {
    let (Some(models), Some(audio_path)) = (env("OWNAUDIO_MT3_MODELS"), env("OWNAUDIO_MT3_AUDIO"))
    else {
        panic!("set OWNAUDIO_MT3_MODELS and OWNAUDIO_MT3_AUDIO");
    };

    let paths = ModelPaths {
        encoder: format!("{models}/mt3_encoder.onnx"),
        decoder_init: format!("{models}/mt3_decoder_init.onnx"),
        decoder_step: format!("{models}/mt3_decoder_step.onnx"),
        vocab: format!("{models}/vocab.json"),
    };

    let mut transcriber = Mt3Transcriber::load(&paths, TranscribeOptions::default())
        .expect("the exported model should load");

    let samples = read_f32(&audio_path);
    let rate = transcriber.sample_rate();
    let duration = samples.len() as f64 / rate as f64;

    let started = std::time::Instant::now();
    let notes = transcriber
        .transcribe(&samples, rate, 1, |p| eprint!("\r{:.0}%", p * 100.0))
        .expect("transcription should succeed");
    let elapsed = started.elapsed();

    eprintln!(
        "\n{} notes from {duration:.2}s of audio in {:.1}s ({:.2}x realtime)",
        notes.len(),
        elapsed.as_secs_f64(),
        duration / elapsed.as_secs_f64()
    );
    for note in notes.iter().take(15) {
        eprintln!(
            "  {:.3}-{:.3}s  pitch {:3}  vel {:3}  program {:3}{}",
            note.start,
            note.end,
            note.pitch,
            note.velocity,
            note.program,
            if note.is_drum { "  [drum]" } else { "" }
        );
    }

    assert!(!notes.is_empty(), "real music should produce some notes");

    for note in &notes {
        assert!(note.end > note.start, "note {note:?} has no duration");
        assert!(note.pitch <= 127);
        assert!((1..=127).contains(&note.velocity));
        assert!(
            note.start >= 0.0 && note.end <= duration + 1.0,
            "note {note:?} falls outside the audio"
        );
    }

    assert!(
        notes.windows(2).all(|w| w[0].start <= w[1].start),
        "notes should come back sorted by onset"
    );
}
