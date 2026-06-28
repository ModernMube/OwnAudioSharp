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
        ownaudio_v1_mixer_create, ownaudio_v1_mixer_destroy, ownaudio_v1_mixer_pause_all,
        ownaudio_v1_mixer_play_all, ownaudio_v1_mixer_stop_all, ownaudio_v1_track_create,
        ownaudio_v1_track_destroy,
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
