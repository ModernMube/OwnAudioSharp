/// FFI smoke tests — exercise the extern "C" surface directly from Rust.
///
/// These tests intentionally avoid any C# code; they validate that the ABI
/// layer itself is correct before the C# binding (step 4) is built.
///
/// Functions are linked via `rlib` so they are callable as ordinary Rust fns.
/// `unsafe` is used only where raw pointers are dereferenced inside callbacks.
use ownaudio_ffi::{
    error_code::OwnAudioErrorCode,
    ffi_config::{OwnAudioSampleFormat, OwnAudioStreamConfig},
    ffi_device::{
        ownaudio_v1_free_device_list, ownaudio_v1_list_input_devices,
        ownaudio_v1_list_output_devices, OwnAudioDeviceInfo,
    },
    ffi_stream::{
        ownaudio_v1_engine_create, ownaudio_v1_engine_destroy, ownaudio_v1_open_output_stream,
        ownaudio_v1_output_stream_destroy, ownaudio_v1_output_stream_pause,
        ownaudio_v1_output_stream_play,
    },
    handles::{OwnAudioEngineHandle, OwnAudioOutputStreamHandle},
};

// ---------------------------------------------------------------------------
// Null-safety tests — these must never panic
// ---------------------------------------------------------------------------

#[test]
fn engine_create_null_out_returns_null_pointer_error() {
    let code = ownaudio_v1_engine_create(std::ptr::null_mut());
    assert_eq!(code, OwnAudioErrorCode::NullPointer as i32);
}

#[test]
fn engine_destroy_null_is_safe() {
    ownaudio_v1_engine_destroy(std::ptr::null_mut());
}

#[test]
fn list_output_devices_null_out_returns_null_pointer_error() {
    let mut count: usize = 0;
    let code = ownaudio_v1_list_output_devices(std::ptr::null_mut(), &mut count);
    assert_eq!(code, OwnAudioErrorCode::NullPointer as i32);
}

#[test]
fn free_device_list_null_is_safe() {
    ownaudio_v1_free_device_list(std::ptr::null_mut(), 0);
}

#[test]
fn output_stream_play_null_is_invalid_handle() {
    let code = ownaudio_v1_output_stream_play(std::ptr::null_mut());
    assert_eq!(code, OwnAudioErrorCode::InvalidHandle as i32);
}

#[test]
fn output_stream_pause_null_is_invalid_handle() {
    let code = ownaudio_v1_output_stream_pause(std::ptr::null_mut());
    assert_eq!(code, OwnAudioErrorCode::InvalidHandle as i32);
}

#[test]
fn output_stream_destroy_null_is_safe() {
    ownaudio_v1_output_stream_destroy(std::ptr::null_mut());
}

// ---------------------------------------------------------------------------
// Engine lifecycle smoke test
// ---------------------------------------------------------------------------

#[test]
fn engine_create_and_destroy_smoke() {
    let mut handle: *mut OwnAudioEngineHandle = std::ptr::null_mut();
    let code = ownaudio_v1_engine_create(&mut handle);
    assert_eq!(code, OwnAudioErrorCode::Success as i32, "engine_create failed");
    assert!(!handle.is_null(), "engine handle must not be null on success");
    ownaudio_v1_engine_destroy(handle);
}

// ---------------------------------------------------------------------------
// Device enumeration smoke tests
// ---------------------------------------------------------------------------

#[test]
fn list_output_devices_smoke() {
    let mut devices: *mut OwnAudioDeviceInfo = std::ptr::null_mut();
    let mut count: usize = 0;
    let code = ownaudio_v1_list_output_devices(&mut devices, &mut count);
    assert_eq!(code, OwnAudioErrorCode::Success as i32, "list_output_devices failed");
    // count == 0 is valid on headless CI; just ensure no crash.
    ownaudio_v1_free_device_list(devices, count);
}

#[test]
fn list_input_devices_smoke() {
    let mut devices: *mut OwnAudioDeviceInfo = std::ptr::null_mut();
    let mut count: usize = 0;
    let code = ownaudio_v1_list_input_devices(&mut devices, &mut count);
    assert_eq!(code, OwnAudioErrorCode::Success as i32, "list_input_devices failed");
    ownaudio_v1_free_device_list(devices, count);
}

// ---------------------------------------------------------------------------
// Output stream lifecycle smoke test
// ---------------------------------------------------------------------------

/// Silent-output stream: create engine → open stream → play → pause → destroy.
///
/// Requires a real audio device.  On headless CI the `open_output_stream` call
/// will return `DeviceNotFound` / `UnsupportedConfig` / `StreamBuildFailed`,
/// which the test treats as expected non-panic outcomes.
#[test]
fn output_stream_create_play_pause_destroy_smoke() {
    let mut engine: *mut OwnAudioEngineHandle = std::ptr::null_mut();
    let code = ownaudio_v1_engine_create(&mut engine);
    assert_eq!(code, OwnAudioErrorCode::Success as i32);

    let config = OwnAudioStreamConfig {
        sample_rate: 48_000,
        channels: 2,
        sample_format: OwnAudioSampleFormat::F32,
        buffer_size_frames: 0,
    };

    unsafe extern "C" fn silence_callback(
        buffer: *mut f32,
        frame_count: usize,
        channels: u16,
        _user_data: *mut std::os::raw::c_void,
    ) {
        let len = frame_count * channels as usize;
        // SAFETY: buffer and len are provided by the audio engine.
        let slice = unsafe { std::slice::from_raw_parts_mut(buffer, len) };
        slice.fill(0.0);
    }

    let mut stream: *mut OwnAudioOutputStreamHandle = std::ptr::null_mut();
    let open_code = ownaudio_v1_open_output_stream(
        engine,
        std::ptr::null(), // use default device
        &config,
        Some(silence_callback),
        std::ptr::null_mut(),
        &mut stream,
    );

    if open_code == OwnAudioErrorCode::Success as i32 {
        assert!(!stream.is_null());

        let play_code = ownaudio_v1_output_stream_play(stream);
        assert_eq!(play_code, OwnAudioErrorCode::Success as i32);

        std::thread::sleep(std::time::Duration::from_millis(100));

        let pause_code = ownaudio_v1_output_stream_pause(stream);
        assert_eq!(pause_code, OwnAudioErrorCode::Success as i32);

        ownaudio_v1_output_stream_destroy(stream);
    } else {
        // No audio device available (headless CI) — not a failure.
        let allowed = [
            OwnAudioErrorCode::DeviceNotFound as i32,
            OwnAudioErrorCode::UnsupportedConfig as i32,
            OwnAudioErrorCode::StreamBuildFailed as i32,
        ];
        assert!(
            allowed.contains(&open_code),
            "unexpected error code {open_code} when no audio device available"
        );
    }

    ownaudio_v1_engine_destroy(engine);
}

// ---------------------------------------------------------------------------
// Track-source feed (ring buffer) tests
// ---------------------------------------------------------------------------

mod track_source {
    use ownaudio_ffi::error_code::OwnAudioErrorCode;
    use ownaudio_ffi::ffi_source::{
        ownaudio_v1_track_clear_source, ownaudio_v1_track_set_ring_source,
        ownaudio_v1_track_source_destroy, ownaudio_v1_track_source_free_samples,
        ownaudio_v1_track_source_write,
    };
    use ownaudio_ffi::ffi_track::{
        ownaudio_v1_mixer_create, ownaudio_v1_mixer_destroy, ownaudio_v1_mixer_get_master_peaks,
        ownaudio_v1_mixer_pause_all, ownaudio_v1_mixer_play_all, ownaudio_v1_mixer_set_master_gain,
        ownaudio_v1_mixer_stop_all, ownaudio_v1_track_create, ownaudio_v1_track_destroy,
        ownaudio_v1_track_get_peaks, ownaudio_v1_track_get_rendered_frames,
        ownaudio_v1_track_reset_position,
    };
    use ownaudio_ffi::handles::{
        OwnAudioMixerHandle, OwnAudioTrackHandle, OwnAudioTrackSourceHandle,
    };

    #[test]
    fn transport_all_null_handle_is_invalid_handle() {
        assert_eq!(
            ownaudio_v1_mixer_pause_all(std::ptr::null_mut()),
            OwnAudioErrorCode::InvalidHandle as i32
        );
        assert_eq!(
            ownaudio_v1_mixer_stop_all(std::ptr::null_mut()),
            OwnAudioErrorCode::InvalidHandle as i32
        );
    }

    #[test]
    fn mixer_play_pause_stop_all_smoke() {
        let mut mixer: *mut OwnAudioMixerHandle = std::ptr::null_mut();
        assert_eq!(
            ownaudio_v1_mixer_create(48_000.0, 2, &mut mixer),
            OwnAudioErrorCode::Success as i32
        );

        let mut track_a: *mut OwnAudioTrackHandle = std::ptr::null_mut();
        let mut track_b: *mut OwnAudioTrackHandle = std::ptr::null_mut();
        assert_eq!(
            ownaudio_v1_track_create(mixer, &mut track_a),
            OwnAudioErrorCode::Success as i32
        );
        assert_eq!(
            ownaudio_v1_track_create(mixer, &mut track_b),
            OwnAudioErrorCode::Success as i32
        );

        assert_eq!(
            ownaudio_v1_mixer_play_all(mixer),
            OwnAudioErrorCode::Success as i32
        );
        assert_eq!(
            ownaudio_v1_mixer_pause_all(mixer),
            OwnAudioErrorCode::Success as i32
        );
        assert_eq!(
            ownaudio_v1_mixer_stop_all(mixer),
            OwnAudioErrorCode::Success as i32
        );

        ownaudio_v1_track_destroy(track_a);
        ownaudio_v1_track_destroy(track_b);
        ownaudio_v1_mixer_destroy(mixer);
    }

    #[test]
    fn master_gain_and_metering_contract_smoke() {
        // Null handle → InvalidHandle; null out-pointers → NullPointer.
        assert_eq!(
            ownaudio_v1_mixer_set_master_gain(std::ptr::null_mut(), 0.5),
            OwnAudioErrorCode::InvalidHandle as i32
        );
        let mut l = 9.0f32;
        let mut r = 9.0f32;
        assert_eq!(
            ownaudio_v1_mixer_get_master_peaks(std::ptr::null_mut(), &mut l, &mut r),
            OwnAudioErrorCode::InvalidHandle as i32
        );
        assert_eq!(
            ownaudio_v1_track_get_peaks(std::ptr::null_mut(), &mut l, &mut r),
            OwnAudioErrorCode::InvalidHandle as i32
        );

        let mut mixer: *mut OwnAudioMixerHandle = std::ptr::null_mut();
        assert_eq!(
            ownaudio_v1_mixer_create(48_000.0, 2, &mut mixer),
            OwnAudioErrorCode::Success as i32
        );
        let mut track: *mut OwnAudioTrackHandle = std::ptr::null_mut();
        assert_eq!(
            ownaudio_v1_track_create(mixer, &mut track),
            OwnAudioErrorCode::Success as i32
        );

        assert_eq!(
            ownaudio_v1_mixer_get_master_peaks(mixer, std::ptr::null_mut(), &mut r),
            OwnAudioErrorCode::NullPointer as i32
        );
        assert_eq!(
            ownaudio_v1_track_get_peaks(track, &mut l, std::ptr::null_mut()),
            OwnAudioErrorCode::NullPointer as i32
        );

        // Setting the master gain succeeds; a fresh mixer/track reports zero peaks.
        assert_eq!(
            ownaudio_v1_mixer_set_master_gain(mixer, 0.5),
            OwnAudioErrorCode::Success as i32
        );
        let (mut ml, mut mr) = (9.0f32, 9.0f32);
        assert_eq!(
            ownaudio_v1_mixer_get_master_peaks(mixer, &mut ml, &mut mr),
            OwnAudioErrorCode::Success as i32
        );
        assert_eq!((ml, mr), (0.0, 0.0));
        let (mut tl, mut tr) = (9.0f32, 9.0f32);
        assert_eq!(
            ownaudio_v1_track_get_peaks(track, &mut tl, &mut tr),
            OwnAudioErrorCode::Success as i32
        );
        assert_eq!((tl, tr), (0.0, 0.0));

        ownaudio_v1_track_destroy(track);
        ownaudio_v1_mixer_destroy(mixer);
    }

    #[test]
    fn get_rendered_frames_contract_smoke() {
        // Null handle → InvalidHandle; null out-pointer → NullPointer.
        let mut frames: u64 = 999;
        assert_eq!(
            ownaudio_v1_track_get_rendered_frames(std::ptr::null_mut(), &mut frames),
            OwnAudioErrorCode::InvalidHandle as i32
        );
        assert_eq!(
            ownaudio_v1_track_reset_position(std::ptr::null_mut()),
            OwnAudioErrorCode::InvalidHandle as i32
        );

        let mut mixer: *mut OwnAudioMixerHandle = std::ptr::null_mut();
        assert_eq!(
            ownaudio_v1_mixer_create(48_000.0, 2, &mut mixer),
            OwnAudioErrorCode::Success as i32
        );
        let mut track: *mut OwnAudioTrackHandle = std::ptr::null_mut();
        assert_eq!(
            ownaudio_v1_track_create(mixer, &mut track),
            OwnAudioErrorCode::Success as i32
        );

        assert_eq!(
            ownaudio_v1_track_get_rendered_frames(track, std::ptr::null_mut()),
            OwnAudioErrorCode::NullPointer as i32
        );

        // A fresh track has rendered nothing yet; reset keeps it at zero.
        let mut frames: u64 = 42;
        assert_eq!(
            ownaudio_v1_track_get_rendered_frames(track, &mut frames),
            OwnAudioErrorCode::Success as i32
        );
        assert_eq!(frames, 0);

        assert_eq!(
            ownaudio_v1_track_reset_position(track),
            OwnAudioErrorCode::Success as i32
        );
        let mut frames: u64 = 42;
        assert_eq!(
            ownaudio_v1_track_get_rendered_frames(track, &mut frames),
            OwnAudioErrorCode::Success as i32
        );
        assert_eq!(frames, 0);

        ownaudio_v1_track_destroy(track);
        ownaudio_v1_mixer_destroy(mixer);
    }

    #[test]
    fn write_null_handle_is_null_pointer() {
        let mut written: usize = 123;
        let samples = [0.0f32; 4];
        let code = ownaudio_v1_track_source_write(
            std::ptr::null_mut(),
            samples.as_ptr(),
            samples.len(),
            &mut written,
        );
        assert_eq!(code, OwnAudioErrorCode::NullPointer as i32);
    }

    #[test]
    fn destroy_null_is_safe() {
        ownaudio_v1_track_source_destroy(std::ptr::null_mut());
    }

    #[test]
    fn set_ring_source_write_and_free_smoke() {
        let mut mixer: *mut OwnAudioMixerHandle = std::ptr::null_mut();
        assert_eq!(
            ownaudio_v1_mixer_create(48_000.0, 2, &mut mixer),
            OwnAudioErrorCode::Success as i32
        );
        assert!(!mixer.is_null());

        let mut track: *mut OwnAudioTrackHandle = std::ptr::null_mut();
        assert_eq!(
            ownaudio_v1_track_create(mixer, &mut track),
            OwnAudioErrorCode::Success as i32
        );

        let mut source: *mut OwnAudioTrackSourceHandle = std::ptr::null_mut();
        let cap = 8usize;
        assert_eq!(
            ownaudio_v1_track_set_ring_source(mixer, track, cap, &mut source),
            OwnAudioErrorCode::Success as i32
        );
        assert!(!source.is_null());

        // The empty ring exposes its full capacity as free.
        let mut free: usize = 0;
        assert_eq!(
            ownaudio_v1_track_source_free_samples(source, &mut free),
            OwnAudioErrorCode::Success as i32
        );
        assert_eq!(free, cap);

        // Writing more than fits accepts only the capacity, reports it back.
        let data = [1.0f32; 16];
        let mut written: usize = 0;
        assert_eq!(
            ownaudio_v1_track_source_write(source, data.as_ptr(), data.len(), &mut written),
            OwnAudioErrorCode::Success as i32
        );
        assert_eq!(written, cap);

        // Now full: zero free, next write accepts nothing.
        assert_eq!(
            ownaudio_v1_track_source_free_samples(source, &mut free),
            OwnAudioErrorCode::Success as i32
        );
        assert_eq!(free, 0);
        assert_eq!(
            ownaudio_v1_track_source_write(source, data.as_ptr(), data.len(), &mut written),
            OwnAudioErrorCode::Success as i32
        );
        assert_eq!(written, 0);

        // Clearing the source and re-installing is accepted.
        assert_eq!(
            ownaudio_v1_track_clear_source(mixer, track),
            OwnAudioErrorCode::Success as i32
        );

        ownaudio_v1_track_source_destroy(source);
        ownaudio_v1_track_destroy(track);
        ownaudio_v1_mixer_destroy(mixer);
    }
}

// ---------------------------------------------------------------------------
// File-backed track source tests
// ---------------------------------------------------------------------------

mod file_source {
    use ownaudio_ffi::error_code::OwnAudioErrorCode;
    use ownaudio_ffi::ffi_file_source::{
        ownaudio_v1_file_source_destroy, ownaudio_v1_file_source_is_finished,
        ownaudio_v1_file_source_seek, ownaudio_v1_file_source_set_loop, ownaudio_v1_track_open_file,
    };
    use ownaudio_ffi::ffi_source::ownaudio_v1_track_clear_source;
    use ownaudio_ffi::ffi_track::{
        ownaudio_v1_mixer_create, ownaudio_v1_mixer_destroy, ownaudio_v1_track_create,
        ownaudio_v1_track_destroy,
    };
    use ownaudio_ffi::handles::{
        OwnAudioFileSourceHandle, OwnAudioMixerHandle, OwnAudioTrackHandle,
    };
    use std::ffi::CString;
    use std::io::Write;
    use std::path::PathBuf;

    /// Writes a short stereo 16-bit PCM WAV to a unique temp path and returns it.
    /// Removed on drop.
    struct TempWav {
        path: PathBuf,
    }

    impl TempWav {
        fn new(frames: usize) -> Self {
            use std::sync::atomic::{AtomicU64, Ordering};
            static COUNTER: AtomicU64 = AtomicU64::new(0);
            let id = COUNTER.fetch_add(1, Ordering::Relaxed);
            let path = std::env::temp_dir()
                .join(format!("ownaudio_ffi_test_{}_{}.wav", std::process::id(), id));

            let channels: u16 = 2;
            let sample_rate: u32 = 44_100;
            let interleaved: Vec<i16> = vec![1_000i16; frames * channels as usize];
            let data_len = (interleaved.len() * 2) as u32;
            let byte_rate = sample_rate * channels as u32 * 2;
            let block_align = channels * 2;

            let mut buf: Vec<u8> = Vec::with_capacity(44 + data_len as usize);
            buf.extend_from_slice(b"RIFF");
            buf.extend_from_slice(&(36 + data_len).to_le_bytes());
            buf.extend_from_slice(b"WAVE");
            buf.extend_from_slice(b"fmt ");
            buf.extend_from_slice(&16u32.to_le_bytes());
            buf.extend_from_slice(&1u16.to_le_bytes()); // PCM
            buf.extend_from_slice(&channels.to_le_bytes());
            buf.extend_from_slice(&sample_rate.to_le_bytes());
            buf.extend_from_slice(&byte_rate.to_le_bytes());
            buf.extend_from_slice(&block_align.to_le_bytes());
            buf.extend_from_slice(&16u16.to_le_bytes());
            buf.extend_from_slice(b"data");
            buf.extend_from_slice(&data_len.to_le_bytes());
            for &s in &interleaved {
                buf.extend_from_slice(&s.to_le_bytes());
            }

            let mut file = std::fs::File::create(&path).expect("create temp wav");
            file.write_all(&buf).expect("write temp wav");
            file.flush().expect("flush temp wav");

            TempWav { path }
        }

        fn c_path(&self) -> CString {
            CString::new(self.path.to_str().unwrap()).unwrap()
        }
    }

    impl Drop for TempWav {
        fn drop(&mut self) {
            let _ = std::fs::remove_file(&self.path);
        }
    }

    #[test]
    fn control_null_handles_are_invalid() {
        assert_eq!(
            ownaudio_v1_file_source_set_loop(std::ptr::null_mut(), 1),
            OwnAudioErrorCode::InvalidHandle as i32
        );
        assert_eq!(
            ownaudio_v1_file_source_seek(std::ptr::null_mut(), 0),
            OwnAudioErrorCode::InvalidHandle as i32
        );

        let mut finished: u8 = 9;
        assert_eq!(
            ownaudio_v1_file_source_is_finished(std::ptr::null_mut(), &mut finished),
            OwnAudioErrorCode::InvalidHandle as i32
        );
    }

    #[test]
    fn is_finished_null_out_is_null_pointer() {
        // A null out-pointer is reported before the handle is even examined.
        assert_eq!(
            ownaudio_v1_file_source_is_finished(std::ptr::null_mut(), std::ptr::null_mut()),
            OwnAudioErrorCode::NullPointer as i32
        );
    }

    #[test]
    fn destroy_null_is_safe() {
        ownaudio_v1_file_source_destroy(std::ptr::null_mut());
    }

    #[test]
    fn open_file_null_args_are_null_pointer() {
        let mut source: *mut OwnAudioFileSourceHandle = std::ptr::null_mut();
        assert_eq!(
            ownaudio_v1_track_open_file(
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                std::ptr::null(),
                44_100,
                2,
                0,
                &mut source,
            ),
            OwnAudioErrorCode::NullPointer as i32
        );
    }

    #[test]
    fn open_file_control_and_teardown_smoke() {
        let wav = TempWav::new(4_000);
        let path = wav.c_path();

        let mut mixer: *mut OwnAudioMixerHandle = std::ptr::null_mut();
        assert_eq!(
            ownaudio_v1_mixer_create(44_100.0, 2, &mut mixer),
            OwnAudioErrorCode::Success as i32
        );
        let mut track: *mut OwnAudioTrackHandle = std::ptr::null_mut();
        assert_eq!(
            ownaudio_v1_track_create(mixer, &mut track),
            OwnAudioErrorCode::Success as i32
        );

        let mut source: *mut OwnAudioFileSourceHandle = std::ptr::null_mut();
        assert_eq!(
            ownaudio_v1_track_open_file(mixer, track, path.as_ptr(), 44_100, 2, 8_192, &mut source),
            OwnAudioErrorCode::Success as i32
        );
        assert!(!source.is_null());

        // A freshly opened source is not finished, and the control calls succeed.
        let mut finished: u8 = 9;
        assert_eq!(
            ownaudio_v1_file_source_is_finished(source, &mut finished),
            OwnAudioErrorCode::Success as i32
        );
        assert_eq!(finished, 0);

        assert_eq!(
            ownaudio_v1_file_source_set_loop(source, 1),
            OwnAudioErrorCode::Success as i32
        );
        assert_eq!(
            ownaudio_v1_file_source_seek(source, 1_000),
            OwnAudioErrorCode::Success as i32
        );

        // Tear down in the documented order: clear the track's source (retires the
        // decoding source off the audio thread), then destroy the control handle.
        assert_eq!(
            ownaudio_v1_track_clear_source(mixer, track),
            OwnAudioErrorCode::Success as i32
        );
        ownaudio_v1_file_source_destroy(source);
        ownaudio_v1_track_destroy(track);
        ownaudio_v1_mixer_destroy(mixer);
    }
}

// ---------------------------------------------------------------------------
// Master effect bus tests
// ---------------------------------------------------------------------------

mod master_effect {
    use ownaudio_ffi::error_code::OwnAudioErrorCode;
    use ownaudio_ffi::ffi_effects::{
        ownaudio_v1_effect_get_param, ownaudio_v1_effect_set_param,
        ownaudio_v1_mixer_add_master_effect, ownaudio_v1_mixer_remove_master_effect,
    };
    use ownaudio_ffi::ffi_track::{ownaudio_v1_mixer_create, ownaudio_v1_mixer_destroy};
    use ownaudio_ffi::handles::{OwnAudioEffectHandle, OwnAudioMixerHandle};

    const EFFECT_TYPE_COMPRESSOR: u32 = 2;
    const PARAM_THRESHOLD: u32 = 2;

    #[test]
    fn add_master_effect_null_args_are_null_pointer() {
        let mut effect: *mut OwnAudioEffectHandle = std::ptr::null_mut();
        assert_eq!(
            ownaudio_v1_mixer_add_master_effect(std::ptr::null_mut(), 0, 48_000.0, &mut effect),
            OwnAudioErrorCode::NullPointer as i32
        );
    }

    #[test]
    fn add_master_effect_unknown_type_is_invalid_handle() {
        let mut mixer: *mut OwnAudioMixerHandle = std::ptr::null_mut();
        assert_eq!(
            ownaudio_v1_mixer_create(48_000.0, 2, &mut mixer),
            OwnAudioErrorCode::Success as i32
        );
        let mut effect: *mut OwnAudioEffectHandle = std::ptr::null_mut();
        assert_eq!(
            ownaudio_v1_mixer_add_master_effect(mixer, 9_999, 48_000.0, &mut effect),
            OwnAudioErrorCode::InvalidHandle as i32
        );
        ownaudio_v1_mixer_destroy(mixer);
    }

    #[test]
    fn add_set_param_and_remove_master_effect_smoke() {
        let mut mixer: *mut OwnAudioMixerHandle = std::ptr::null_mut();
        assert_eq!(
            ownaudio_v1_mixer_create(48_000.0, 2, &mut mixer),
            OwnAudioErrorCode::Success as i32
        );

        let mut effect: *mut OwnAudioEffectHandle = std::ptr::null_mut();
        assert_eq!(
            ownaudio_v1_mixer_add_master_effect(
                mixer,
                EFFECT_TYPE_COMPRESSOR,
                48_000.0,
                &mut effect
            ),
            OwnAudioErrorCode::Success as i32
        );
        assert!(!effect.is_null());

        // Setting a known param succeeds and is reflected by the control-side shadow.
        assert_eq!(
            ownaudio_v1_effect_set_param(mixer, effect, PARAM_THRESHOLD, -18.0),
            OwnAudioErrorCode::Success as i32
        );
        let mut value: f32 = 0.0;
        assert_eq!(
            ownaudio_v1_effect_get_param(mixer, effect, PARAM_THRESHOLD, &mut value),
            OwnAudioErrorCode::Success as i32
        );
        assert_eq!(value, -18.0);

        // The dedicated master remove retires the effect and destroys the handle.
        assert_eq!(
            ownaudio_v1_mixer_remove_master_effect(mixer, effect),
            OwnAudioErrorCode::Success as i32
        );

        ownaudio_v1_mixer_destroy(mixer);
    }
}

// ---------------------------------------------------------------------------
// VST3 plugin effect tests
// ---------------------------------------------------------------------------

mod vst_effect {
    use std::os::raw::c_void;

    use ownaudio_ffi::error_code::OwnAudioErrorCode;
    use ownaudio_ffi::ffi_effects::{
        ownaudio_v1_effect_get_param, ownaudio_v1_effect_set_param,
        ownaudio_v1_mixer_add_master_vst_effect, ownaudio_v1_mixer_remove_master_effect,
        ownaudio_v1_track_add_vst_effect,
    };
    use ownaudio_ffi::ffi_track::{
        ownaudio_v1_mixer_create, ownaudio_v1_mixer_destroy, ownaudio_v1_track_create,
    };
    use ownaudio_ffi::handles::{
        OwnAudioEffectHandle, OwnAudioMixerHandle, OwnAudioTrackHandle,
    };
    use ownaudio_ffi::VstAudioBuffer;

    /// Common param ids shared by every effect (mirrors `PARAM_ENABLED` / `PARAM_MIX`).
    const PARAM_ENABLED: u32 = 0;
    const PARAM_MIX: u32 = 1;

    /// Plugin stub that copies input planes straight to output planes.
    unsafe extern "C" fn passthrough(_handle: *mut c_void, buffer: *mut VstAudioBuffer) -> bool {
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
    fn add_track_vst_effect_null_mixer_is_null_pointer() {
        let mut effect: *mut OwnAudioEffectHandle = std::ptr::null_mut();
        assert_eq!(
            ownaudio_v1_track_add_vst_effect(
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                Some(passthrough),
                2,
                512,
                0,
                &mut effect,
            ),
            OwnAudioErrorCode::NullPointer as i32
        );
    }

    #[test]
    fn add_master_vst_effect_null_process_fn_is_null_pointer() {
        let mut mixer: *mut OwnAudioMixerHandle = std::ptr::null_mut();
        assert_eq!(
            ownaudio_v1_mixer_create(48_000.0, 2, &mut mixer),
            OwnAudioErrorCode::Success as i32
        );
        let mut effect: *mut OwnAudioEffectHandle = std::ptr::null_mut();
        assert_eq!(
            ownaudio_v1_mixer_add_master_vst_effect(
                mixer,
                std::ptr::null_mut(),
                None,
                2,
                512,
                0,
                &mut effect,
            ),
            OwnAudioErrorCode::NullPointer as i32
        );
        ownaudio_v1_mixer_destroy(mixer);
    }

    #[test]
    fn add_master_vst_effect_param_roundtrip_and_remove() {
        let mut mixer: *mut OwnAudioMixerHandle = std::ptr::null_mut();
        assert_eq!(
            ownaudio_v1_mixer_create(48_000.0, 2, &mut mixer),
            OwnAudioErrorCode::Success as i32
        );

        // A distinct non-null sentinel stands in for the opaque plugin handle;
        // the passthrough stub never dereferences it.
        let fake_handle = 0x1234usize as *mut c_void;
        let mut effect: *mut OwnAudioEffectHandle = std::ptr::null_mut();
        assert_eq!(
            ownaudio_v1_mixer_add_master_vst_effect(
                mixer,
                fake_handle,
                Some(passthrough),
                2,
                512,
                0,
                &mut effect,
            ),
            OwnAudioErrorCode::Success as i32
        );
        assert!(!effect.is_null());

        // The bridge exposes only the shared enabled/mix params; mix roundtrips.
        assert_eq!(
            ownaudio_v1_effect_set_param(mixer, effect, PARAM_MIX, 0.25),
            OwnAudioErrorCode::Success as i32
        );
        let mut value: f32 = 0.0;
        assert_eq!(
            ownaudio_v1_effect_get_param(mixer, effect, PARAM_MIX, &mut value),
            OwnAudioErrorCode::Success as i32
        );
        assert_eq!(value, 0.25);

        // A plugin-specific param id is unknown to the bridge shadow.
        assert_eq!(
            ownaudio_v1_effect_set_param(mixer, effect, 42, 1.0),
            OwnAudioErrorCode::InvalidHandle as i32
        );

        assert_eq!(
            ownaudio_v1_mixer_remove_master_effect(mixer, effect),
            OwnAudioErrorCode::Success as i32
        );
        ownaudio_v1_mixer_destroy(mixer);
    }

    #[test]
    fn add_track_vst_effect_smoke() {
        let mut mixer: *mut OwnAudioMixerHandle = std::ptr::null_mut();
        assert_eq!(
            ownaudio_v1_mixer_create(48_000.0, 2, &mut mixer),
            OwnAudioErrorCode::Success as i32
        );
        let mut track: *mut OwnAudioTrackHandle = std::ptr::null_mut();
        assert_eq!(
            ownaudio_v1_track_create(mixer, &mut track),
            OwnAudioErrorCode::Success as i32
        );

        let fake_handle = 0xBEEFusize as *mut c_void;
        let mut effect: *mut OwnAudioEffectHandle = std::ptr::null_mut();
        assert_eq!(
            ownaudio_v1_track_add_vst_effect(
                mixer,
                track,
                fake_handle,
                Some(passthrough),
                2,
                512,
                0,
                &mut effect,
            ),
            OwnAudioErrorCode::Success as i32
        );
        assert!(!effect.is_null());

        // Enabled defaults to 1.0 (active) and is readable through the shadow.
        let mut value: f32 = -1.0;
        assert_eq!(
            ownaudio_v1_effect_get_param(mixer, effect, PARAM_ENABLED, &mut value),
            OwnAudioErrorCode::Success as i32
        );
        assert_eq!(value, 1.0);

        ownaudio_v1_mixer_destroy(mixer);
    }
}
