use cpal::traits::{DeviceTrait, HostTrait};

use crate::error::{AudioError, Result};

/// Snapshot descriptor of an audio device.
///
/// This type intentionally owns its data (no lifetimes, no Cpal types) so
/// it can be sent across threads and eventually marshalled over FFI.
#[derive(Debug, Clone)]
pub struct AudioDeviceInfo {
    /// Human-readable device name reported by the OS.
    pub name: String,
    /// Whether this device is the current system default for output.
    pub is_default_output: bool,
    /// Whether this device is the current system default for input.
    pub is_default_input: bool,
    /// Maximum number of output channels the device supports.
    pub max_output_channels: u16,
    /// Maximum number of input channels the device supports.
    pub max_input_channels: u16,
    /// The device's preferred sample rate (from its default config).
    pub default_sample_rate: u32,
}

/// Returns a list of all available output devices on the default host.
pub fn list_output_devices() -> Result<Vec<AudioDeviceInfo>> {
    list_output_devices_on(&cpal::default_host())
}

/// Returns a list of all available input devices on the default host.
pub fn list_input_devices() -> Result<Vec<AudioDeviceInfo>> {
    list_input_devices_on(&cpal::default_host())
}

/// Returns a list of all available output devices on the given host.
///
/// Used by [`crate::AudioEngine::list_output_devices`] so that an engine
/// created for a non-default host API (e.g. ASIO on Windows) enumerates the
/// devices of that host instead of the platform default one.
pub fn list_output_devices_on(host: &cpal::Host) -> Result<Vec<AudioDeviceInfo>> {
    let default_name = host
        .default_output_device()
        .and_then(|d| d.description().map(|desc| desc.name().to_owned()).ok());

    let mut devices = Vec::new();
    for device in host.output_devices()? {
        if let Ok(info) = device_to_output_info(&device, default_name.as_deref()) {
            devices.push(info);
        }
    }
    Ok(devices)
}

/// Returns a list of all available input devices on the given host.
///
/// Used by [`crate::AudioEngine::list_input_devices`] so that an engine
/// created for a non-default host API (e.g. ASIO on Windows) enumerates the
/// devices of that host instead of the platform default one.
pub fn list_input_devices_on(host: &cpal::Host) -> Result<Vec<AudioDeviceInfo>> {
    let default_name = host
        .default_input_device()
        .and_then(|d| d.description().map(|desc| desc.name().to_owned()).ok());

    let mut devices = Vec::new();
    for device in host.input_devices()? {
        if let Ok(info) = device_to_input_info(&device, default_name.as_deref()) {
            devices.push(info);
        }
    }
    Ok(devices)
}

/// Returns info for the system default output device.
pub fn default_output_device() -> Result<AudioDeviceInfo> {
    let host = cpal::default_host();
    let device = host
        .default_output_device()
        .ok_or(AudioError::DeviceNotFound)?;
    device_to_output_info(&device, None)
}

/// Returns info for the system default input device.
pub fn default_input_device() -> Result<AudioDeviceInfo> {
    let host = cpal::default_host();
    let device = host
        .default_input_device()
        .ok_or(AudioError::DeviceNotFound)?;
    device_to_input_info(&device, None)
}

// ---------------------------------------------------------------------------
// Internal helpers
// ---------------------------------------------------------------------------

/// Selects an output `cpal::Device` by name using the given host, or falls back to the default.
///
/// Used by `engine.rs`; callers outside this crate never see `cpal::Device`.
pub(crate) fn resolve_output_device(host: &cpal::Host, name: Option<&str>) -> Result<cpal::Device> {
    match name {
        None => host
            .default_output_device()
            .ok_or(AudioError::DeviceNotFound),
        Some(target) => host
            .output_devices()?
            .find(|d| {
                d.description()
                    .map(|desc| desc.name() == target)
                    .unwrap_or(false)
            })
            .ok_or(AudioError::DeviceNotFound),
    }
}

/// Selects an input `cpal::Device` by name using the given host, or falls back to the default.
pub(crate) fn resolve_input_device(host: &cpal::Host, name: Option<&str>) -> Result<cpal::Device> {
    match name {
        None => host
            .default_input_device()
            .ok_or(AudioError::DeviceNotFound),
        Some(target) => host
            .input_devices()?
            .find(|d| {
                d.description()
                    .map(|desc| desc.name() == target)
                    .unwrap_or(false)
            })
            .ok_or(AudioError::DeviceNotFound),
    }
}

fn device_to_output_info(
    device: &cpal::Device,
    default_name: Option<&str>,
) -> Result<AudioDeviceInfo> {
    let name = device.description()?.name().to_owned();
    let is_default = default_name.map(|n| n == name).unwrap_or(false);

    let max_channels = device
        .supported_output_configs()?
        .map(|c| c.channels())
        .max()
        .unwrap_or(0);

    let sample_rate = device
        .default_output_config()
        .map(|c| c.sample_rate())
        .unwrap_or(0);

    Ok(AudioDeviceInfo {
        name,
        is_default_output: is_default,
        is_default_input: false,
        max_output_channels: max_channels,
        max_input_channels: 0,
        default_sample_rate: sample_rate,
    })
}

fn device_to_input_info(
    device: &cpal::Device,
    default_name: Option<&str>,
) -> Result<AudioDeviceInfo> {
    let name = device.description()?.name().to_owned();
    let is_default = default_name.map(|n| n == name).unwrap_or(false);

    let max_channels = device
        .supported_input_configs()?
        .map(|c| c.channels())
        .max()
        .unwrap_or(0);

    let sample_rate = device
        .default_input_config()
        .map(|c| c.sample_rate())
        .unwrap_or(0);

    Ok(AudioDeviceInfo {
        name,
        is_default_output: false,
        is_default_input: is_default,
        max_output_channels: 0,
        max_input_channels: max_channels,
        default_sample_rate: sample_rate,
    })
}
