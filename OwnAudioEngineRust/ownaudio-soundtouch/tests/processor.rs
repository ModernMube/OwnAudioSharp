//! End-to-end behaviour tests for [`SoundTouchProcessor`].
//!
//! These verify the duration relationships the C# pipeline guarantees
//! (`input * input_output_sample_ratio ≈ output`), pitch-preserves-duration,
//! and signal sanity (finite, no NaN/Inf), which together pin the WSOLA chain
//! to its reference behaviour.

use ownaudio_soundtouch::SoundTouchProcessor;

/// Pushes `total_frames` of a test sine through the processor and drains every
/// available output frame, returning the full output (interleaved).
fn run_full(st: &mut SoundTouchProcessor, channels: usize, total_frames: usize) -> Vec<f32> {
    let block = 1024usize;
    let mut input = vec![0.0f32; block * channels];
    let mut out = Vec::new();
    let mut scratch = vec![0.0f32; block * 4 * channels];

    let mut phase = 0.0f32;
    let mut produced = 0usize;
    while produced < total_frames {
        let n = block.min(total_frames - produced);
        for f in 0..n {
            let s = (phase).sin() * 0.5;
            phase += 0.05;
            for c in 0..channels {
                input[f * channels + c] = s;
            }
        }
        st.put_samples(&input, n).unwrap();
        produced += n;

        loop {
            let got = st.receive_samples(&mut scratch, block * 4);
            if got == 0 {
                break;
            }
            out.extend_from_slice(&scratch[..got * channels]);
        }
    }

    st.flush();
    loop {
        let got = st.receive_samples(&mut scratch, block * 4);
        if got == 0 {
            break;
        }
        out.extend_from_slice(&scratch[..got * channels]);
    }
    out
}

fn assert_finite(samples: &[f32]) {
    assert!(
        samples.iter().all(|v| v.is_finite()),
        "output contains non-finite samples"
    );
}

#[test]
fn tempo_up_halves_duration() {
    let mut st = SoundTouchProcessor::new();
    st.set_sample_rate(44100).unwrap();
    st.set_channels(2).unwrap();
    st.set_tempo(2.0);

    let total = 44100; // 1 second
    let out = run_full(&mut st, 2, total);
    let out_frames = out.len() / 2;
    assert_finite(&out);

    let ratio = out_frames as f64 / total as f64;
    assert!(
        (ratio - 0.5).abs() < 0.05,
        "tempo=2.0 expected ~0.5 length ratio, got {ratio}"
    );
}

#[test]
fn tempo_down_doubles_duration() {
    let mut st = SoundTouchProcessor::new();
    st.set_sample_rate(44100).unwrap();
    st.set_channels(2).unwrap();
    st.set_tempo(0.5);

    let total = 44100;
    let out = run_full(&mut st, 2, total);
    let out_frames = out.len() / 2;
    assert_finite(&out);

    let ratio = out_frames as f64 / total as f64;
    assert!(
        (ratio - 2.0).abs() < 0.1,
        "tempo=0.5 expected ~2.0 length ratio, got {ratio}"
    );
}

#[test]
fn pitch_shift_preserves_duration() {
    let mut st = SoundTouchProcessor::new();
    st.set_sample_rate(44100).unwrap();
    st.set_channels(2).unwrap();
    st.set_pitch_semitones(4.0); // +4 semitones

    let total = 44100;
    let out = run_full(&mut st, 2, total);
    let out_frames = out.len() / 2;
    assert_finite(&out);

    let ratio = out_frames as f64 / total as f64;
    assert!(
        (ratio - 1.0).abs() < 0.06,
        "pitch shift should preserve duration, got ratio {ratio}"
    );
}

#[test]
fn unity_settings_reproduce_duration_mono() {
    let mut st = SoundTouchProcessor::new();
    st.set_sample_rate(48000).unwrap();
    st.set_channels(1).unwrap();

    let total = 48000;
    let out = run_full(&mut st, 1, total);
    assert_finite(&out);
    let ratio = out.len() as f64 / total as f64;
    assert!(
        (ratio - 1.0).abs() < 0.03,
        "unity settings should reproduce duration, got {ratio}"
    );
}

#[test]
fn ratio_matches_reported_value() {
    let mut st = SoundTouchProcessor::new();
    st.set_sample_rate(44100).unwrap();
    st.set_channels(2).unwrap();
    st.set_tempo(1.3);
    st.set_pitch_semitones(-2.0);

    let reported = st.input_output_sample_ratio();
    let total = 88200;
    let out = run_full(&mut st, 2, total);
    let measured = (out.len() / 2) as f64 / total as f64;
    assert!(
        (measured - reported).abs() / reported < 0.05,
        "measured ratio {measured} should match reported {reported}"
    );
}

#[test]
fn put_before_sample_rate_errors() {
    let mut st = SoundTouchProcessor::new();
    let buf = vec![0.0f32; 256];
    assert!(st.put_samples(&buf, 128).is_err());
}

#[test]
fn settings_roundtrip() {
    let mut st = SoundTouchProcessor::new();
    st.set_sample_rate(44100).unwrap();
    st.set_channels(2).unwrap();

    use ownaudio_soundtouch::SettingId;
    assert!(st.set_setting(SettingId::UseQuickSeek, 1));
    assert_eq!(st.get_setting(SettingId::UseQuickSeek), 1);
    assert!(st.set_setting(SettingId::SequenceDurationMs, 60));
    assert_eq!(st.get_setting(SettingId::SequenceDurationMs), 60);
    assert!(st.set_setting(SettingId::UseAntiAliasFilter, 0));
    assert_eq!(st.get_setting(SettingId::UseAntiAliasFilter), 0);
}

#[test]
fn quick_seek_also_runs_clean() {
    let mut st = SoundTouchProcessor::new();
    st.set_sample_rate(44100).unwrap();
    st.set_channels(2).unwrap();
    st.set_tempo(1.5);
    use ownaudio_soundtouch::SettingId;
    st.set_setting(SettingId::UseQuickSeek, 1);

    let out = run_full(&mut st, 2, 44100);
    assert_finite(&out);
    let ratio = (out.len() / 2) as f64 / 44100.0;
    assert!((ratio - (1.0 / 1.5)).abs() < 0.06, "got {ratio}");
}
