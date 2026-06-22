//! Unit tests for the streaming decoder (Symphonia backend + StreamingTrack).

use super::backend::symphonia_backend::SymphoniaBackend;
use super::open_streaming;
use super::test_support::{linear_ramp, mono_ramp, TempWav};
use super::AudioDecoderBackend;

const SR: u32 = 44_100;

/// Decodes an entire backend into a single interleaved buffer.
fn drain_backend(backend: &mut dyn AudioDecoderBackend) -> Vec<f32> {
    let mut all = Vec::new();
    let mut buf = vec![0.0f32; 4096];
    loop {
        let r = backend.read_frames(&mut buf).expect("read_frames");
        all.extend_from_slice(&buf[..r.samples_written]);
        if r.is_eof {
            break;
        }
    }
    all
}

#[test]
fn symphonia_wav_decode_matches_reference() {
    let frames = 5000;
    let ramp = mono_ramp(frames);
    let wav = TempWav::write(1, SR, &ramp);

    let mut backend = SymphoniaBackend::open(wav.path_str(), 0, 0).expect("open");
    let info = backend.stream_info();
    assert_eq!(info.channels, 1);
    assert_eq!(info.sample_rate, SR);

    let decoded = drain_backend(&mut backend);
    assert_eq!(decoded.len(), frames, "decoded sample count should match");

    for (i, &expected) in ramp.iter().enumerate() {
        let exp = expected as f32 / 32768.0;
        assert!(
            (decoded[i] - exp).abs() < 1e-3,
            "sample {i}: got {}, expected {exp}",
            decoded[i]
        );
    }
}

#[test]
fn stream_info_reports_unknown_duration_helpers() {
    let ramp = mono_ramp(2000);
    let wav = TempWav::write(1, SR, &ramp);
    let backend = SymphoniaBackend::open(wav.path_str(), 0, 0).expect("open");
    let info = backend.stream_info();
    assert!(!info.has_unknown_duration());
    // ~2000 frames / 44100 Hz ≈ 45 ms
    assert!(info.duration_ms >= 40 && info.duration_ms <= 50, "duration_ms={}", info.duration_ms);
}

#[test]
fn eof_detection() {
    let ramp = mono_ramp(1024);
    let wav = TempWav::write(1, SR, &ramp);
    let mut backend = SymphoniaBackend::open(wav.path_str(), 0, 0).expect("open");

    let mut buf = vec![0.0f32; 4096];
    let mut saw_eof = false;
    for _ in 0..16 {
        let r = backend.read_frames(&mut buf).expect("read");
        if r.is_eof {
            saw_eof = true;
            break;
        }
    }
    assert!(saw_eof, "EOF must be reported at end of stream");
}

#[test]
fn seek_is_sample_accurate() {
    let frames = 12_000;
    // value == frame index, so a decoded sample maps uniquely back to its frame.
    let ramp = linear_ramp(frames);
    let wav = TempWav::write(1, SR, &ramp);
    let mut backend = SymphoniaBackend::open(wav.path_str(), 0, 0).expect("open");

    let target = 4000u64;
    backend.seek(target).expect("seek");

    let mut buf = vec![0.0f32; 16];
    let r = backend.read_frames(&mut buf).expect("read after seek");
    assert!(r.samples_written > 0);

    // Recover the landed frame from the first decoded sample.
    let landed = (buf[0] * 32768.0).round() as i64;
    let delta = (landed - target as i64).abs();
    assert!(delta <= 2, "seek landed at frame {landed}, expected {target} (±2)");
}

#[test]
fn channel_upmix_mono_to_stereo() {
    let frames = 1000;
    let ramp = mono_ramp(frames);
    let wav = TempWav::write(1, SR, &ramp);

    let mut backend = SymphoniaBackend::open(wav.path_str(), 0, 2).expect("open");
    assert_eq!(backend.stream_info().channels, 2);

    let decoded = drain_backend(&mut backend);
    assert_eq!(decoded.len(), frames * 2, "stereo output is double the frames");
    // Left and right must be identical for a duplicated mono source.
    for f in 0..frames {
        assert!((decoded[f * 2] - decoded[f * 2 + 1]).abs() < 1e-6);
    }
}

#[test]
fn channel_downmix_stereo_to_mono() {
    let frames = 500;
    // Interleaved stereo: L = +x, R = -x → mono average ≈ 0.
    let mut inter = Vec::with_capacity(frames * 2);
    for i in 0..frames {
        let v = ((i % 100) as i16) * 100;
        inter.push(v);
        inter.push(-v);
    }
    let wav = TempWav::write(2, SR, &inter);

    let mut backend = SymphoniaBackend::open(wav.path_str(), 0, 1).expect("open");
    assert_eq!(backend.stream_info().channels, 1);

    let decoded = drain_backend(&mut backend);
    assert_eq!(decoded.len(), frames);
    for &s in &decoded {
        assert!(s.abs() < 1e-3, "downmix of +x/-x should cancel, got {s}");
    }
}

#[test]
fn resampling_changes_frame_count_by_ratio() {
    let frames = 8000;
    let ramp = mono_ramp(frames);
    let wav = TempWav::write(1, SR, &ramp);

    // 44100 → 48000 upsample.
    let mut backend = SymphoniaBackend::open(wav.path_str(), 48_000, 0).expect("open");
    assert_eq!(backend.stream_info().sample_rate, 48_000);

    let decoded = drain_backend(&mut backend);
    let ratio = decoded.len() as f64 / frames as f64;
    let expected = 48_000.0 / 44_100.0;
    assert!(
        (ratio - expected).abs() < 0.05,
        "resample ratio {ratio:.4} should be near {expected:.4}"
    );
    assert!(decoded.iter().all(|s| s.is_finite()));
}

#[test]
fn streaming_track_reads_full_stream() {
    let frames = 20_000;
    let ramp = mono_ramp(frames);
    let wav = TempWav::write(1, SR, &ramp);

    let mut track = open_streaming(wav.path_str(), 0, 0, SR as usize).expect("open streaming");
    assert_eq!(track.stream_info().channels, 1);

    let mut total = 0usize;
    let mut buf = vec![0.0f32; 1024];
    // Poll until EOF, sleeping briefly to let the prefetch thread fill the ring.
    for _ in 0..2000 {
        let n = track.read(&mut buf);
        total += n;
        if n == 0 {
            if track.is_eof() {
                break;
            }
            std::thread::sleep(std::time::Duration::from_millis(2));
        }
    }

    assert!(track.is_eof(), "track should reach EOF");
    assert_eq!(total, frames, "streamed sample count should equal source frames");
}

#[test]
fn streaming_track_seek_then_read() {
    let frames = 12_000;
    let ramp = linear_ramp(frames);
    let wav = TempWav::write(1, SR, &ramp);

    let target = 6000u64;
    let mut track = open_streaming(wav.path_str(), 0, 0, SR as usize).expect("open streaming");
    track.seek(target);

    // Give the prefetch thread time to perform the seek and refill.
    let mut buf = vec![0.0f32; 64];
    let mut got = None;
    for _ in 0..500 {
        let n = track.read(&mut buf);
        if n > 0 {
            got = Some(buf[0]);
            break;
        }
        std::thread::sleep(std::time::Duration::from_millis(2));
    }

    let first = got.expect("should read samples after seek");
    let landed = (first * 32768.0).round() as i64;
    let delta = (landed - target as i64).abs();
    // A few frames of slack: the ring may briefly hold pre-seek samples.
    assert!(delta <= 64, "post-seek landed at frame {landed}, expected {target}");
}

#[test]
fn drop_stops_prefetch_thread() {
    let ramp = mono_ramp(100_000);
    let wav = TempWav::write(1, SR, &ramp);
    // Opening and immediately dropping must not hang or panic (thread joins).
    let track = open_streaming(wav.path_str(), 0, 0, SR as usize).expect("open");
    drop(track);
}

#[test]
fn open_missing_file_errors() {
    let result = open_streaming("/nonexistent/path/to/file.wav", 0, 0, 4096);
    assert!(result.is_err());
}
