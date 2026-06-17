use std::ffi::CString;
use std::os::raw::c_char;

use ownaudio_core::{list_input_devices, list_output_devices};

use crate::error_code::{set_last_error, OwnAudioErrorCode};

/// Snapshot descriptor of an audio device, passed to the C# caller.
///
/// The `name` field is a null-terminated UTF-8 string allocated by Rust.
/// The caller must **not** free `name` directly.  Pass the entire array to
/// `ownaudio_v1_free_device_list` when done.
#[repr(C)]
pub struct OwnAudioDeviceInfo {
    /// Human-readable device name (null-terminated UTF-8, Rust-owned).
    pub name: *const c_char,
    /// Whether this device is the current system default for input.
    pub is_default_input: bool,
    /// Whether this device is the current system default for output.
    pub is_default_output: bool,
    /// Maximum number of input channels supported by this device.
    pub max_input_channels: u16,
    /// Maximum number of output channels supported by this device.
    pub max_output_channels: u16,
    /// The device's preferred sample rate in Hz.
    pub default_sample_rate: u32,
}

/// Lists all available output devices on the default host.
///
/// On success, `*out_devices` points to a Rust-owned array of
/// `*out_count` elements.  The caller must release it with
/// `ownaudio_v1_free_device_list(*out_devices, *out_count)`.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_list_output_devices(
    out_devices: *mut *mut OwnAudioDeviceInfo,
    out_count: *mut usize,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_devices.is_null() || out_count.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        match list_output_devices() {
            Ok(devices) => {
                let (ptr, count) = devices_to_c(devices);
                unsafe {
                    *out_devices = ptr;
                    *out_count = count;
                }
                OwnAudioErrorCode::Success as i32
            }
            Err(e) => {
                set_last_error(e.to_string());
                OwnAudioErrorCode::from(e) as i32
            }
        }
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Lists all available input devices on the default host.
///
/// On success, `*out_devices` points to a Rust-owned array of
/// `*out_count` elements.  The caller must release it with
/// `ownaudio_v1_free_device_list(*out_devices, *out_count)`.
///
/// Returns `OwnAudioErrorCode::Success` (0) on success.
#[no_mangle]
pub extern "C" fn ownaudio_v1_list_input_devices(
    out_devices: *mut *mut OwnAudioDeviceInfo,
    out_count: *mut usize,
) -> i32 {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        if out_devices.is_null() || out_count.is_null() {
            return OwnAudioErrorCode::NullPointer as i32;
        }

        match list_input_devices() {
            Ok(devices) => {
                let (ptr, count) = devices_to_c(devices);
                unsafe {
                    *out_devices = ptr;
                    *out_count = count;
                }
                OwnAudioErrorCode::Success as i32
            }
            Err(e) => {
                set_last_error(e.to_string());
                OwnAudioErrorCode::from(e) as i32
            }
        }
    }));

    result.unwrap_or(OwnAudioErrorCode::InternalPanic as i32)
}

/// Releases an array previously returned by `ownaudio_v1_list_output_devices`
/// or `ownaudio_v1_list_input_devices`.
///
/// Passing `null` or `count = 0` is safe and has no effect.
#[no_mangle]
pub extern "C" fn ownaudio_v1_free_device_list(devices: *mut OwnAudioDeviceInfo, count: usize) {
    if devices.is_null() || count == 0 {
        return;
    }

    // SAFETY: `devices` was allocated by `devices_to_c` using Vec::into_raw_parts
    // logic (Box::into_raw on a slice), and each `name` was allocated by
    // CString::into_raw.
    unsafe {
        let slice = std::slice::from_raw_parts_mut(devices, count);
        for info in slice.iter_mut() {
            if !info.name.is_null() {
                // Reclaim and drop the CString that owns this pointer.
                drop(CString::from_raw(info.name as *mut c_char));
                info.name = std::ptr::null();
            }
        }
        // Reconstruct the Vec and let it drop to free the array.
        drop(Vec::from_raw_parts(devices, count, count));
    }
}

// ---------------------------------------------------------------------------
// Internal helper
// ---------------------------------------------------------------------------

fn devices_to_c(
    devices: Vec<ownaudio_core::AudioDeviceInfo>,
) -> (*mut OwnAudioDeviceInfo, usize) {
    let mut c_vec: Vec<OwnAudioDeviceInfo> = devices
        .into_iter()
        .map(|d| {
            let name_cstr = CString::new(d.name).unwrap_or_else(|_| {
                // Replace embedded NUL bytes with a placeholder.
                CString::new("<invalid>").unwrap()
            });
            OwnAudioDeviceInfo {
                name: name_cstr.into_raw(),
                is_default_input: d.is_default_input,
                is_default_output: d.is_default_output,
                max_input_channels: d.max_input_channels,
                max_output_channels: d.max_output_channels,
                default_sample_rate: d.default_sample_rate,
            }
        })
        .collect();

    let count = c_vec.len();
    // Shrink the vec so that len == capacity before transferring ownership,
    // allowing `Vec::from_raw_parts(ptr, count, count)` in the free function.
    c_vec.shrink_to_fit();
    let ptr = c_vec.as_mut_ptr();
    std::mem::forget(c_vec);

    (ptr, count)
}
