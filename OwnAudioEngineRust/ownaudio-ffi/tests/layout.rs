//! ABI layout parity tests — the authoritative source of truth for the
//! `#[repr(C)]` memory layout of every type that crosses the FFI boundary.
//!
//! These tests hardcode the expected size, alignment and field offsets of each
//! FFI struct and enum. The C# side mirrors the very same numbers in
//! `FfiStructLayoutParityTests` (`Ownaudio.Test.Engine`). If either side ever
//! drifts — a reordered field, a changed type, an inserted padding byte — one
//! of the two test suites fails immediately, before any silent memory
//! corruption can reach a release build.
//!
//! Pointer-width dependent layouts (`OwnAudioDeviceInfo`) are computed from
//! `size_of::<*const c_char>()` so the assertions hold on both 64-bit and
//! 32-bit targets.

use std::mem::{align_of, offset_of, size_of};
use std::os::raw::c_char;

use ownaudio_ffi::{
    error_code::OwnAudioErrorCode,
    ffi_config::{OwnAudioSampleFormat, OwnAudioStreamConfig},
    ffi_device::OwnAudioDeviceInfo,
    host_api::OwnHostApi,
};

/// Native pointer width in bytes on the current target (8 on 64-bit, 4 on 32-bit).
const PTR: usize = size_of::<*const c_char>();

// ---------------------------------------------------------------------------
// OwnAudioStreamConfig — fixed 16-byte layout on every target
// ---------------------------------------------------------------------------

#[test]
fn stream_config_size_and_align() {
    assert_eq!(size_of::<OwnAudioStreamConfig>(), 16, "OwnAudioStreamConfig size");
    assert_eq!(align_of::<OwnAudioStreamConfig>(), 4, "OwnAudioStreamConfig align");
}

#[test]
fn stream_config_field_offsets() {
    assert_eq!(offset_of!(OwnAudioStreamConfig, sample_rate), 0, "sample_rate");
    assert_eq!(offset_of!(OwnAudioStreamConfig, channels), 4, "channels");
    // 2 bytes implicit padding at offset 6 to align the i32 enum on a 4-byte boundary.
    assert_eq!(offset_of!(OwnAudioStreamConfig, sample_format), 8, "sample_format");
    assert_eq!(offset_of!(OwnAudioStreamConfig, buffer_size_frames), 12, "buffer_size_frames");
}

// ---------------------------------------------------------------------------
// OwnAudioDeviceInfo — pointer-width dependent layout
// ---------------------------------------------------------------------------

#[test]
fn device_info_size_and_align() {
    // name(ptr) + bool + bool + u16 + u16 + pad(2) + u32, rounded up to ptr align.
    let expected_size = if PTR == 8 { 24 } else { 16 };
    assert_eq!(size_of::<OwnAudioDeviceInfo>(), expected_size, "OwnAudioDeviceInfo size");
    assert_eq!(align_of::<OwnAudioDeviceInfo>(), PTR, "OwnAudioDeviceInfo align");
}

#[test]
fn device_info_field_offsets() {
    assert_eq!(offset_of!(OwnAudioDeviceInfo, name), 0, "name");
    assert_eq!(offset_of!(OwnAudioDeviceInfo, is_default_input), PTR, "is_default_input");
    assert_eq!(offset_of!(OwnAudioDeviceInfo, is_default_output), PTR + 1, "is_default_output");
    assert_eq!(offset_of!(OwnAudioDeviceInfo, max_input_channels), PTR + 2, "max_input_channels");
    assert_eq!(offset_of!(OwnAudioDeviceInfo, max_output_channels), PTR + 4, "max_output_channels");
    // 2 bytes implicit padding at PTR + 6 to align the u32 on a 4-byte boundary.
    assert_eq!(offset_of!(OwnAudioDeviceInfo, default_sample_rate), PTR + 8, "default_sample_rate");
}

// ---------------------------------------------------------------------------
// repr(C) enums — each is exactly one C int wide (4 bytes on every target)
// ---------------------------------------------------------------------------

#[test]
fn sample_format_is_c_int_wide() {
    assert_eq!(size_of::<OwnAudioSampleFormat>(), 4, "OwnAudioSampleFormat size");
    assert_eq!(align_of::<OwnAudioSampleFormat>(), 4, "OwnAudioSampleFormat align");
}

#[test]
fn error_code_is_c_int_wide() {
    assert_eq!(size_of::<OwnAudioErrorCode>(), 4, "OwnAudioErrorCode size");
    assert_eq!(align_of::<OwnAudioErrorCode>(), 4, "OwnAudioErrorCode align");
}

#[test]
fn host_api_is_c_int_wide() {
    assert_eq!(size_of::<OwnHostApi>(), 4, "OwnHostApi size");
    assert_eq!(align_of::<OwnHostApi>(), 4, "OwnHostApi align");
}

// ---------------------------------------------------------------------------
// Enum discriminant values — must match the C# mirror exactly
// ---------------------------------------------------------------------------

#[test]
fn sample_format_discriminants() {
    assert_eq!(OwnAudioSampleFormat::F32 as i32, 0);
    assert_eq!(OwnAudioSampleFormat::I16 as i32, 1);
    assert_eq!(OwnAudioSampleFormat::U16 as i32, 2);
}

#[test]
fn error_code_discriminants() {
    assert_eq!(OwnAudioErrorCode::Success as i32, 0);
    assert_eq!(OwnAudioErrorCode::DeviceNotFound as i32, 1);
    assert_eq!(OwnAudioErrorCode::DeviceEnumerationFailed as i32, 2);
    assert_eq!(OwnAudioErrorCode::UnsupportedConfig as i32, 3);
    assert_eq!(OwnAudioErrorCode::StreamBuildFailed as i32, 4);
    assert_eq!(OwnAudioErrorCode::StreamControlFailed as i32, 5);
    assert_eq!(OwnAudioErrorCode::NullPointer as i32, 6);
    assert_eq!(OwnAudioErrorCode::InvalidHandle as i32, 7);
    assert_eq!(OwnAudioErrorCode::InternalPanic as i32, 8);
    assert_eq!(OwnAudioErrorCode::InternalError as i32, 9);
    assert_eq!(OwnAudioErrorCode::HostApiNotAvailable as i32, 10);
    assert_eq!(OwnAudioErrorCode::AsioDriverNotFound as i32, 11);
}

#[test]
fn host_api_discriminants() {
    assert_eq!(OwnHostApi::Wasapi as i32, 0);
    assert_eq!(OwnHostApi::Asio as i32, 1);
    assert_eq!(OwnHostApi::CoreAudio as i32, 2);
    assert_eq!(OwnHostApi::Alsa as i32, 3);
}
