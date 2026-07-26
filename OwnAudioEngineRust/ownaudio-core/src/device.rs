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
    Ok(collect_output_devices(host)?
        .into_iter()
        .map(|(_, info)| info)
        .collect())
}

/// Same enumeration, but hands back the `cpal::Device` next to each snapshot.
///
/// [`crate::AudioEngine`] keeps those devices so that opening a stream by name later does not
/// have to enumerate again. On ASIO that is the difference between working and not: enumeration
/// loads every driver to read its properties, and a driver will not load while another one is
/// in use, so the list comes back short — or empty — once any stream is open.
pub(crate) fn collect_output_devices(
    host: &cpal::Host,
) -> Result<Vec<(cpal::Device, AudioDeviceInfo)>> {
    let default_name = host
        .default_output_device()
        .and_then(|d| d.description().map(|desc| desc.name().to_owned()).ok());

    let Some(enumerated) = enumerate(|| host.output_devices()) else {
        return default_output_only(host);
    };

    let mut devices = Vec::new();
    for device in enumerated? {
        if let Ok(info) = device_to_output_info(&device, default_name.as_deref()) {
            devices.push((device, info));
        }
    }
    Ok(devices)
}

/// Everything the platform default device can tell us without walking the device list.
///
/// Android only: see [`enumerate`] for why we end up here.
fn default_output_only(host: &cpal::Host) -> Result<Vec<(cpal::Device, AudioDeviceInfo)>> {
    let Some(device) = host.default_output_device() else {
        return Ok(Vec::new());
    };

    let name = device
        .description()
        .map(|d| d.name().to_owned())
        .unwrap_or_else(|_| "Default Device".to_owned());
    let info = device_to_output_info(&device, Some(&name)).unwrap_or(AudioDeviceInfo {
        name,
        is_default_output: true,
        is_default_input: false,
        max_output_channels: 2,
        max_input_channels: 0,
        default_sample_rate: 48_000,
    });

    Ok(vec![(device, info)])
}

/// Same, for capture.
fn default_input_only(host: &cpal::Host) -> Result<Vec<(cpal::Device, AudioDeviceInfo)>> {
    let Some(device) = host.default_input_device() else {
        return Ok(Vec::new());
    };

    let name = device
        .description()
        .map(|d| d.name().to_owned())
        .unwrap_or_else(|_| "Default Device".to_owned());
    let info = device_to_input_info(&device, Some(&name)).unwrap_or(AudioDeviceInfo {
        name,
        is_default_output: false,
        is_default_input: true,
        max_output_channels: 0,
        max_input_channels: 1,
        default_sample_rate: 48_000,
    });

    Ok(vec![(device, info)])
}

/// Runs a cpal device enumeration, returning `None` when the platform cannot do it.
///
/// Only Android can answer `None`: cpal walks the device list through
/// `AudioManager.getDevices()` over JNI, and the JNI handles come from
/// `ndk_context`, which *panics* when nothing has initialised it. An app that
/// loads us through P/Invoke — every .NET Android app — has no ndk-glue and no
/// `JNI_OnLoad`, so unless it called `ownaudio_v1_android_init` first, this is a
/// guaranteed panic rather than an error. Catching it keeps playback working on
/// the default device instead of failing initialisation outright.
#[cfg(target_os = "android")]
fn enumerate<T>(list: impl FnOnce() -> T) -> Option<T> {
    std::panic::catch_unwind(std::panic::AssertUnwindSafe(list)).ok()
}

/// Everywhere else enumeration is a plain call that either works or returns an error.
#[cfg(not(target_os = "android"))]
fn enumerate<T>(list: impl FnOnce() -> T) -> Option<T> {
    Some(list())
}

/// Returns a list of all available input devices on the given host.
///
/// Used by [`crate::AudioEngine::list_input_devices`] so that an engine
/// created for a non-default host API (e.g. ASIO on Windows) enumerates the
/// devices of that host instead of the platform default one.
pub fn list_input_devices_on(host: &cpal::Host) -> Result<Vec<AudioDeviceInfo>> {
    Ok(collect_input_devices(host)?
        .into_iter()
        .map(|(_, info)| info)
        .collect())
}

/// Capture side counterpart of [`collect_output_devices`].
pub(crate) fn collect_input_devices(
    host: &cpal::Host,
) -> Result<Vec<(cpal::Device, AudioDeviceInfo)>> {
    let default_name = host
        .default_input_device()
        .and_then(|d| d.description().map(|desc| desc.name().to_owned()).ok());

    let Some(enumerated) = enumerate(|| host.input_devices()) else {
        return default_input_only(host);
    };

    let mut devices = Vec::new();
    for device in enumerated? {
        if let Ok(info) = device_to_input_info(&device, default_name.as_deref()) {
            devices.push((device, info));
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

// Internal helpers

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

/// Resolves a single device from the combined enumeration, so an ASIO driver's capture and
/// playback can be opened on the *same* `cpal::Device` instance — the only way cpal merges them
/// into one duplex ASIO stream. `None` picks the first (default) driver. Must be called while no
/// stream is open, since ASIO cannot enumerate once a driver is loaded.
pub(crate) fn resolve_duplex_device(host: &cpal::Host, name: Option<&str>) -> Result<cpal::Device> {
    match name {
        None => host.devices()?.next().ok_or(AudioError::DeviceNotFound),
        Some(target) => host
            .devices()?
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
