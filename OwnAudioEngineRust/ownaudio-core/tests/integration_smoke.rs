/// Smoke test: open the default output device, play a 440 Hz sine wave for
/// ~1 second, then stop cleanly.  The test passes if no panic or error occurs.
///
/// This test requires a real audio device.  On headless CI without audio
/// hardware (or a virtual sink), run with:
///   CPAL_ASOUND_CARD=default   (Linux / ALSA)
/// or configure a virtual audio device before running.
#[test]
fn sine_wave_output_smoke() {
    use ownaudio_core::{AudioEngine, StreamConfig};
    use std::f32::consts::TAU;

    let engine = AudioEngine::new().expect("AudioEngine::new failed");
    let config = StreamConfig::stereo_f32(48_000);

    let sample_rate = config.sample_rate as f32;
    let channels = config.channels as usize;
    let freq = 440.0_f32;
    let mut phase = 0.0_f32;
    let phase_step = freq / sample_rate;

    let stream = engine
        .open_output_stream(None, &config, move |buf| {
            // REAL-TIME PATH: no heap allocation here.
            for frame in buf.chunks_mut(channels) {
                let sample = (phase * TAU).sin() * 0.2;
                for ch in frame.iter_mut() {
                    *ch = sample;
                }
                phase = (phase + phase_step) % 1.0;
            }
        })
        .expect("open_output_stream failed");

    stream.play().expect("play failed");
    std::thread::sleep(std::time::Duration::from_millis(1_000));
    // Dropping `stream` here stops and destroys the Cpal stream.
}

/// Full-chain smoke test: sine wave → ring buffer → resampler (44100 → 48000)
/// → mixer → output stream.  Passes if the chain produces finite samples with
/// no errors or panics.  Does not require an audio device to be present.
#[test]
fn ring_buffer_resampler_mixer_chain() {
    use ownaudio_core::{format, ring_buffer, Mixer, Resampler};
    use std::f32::consts::TAU;

    let input_rate = 44_100u32;
    let output_rate = 48_000u32;
    let channels = 2usize;
    let frames = 512usize;

    // 1. Generate interleaved stereo sine at 44 100 Hz.
    let mut source_interleaved = vec![0.0f32; frames * channels];
    let freq = 440.0f32;
    let sr = input_rate as f32;
    for (i, chunk) in source_interleaved.chunks_mut(channels).enumerate() {
        let s = (i as f32 / sr * freq * TAU).sin() * 0.3;
        for sample in chunk.iter_mut() {
            *sample = s;
        }
    }

    // 2. Write into ring buffer and read it back.
    let buf_cap = source_interleaved.len() * 2;
    let (mut writer, mut reader) = ring_buffer(buf_cap);
    let written = writer.write(&source_interleaved);
    assert_eq!(written, source_interleaved.len());

    let mut rb_out = vec![0.0f32; written];
    let read = reader.read(&mut rb_out);
    assert_eq!(read, written);

    // 3. Deinterleave → planar for the resampler.
    let mut planar_in = vec![vec![0.0f32; frames]; channels];
    format::deinterleave(&rb_out, channels, &mut planar_in);

    // 4. Resample from 44 100 → 48 000.
    let mut rs = Resampler::new(input_rate, output_rate, channels, frames).unwrap();
    let out_max = rs.output_frames_max();
    let mut planar_resampled = vec![vec![0.0f32; out_max]; channels];
    let frames_out = rs.process(&planar_in, &mut planar_resampled).unwrap();

    for ch in &planar_resampled {
        assert!(
            ch[..frames_out].iter().all(|&s| s.is_finite()),
            "resampled output must be finite"
        );
    }

    // 5. Mix two copies of the resampled output (simulates two tracks).
    let ch0_a: &[f32] = &planar_resampled[0][..frames_out];
    let ch1_a: &[f32] = &planar_resampled[1][..frames_out];
    let ch0_b = ch0_a.to_vec(); // second "track" copy
    let ch1_b = ch1_a.to_vec();

    let mut mixer = Mixer::new(frames_out);
    let mut mixed_ch0 = vec![0.0f32; frames_out];
    let mut mixed_ch1 = vec![0.0f32; frames_out];
    mixer.mix(&[ch0_a, &ch0_b], &mut mixed_ch0);
    mixer.mix(&[ch1_a, &ch1_b], &mut mixed_ch1);

    assert!(mixed_ch0.iter().all(|&s| s.is_finite()));
    assert!(mixed_ch1.iter().all(|&s| s.is_finite()));
    assert!(frames_out > 0, "resampler must produce output frames");
}

/// Smoke test: enumerate output and input devices without panicking.
#[test]
fn device_list_smoke() {
    use ownaudio_core::{list_input_devices, list_output_devices};

    let outputs = list_output_devices().expect("list_output_devices failed");
    let inputs = list_input_devices().expect("list_input_devices failed");

    println!("Output devices ({}):", outputs.len());
    for d in &outputs {
        println!("  {:?}", d);
    }

    println!("Input devices ({}):", inputs.len());
    for d in &inputs {
        println!("  {:?}", d);
    }

    // At least one output device must exist on any real machine.
    assert!(!outputs.is_empty(), "no output devices found");
}
