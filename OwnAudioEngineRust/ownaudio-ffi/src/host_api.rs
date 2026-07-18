/// Identifies the OS audio host API to use when creating an engine.
///
/// Pass this value to `ownaudio_v1_engine_create_with_host` to select a
/// specific backend instead of the platform default.
///
/// Not all variants are available on every platform or binary build:
/// - `Asio` requires Windows and a build compiled with `--features asio`.
/// - `CoreAudio` covers both macOS and iOS.
/// - `Alsa` is only meaningful on Linux.
/// - `AAudio` is only meaningful on Android (8.0+).
///
/// Requesting an unavailable variant returns
/// [`OwnAudioErrorCode::HostApiNotAvailable`] (10) or
/// [`OwnAudioErrorCode::AsioDriverNotFound`] (11); the call never panics.
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum OwnHostApi {
    /// Windows Audio Session API — the default Windows audio backend.
    Wasapi = 0,
    /// Steinberg ASIO — low-latency Windows audio for professional interfaces.
    /// Requires `--features asio` at build time and an installed ASIO driver.
    Asio = 1,
    /// Apple Core Audio — the default backend on macOS and on iOS.
    CoreAudio = 2,
    /// Advanced Linux Sound Architecture — the default Linux audio backend.
    Alsa = 3,
    /// Android AAudio — the default Android audio backend on 8.0 and up.
    AAudio = 4,
}

/// Resolves `OwnHostApi` to a `cpal::Host`.
///
/// Returns `Err(OwnAudioErrorCode)` as an `i32` when the requested API is
/// unavailable so the FFI export can return the code directly without panicking.
pub(crate) fn resolve_host(api: OwnHostApi) -> Result<cpal::Host, i32> {
    use crate::error_code::OwnAudioErrorCode;

    match api {
        OwnHostApi::Wasapi => {
            #[cfg(target_os = "windows")]
            {
                cpal::host_from_id(cpal::HostId::Wasapi)
                    .map_err(|_| OwnAudioErrorCode::HostApiNotAvailable as i32)
            }
            #[cfg(not(target_os = "windows"))]
            {
                Err(OwnAudioErrorCode::HostApiNotAvailable as i32)
            }
        }

        OwnHostApi::Asio => {
            #[cfg(all(feature = "asio", target_os = "windows"))]
            {
                cpal::host_from_id(cpal::HostId::Asio).map_err(|e| {
                    crate::error_code::set_last_error(e.to_string());
                    OwnAudioErrorCode::AsioDriverNotFound as i32
                })
            }
            #[cfg(not(all(feature = "asio", target_os = "windows")))]
            {
                Err(OwnAudioErrorCode::HostApiNotAvailable as i32)
            }
        }

        OwnHostApi::CoreAudio => {
            #[cfg(target_vendor = "apple")]
            {
                cpal::host_from_id(cpal::HostId::CoreAudio)
                    .map_err(|_| OwnAudioErrorCode::HostApiNotAvailable as i32)
            }
            #[cfg(not(target_vendor = "apple"))]
            {
                Err(OwnAudioErrorCode::HostApiNotAvailable as i32)
            }
        }

        OwnHostApi::Alsa => {
            #[cfg(target_os = "linux")]
            {
                cpal::host_from_id(cpal::HostId::Alsa)
                    .map_err(|_| OwnAudioErrorCode::HostApiNotAvailable as i32)
            }
            #[cfg(not(target_os = "linux"))]
            {
                Err(OwnAudioErrorCode::HostApiNotAvailable as i32)
            }
        }

        OwnHostApi::AAudio => {
            #[cfg(target_os = "android")]
            {
                cpal::host_from_id(cpal::HostId::AAudio)
                    .map_err(|_| OwnAudioErrorCode::HostApiNotAvailable as i32)
            }
            #[cfg(not(target_os = "android"))]
            {
                Err(OwnAudioErrorCode::HostApiNotAvailable as i32)
            }
        }
    }
}
